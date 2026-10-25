using System.Collections.Concurrent;
using System.Text.Json;
using Fleck;
using Liuzhuan.Models;
using Liuzhuan.Utils;

namespace Liuzhuan.Services.Lan;

/// <summary>
/// WebSocket 会话管理 — 基于 Fleck
/// 处理：握手认证 / 心跳 / sync_text / clipboard_push
/// </summary>
public class WsHub
{
    private WebSocketServer? _server;
    private readonly ConcurrentDictionary<string, IWebSocketConnection> _sessions = new();
    private readonly ConcurrentDictionary<string, string> _sessionDevices = new();
    private readonly ConcurrentDictionary<string, byte> _microphonePending = new();
    private readonly ConcurrentDictionary<string, long> _microphoneLastRequest = new();
    public Func<Task<Dictionary<string, object?>>>? MicrophoneInviteProvider { get; set; }
    public Func<string,Task<Dictionary<string,object?>>>? MicrophoneConfigurationProvider { get; set; }
    private readonly ConcurrentDictionary<string,byte> _configurationPending = new();
    private readonly ConcurrentDictionary<string,long> _configurationLast = new();
    public Func<JsonElement, object>? AutomationProvider { get; set; }
    private readonly ConcurrentDictionary<string, long> _automationLast = new();
    public Func<string,string,string,Task>? DurableTextReceiver {get;set;}
    public int ResumePort {get;set;}
    public string ServerId {get;set;}="";
    public string DeviceId {get;set;}="";
    public string LibraryEpoch {get;}=Guid.NewGuid().ToString("D");
    public Func<string>? DeviceName {get;set;}
    public Func<JsonElement,Task<object>>? LibraryProvider {get;set;}
    private readonly ConcurrentDictionary<string,byte> _libraryPending=new();
    private DurableInbox? _durableInbox;
    private readonly ConcurrentDictionary<string,string> _clientIds=new();
    private readonly object _receiptGate = new();
    private readonly Dictionary<string, TextReceipt> _textReceipts = new();
    private sealed record TextReceipt(string Fingerprint, DateTime CreatedAt, Lazy<bool> Registered);
    private const int MaxTextReceipts = 2048;
    private static readonly TimeSpan TextReceiptLifetime = TimeSpan.FromMinutes(10);

    /// <summary>收到手机文字素材（文本内容, 来源设备）</summary>
    public event Action<string, string>? TextReceived;

    /// <summary>会话数变化（设备数）</summary>
    public event Action<int>? ClientCountChanged;
    public event Action? ClientAuthenticated;

    /// <summary>最近素材摘要提供者（list_sync 用），由外部注入</summary>
    public Func<List<LanItemSummary>>? ListProvider { get; set; }
    public Func<LanListSnapshot>? SnapshotProvider { get; set; }
    /// <summary>Capture and enqueue the reply on the same owner thread as material broadcasts.</summary>
    public Action<Action>? SnapshotDispatcher { get; set; }

    /// <summary>素材详情提供者（get_item 用）：id → 素材</summary>
    public Func<string, MaterialItem?>? ItemLookup { get; set; }

    public int ClientCount => _sessions.Count;

    /// <summary>获取已连接设备列表（设备名, IP）</summary>
    public List<(string Device, string Ip)> GetDevices()
    {
        var list = new List<(string, string)>();
        foreach (var kv in _sessions)
        {
            var device = _sessionDevices.TryGetValue(kv.Key, out var d) ? d : "unknown";
            var ip = "?";
            try { ip = kv.Value.ConnectionInfo.ClientIpAddress; } catch { }
            list.Add((device, ip));
        }
        return list;
    }

    public void Start(int port, string passwordHash)
    {
        try
        {
            _durableInbox ??= new DurableInbox(ConfigService.GetEffectiveDataDir());
            _server = new WebSocketServer($"ws://0.0.0.0:{port}");
            _server.RestartAfterListenError = true;
            _server.Start(socket =>
            {
                socket.OnOpen = () => { /* 认证在消息层做 */ };
                socket.OnMessage = message => HandleMessage(socket, message, passwordHash);
                socket.OnClose = () => RemoveSession(socket);
                socket.OnError = ex => Logger.Error("Lan: ws error: {0}", ex.Message);
            });
            Logger.Run("LanServer: WS listening on port {0}", port);
        }
        catch (Exception ex)
        {
            Logger.Error("LanServer.Start failed: {0}", ex.Message);
            throw;
        }
    }

    public void Stop()
    {
        try
        {
            _server?.Dispose();
            _server = null;
            _sessions.Clear();
            _sessionDevices.Clear();
            _clientIds.Clear();
            _microphoneLastRequest.Clear();
            _configurationLast.Clear();
            _automationLast.Clear();
            Logger.Run("LanServer: stopped");
        }
        catch (Exception ex)
        {
            Logger.Error("LanServer.Stop failed: {0}", ex.Message);
        }
    }

    private void HandleMessage(IWebSocketConnection socket, string message, string passwordHash)
    {
        try
        {
            var msg = JsonSerializer.Deserialize<LanMessage>(message);
            if (msg == null || string.IsNullOrEmpty(msg.Type)) return;

            // All feature messages require the live authenticated socket, including future
            // message types. A client-provided device/session name never grants access.
            if (msg.Type is not ("hello" or "heartbeat" or "bye") && !IsAuthenticated(socket)) return;

            switch (msg.Type)
            {
                case "library_request":
                    _ = HandleLibraryAsync(socket,msg,message.Length);
                    break;
                case "automation_request":
                    HandleAutomation(socket, msg, message.Length);
                    break;
                case "hello":
                    HandleHello(socket, msg, passwordHash);
                    break;

                case "heartbeat":
                    Send(socket, new LanMessage { Type = "heartbeat" });
                    break;

                case "sync_text":
                case "clipboard_push":
                    _ = HandleTextDurableAsync(socket,msg);
                    break;

                case "list_sync":
                    HandleListSync(socket);
                    break;

                case "get_item":
                    HandleGetItem(socket, msg);
                    break;

                case LanMessage.MicrophoneInviteRequest:
                    _ = HandleMicrophoneInviteAsync(socket, msg);
                    break;

                case LanMessage.MicrophoneConfigurationRequest:
                    _ = HandleMicrophoneConfigurationAsync(socket, msg, message.Length);
                    break;

                case "bye":
                    socket.Close();
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Lan: handle message failed: {0}", ex.Message);
        }
    }

    private void HandleHello(IWebSocketConnection socket, LanMessage msg, string passwordHash)
    {
        var auth = msg.Data?.GetValueOrDefault("auth")?.ToString() ?? "";
        var ts = msg.Data?.GetValueOrDefault("ts")?.ToString() ?? "";
        var device = msg.Device ?? "unknown";

        var (ok, reason) = AuthService.Validate(auth, ts);
        if (!ok)
        {
            Send(socket, new LanMessage
            {
                Type = "auth_fail",
                Data = new Dictionary<string, object?> { ["reason"] = reason }
            });
            socket.Close();
            return;
        }

        var rawClientId=msg.Data?.GetValueOrDefault("clientId")?.ToString()??"";
        var clientId=Guid.TryParse(rawClientId,out var parsedClientId)?parsedClientId.ToString("D"):"";
        var sessionId = Guid.NewGuid().ToString("N");
        // New peers reconnect by local UUID; identical friendly names on two phones may coexist.
        var oldKey = clientId.Length>0 ? _clientIds.FirstOrDefault(kv=>kv.Value==clientId).Key
            : _sessionDevices.FirstOrDefault(kv => kv.Value == device && !_clientIds.ContainsKey(kv.Key)).Key;
        if (oldKey != null)
        {
            try { _sessions[oldKey]?.Close(); } catch { }
            _sessions.TryRemove(oldKey, out _);
            _sessionDevices.TryRemove(oldKey, out _);
            _clientIds.TryRemove(oldKey,out _);
            _automationLast.TryRemove(oldKey, out _);
            _configurationLast.TryRemove(oldKey, out _);
            Logger.Run("Lan: replaced old session for device {0}", device);
        }
        _sessions[sessionId] = socket;
        _sessionDevices[sessionId] = device;
        if(clientId.Length>0)_clientIds[sessionId]=clientId;
        Send(socket, new LanMessage
        {
            Type = "welcome",
            Data = new Dictionary<string, object?>
            {
                ["serverVersion"] = "1.2", ["serverId"]=ServerId, ["resumePort"]=ResumePort,
                ["deviceId"]=DeviceId, ["deviceName"]=DeviceName?.Invoke()??"", ["libraryId"]=ServerId, ["libraryEpoch"]=LibraryEpoch,
                ["features"] = new[] { "text", "file", LanMessage.ClipboardReceiptsFeature }
                    .Concat(MicrophoneInviteProvider == null ? Array.Empty<string>() : new[] { LanMessage.MicrophonePairingFeature })
                    .Concat(MicrophoneConfigurationProvider == null ? Array.Empty<string>() : new[] { LanMessage.MicrophoneConfigurationFeature })
                    .Concat(DurableTextReceiver==null?Array.Empty<string>():new[]{"durable_receipts_v1"})
                    .Concat(ResumePort==0?Array.Empty<string>():new[]{"resumable_transfer_v1"})
                    .Concat(AutomationProvider == null ? Array.Empty<string>() : new[] { "automation_v1" })
                    .Concat(LibraryProvider==null || ServerId.Length==0 ? Array.Empty<string>() : new[]{"material_library_v1",LanMessage.LibraryEditFeature,LanMessage.LibraryChangesFeature,LanMessage.ProjectPackageFeature})
                    .Concat(DeviceId.Length==0?Array.Empty<string>():new[]{"device_identity_v1"}).ToArray(),
                ["sessionId"] = sessionId
            }
        });
        Logger.Run("Lan: device connected: {0} ({1})", device, socket.ConnectionInfo.ClientIpAddress);
        ClientCountChanged?.Invoke(_sessions.Count);
        ClientAuthenticated?.Invoke();
    }

    private async Task HandleLibraryAsync(IWebSocketConnection socket,LanMessage message,int length)
    {
        var session=_sessions.FirstOrDefault(x=>ReferenceEquals(x.Value,socket)).Key;
        if(session==null || LibraryProvider==null || !Guid.TryParse(message.Id,out _))return;
        if(length>262144){Send(socket,new LanMessage{Type="library_result",Data=new(){["requestId"]=message.Id,["result"]=new{error="整理请求过大，请减少选择条数。"}}});return;}
        if(!_libraryPending.TryAdd(session,0)) {Send(socket,new LanMessage{Type="library_result",Data=new(){["requestId"]=message.Id,["result"]=new{error="正在处理素材库请求，请稍后重试。"}}});return;}
        try {
            object result;
            try {
                var request=JsonSerializer.SerializeToElement(message.Data);
                if(request.TryGetProperty("expectedDeviceId",out var expected)&&(expected.ValueKind!=JsonValueKind.String||expected.GetString()!=DeviceId))
                    result=new{error="原电脑身份不匹配，已停止操作。",code="target_changed"};
                else result=await LibraryProvider(request);
            }
            catch(Exception ex){result=new{error=ex is ArgumentException or InvalidOperationException?ex.Message:"素材库操作未能完成，请刷新并检查电脑存储。"};}
            if(_sessions.TryGetValue(session,out var live)&&ReferenceEquals(live,socket))
                Send(socket,new LanMessage{Type="library_result",Data=new(){["requestId"]=message.Id,["result"]=JsonSerializer.SerializeToElement(result,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase})}});
        } finally {_libraryPending.TryRemove(session,out _);}
    }
    public void BroadcastItemUpdated(LanItemSummary item) => Broadcast(new LanMessage {Type="item_updated",Data=JsonSerializer.Deserialize<Dictionary<string,object?>>(JsonSerializer.Serialize(item,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase}))});

    private void HandleAutomation(IWebSocketConnection socket, LanMessage message, int length)
    {
        var session = _sessions.FirstOrDefault(x => ReferenceEquals(x.Value, socket)).Key;
        if (session == null || AutomationProvider == null || string.IsNullOrEmpty(message.Id) || message.Id.Length > 128) return;
        var now = Environment.TickCount64;
        object reply;
        try
        {
            if (length > 65536) throw new ArgumentException("规则数据过大。");
            if (_automationLast.TryGetValue(session, out var last) && now - last < 300) throw new ArgumentException("操作过快，请稍后重试。");
            _automationLast[session] = now;
            reply = AutomationProvider(JsonSerializer.SerializeToElement(message.Data));
        }
        catch (Exception ex) { reply = new { error = ex is ArgumentException or InvalidOperationException ? ex.Message : "规则操作失败，请重试或在电脑检查配置。" }; }
        if (_sessions.TryGetValue(session, out var active) && ReferenceEquals(active, socket))
            Send(socket, new LanMessage { Type = "automation_result", Data = new() { ["requestId"] = message.Id, ["result"] = JsonSerializer.SerializeToElement(reply, new JsonSerializerOptions { PropertyNamingPolicy=JsonNamingPolicy.CamelCase }) } });
    }

    private async Task HandleMicrophoneConfigurationAsync(IWebSocketConnection socket, LanMessage message, int length)
    {
        var session = _sessions.FirstOrDefault(x => ReferenceEquals(x.Value, socket)).Key;
        if (session == null || !Guid.TryParseExact(message.Id, "D", out _) || length > 2048) return;
        if (!_configurationPending.TryAdd(session, 0))
        {
            if (IsAuthenticated(socket)) Send(socket, new LanMessage { Type = LanMessage.MicrophoneConfigurationReply,
                Data = new() { ["requestId"] = message.Id, ["status"] = "busy", ["message"] = "电脑正在检查音频，请稍后重试。", ["canRepair"] = false } });
            return;
        }
        try
        {
            Dictionary<string,object?> result;
            var action = message.Data?.GetValueOrDefault("action")?.ToString();
            long now = Environment.TickCount64;
            if (action is not ("check" or "repair"))
                result = new() { ["status"] = "failed", ["message"] = "未知音频配置操作。", ["canRepair"] = false };
            else if (_configurationLast.TryGetValue(session, out long last) && now - last < 2000)
                result = new() { ["status"] = "busy", ["message"] = "操作过快，请稍后重试。", ["canRepair"] = false };
            else
            {
                _configurationLast[session] = now;
                // Check the exact live authenticated socket again before invoking a configuration action.
                if (!IsAuthenticated(socket)) return;
                result = MicrophoneConfigurationProvider == null ? new() { ["status"] = "failed", ["message"] = "电脑版本尚不支持音频修复。", ["canRepair"] = false }
                    : await MicrophoneConfigurationProvider(action);
            }
            if (_sessions.TryGetValue(session, out var live) && ReferenceEquals(live, socket))
            {
                result["requestId"] = message.Id;
                Send(socket, new LanMessage { Type = LanMessage.MicrophoneConfigurationReply, Id = message.Id, Data = result });
            }
        }
        catch
        {
            if (IsAuthenticated(socket)) try { Send(socket, new LanMessage { Type = LanMessage.MicrophoneConfigurationReply,
                Data = new() { ["requestId"] = message.Id, ["status"] = "failed", ["message"] = "电脑音频配置未能完成，请稍后重试。", ["canRepair"] = false } }); } catch { }
        }
        finally { _configurationPending.TryRemove(session, out _); if (!_sessions.ContainsKey(session)) _configurationLast.TryRemove(session, out _); }
    }

    private async Task HandleMicrophoneInviteAsync(IWebSocketConnection socket, LanMessage message)
    {
        // Only the authenticated socket is authority. Client-supplied device/session names are ignored.
        var session = _sessions.FirstOrDefault(kv => ReferenceEquals(kv.Value, socket)).Key;
        if (session == null || string.IsNullOrEmpty(message.Id) || message.Id.Length > 128) return;
        if (!_microphonePending.TryAdd(session, 0)) return;
        try
        {
            Dictionary<string, object?> result;
            long now = Environment.TickCount64;
            if (_microphoneLastRequest.TryGetValue(session, out var previous) && now - previous < 2000)
                result = new() { ["status"] = "retry_later" };
            else
            {
                _microphoneLastRequest[session] = now;
                result = MicrophoneInviteProvider == null ? new() { ["status"] = "unavailable" } : await MicrophoneInviteProvider();
            }
            if (_sessions.TryGetValue(session, out var current) && ReferenceEquals(current, socket))
            {
                result["requestId"] = message.Id;
                Send(socket, new LanMessage { Type = LanMessage.MicrophoneInviteReply, Id = message.Id, Data = result });
            }
        }
        catch
        {
            if (_sessions.TryGetValue(session, out var current) && ReferenceEquals(current, socket))
                try { Send(socket, new LanMessage { Type = LanMessage.MicrophoneInviteReply, Id = message.Id,
                    Data = new() { ["requestId"] = message.Id, ["status"] = "unavailable" } }); } catch { }
        }
        finally
        {
            _microphonePending.TryRemove(session, out _);
            if (!_sessions.ContainsKey(session)) _microphoneLastRequest.TryRemove(session, out _);
        }
    }

    public void SendTransferState(string owner,string id,string state,long revision)
    {
        foreach(var peer in _clientIds.Where(x=>x.Value==owner))
            if(_sessions.TryGetValue(peer.Key,out var socket))try {Send(socket,new LanMessage{Type="transfer_state",Data=new(){["id"]=id,["state"]=state,["revision"]=revision,["serverId"]=ServerId}});}catch{ }
    }

    private async Task HandleTextDurableAsync(IWebSocketConnection socket,LanMessage msg)
    {
        var session=_sessions.FirstOrDefault(x=>ReferenceEquals(x.Value,socket)).Key;
        if(session==null)return;
        if(DurableTextReceiver==null || !_clientIds.TryGetValue(session,out var clientId)){HandleText(socket,msg);return;}
        var content=msg.Data?.GetValueOrDefault("content")?.ToString()??"";
        if(string.IsNullOrWhiteSpace(content) || content.Length>262144 || string.IsNullOrEmpty(msg.Id) || msg.Id.Length>128){SendTextReceipt(socket,msg.Id,"rejected","invalid_request");return;}
        var fingerprint=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new{msg.Type,content,app=msg.Data?.GetValueOrDefault("app")?.ToString()??""}))));
        try {
            await _durableInbox!.RegisterAsync("text:"+clientId+":"+msg.Id,fingerprint,key=>DurableTextReceiver(key,content,_sessionDevices.GetValueOrDefault(session,"手机")));
            var bytes=System.Text.Encoding.UTF8.GetByteCount(content);
            TransferJournal.Report(new(msg.Id,"文字任务","手机 → 电脑",bytes,bytes,"已接收","",0));
            if(_sessions.TryGetValue(session,out var active) && ReferenceEquals(active,socket))SendTextReceipt(socket,msg.Id,"stored",durable:true);
        }catch(InvalidOperationException){SendTextReceipt(socket,msg.Id,"rejected","request_conflict");}
        catch(Exception ex){Logger.Error("Durable receipt unavailable: {0}",ex.GetType().Name);SendTextReceipt(socket,msg.Id,"retry","storage_unavailable");}
    }

    private void HandleText(IWebSocketConnection socket, LanMessage msg)
    {
        var content = msg.Data?.GetValueOrDefault("content")?.ToString() ?? "";
        var session = _sessions.FirstOrDefault(kv => ReferenceEquals(kv.Value, socket)).Key;
        if (session == null || !_sessionDevices.TryGetValue(session, out var device)) return;
        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrEmpty(msg.Id) || msg.Id.Length > 128) {
            SendTextReceipt(socket, msg.Id, "rejected", "invalid_request"); return;
        }
        var app = msg.Data?.GetValueOrDefault("app")?.ToString() ?? "";
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { msg.Type, content, app }))));
        var key = JsonSerializer.Serialize(new[] { device, msg.Id });
        TextReceipt receipt;
        lock (_receiptGate)
        {
            var at = DateTime.UtcNow;
            foreach (var stale in _textReceipts.Where(kv => kv.Value.Registered.IsValueCreated &&
                at - kv.Value.CreatedAt >= TextReceiptLifetime).Select(kv => kv.Key).ToList()) _textReceipts.Remove(stale);
            if (_textReceipts.TryGetValue(key, out var existing)) {
                if (existing.Fingerprint != fingerprint) { SendTextReceipt(socket, msg.Id, "rejected", "request_conflict"); return; }
                receipt = existing;
            } else {
                if (_textReceipts.Count >= MaxTextReceipts) {
                    // 不提前淘汰十分钟内的成功请求；容量满时拒绝新登记，避免失去重试去重依据。
                    SendTextReceipt(socket, msg.Id, "rejected", "receipt_capacity"); return;
                }
                receipt = new TextReceipt(fingerprint, at, new Lazy<bool>(() => {
                    var handler = TextReceived;
                    if (handler == null) return false;
                    try { handler(content, device); return true; }
                    catch (Exception ex) { Logger.Error("Lan: text registration failed: {0}", ex.GetType().Name); return false; }
                }, LazyThreadSafetyMode.ExecutionAndPublication));
                _textReceipts.Add(key, receipt);
            }
        }
        // Lazy 在缓存锁外登记，重复请求等待同一结果；UI Dispatcher 不与缓存锁互相等待。
        if (!receipt.Registered.Value) { SendTextReceipt(socket, msg.Id, "rejected", "registration_failed"); return; }
        var textBytes = System.Text.Encoding.UTF8.GetByteCount(content);
        TransferJournal.Report(new(msg.Id,content.Length>40?content[..40]:content,"手机 → 电脑",textBytes,textBytes,"已接收","",0));
        SendTextReceipt(socket, msg.Id, "stored");
    }

    private static void SendTextReceipt(IWebSocketConnection socket, string requestId, string status, string? reason = null,bool durable=false)
    {
        Send(socket, new LanMessage
        {
            Type = "ack",
            Id = requestId,
            Data = new Dictionary<string, object?> { ["status"] = status, ["requestId"] = requestId, ["reason"] = reason, ["durable"]=durable }
        });
    }

    /// <summary>处理最近素材列表同步请求（返回最近 50 条摘要 + 快照序号）</summary>
    private void HandleListSync(IWebSocketConnection socket)
    {
        if(!_sessions.Values.Any(x=>ReferenceEquals(x,socket)))return;
        try
        {
            if (SnapshotDispatcher != null) SnapshotDispatcher(() => SendListSnapshot(socket));
            else SendListSnapshot(socket);
        }
        catch (Exception ex)
        {
            Logger.Error("Lan: list_sync failed: {0}", ex.Message);
        }
    }

    private void SendListSnapshot(IWebSocketConnection socket)
    {
            // The owner-thread dispatch may run after this connection has been replaced.
            if (!IsAuthenticated(socket)) return;
            var snapshot = SnapshotProvider?.Invoke();
            var items = snapshot?.Items ?? ListProvider?.Invoke() ?? new List<LanItemSummary>();
            var snapshotSeq = snapshot?.Sequence ?? items.FirstOrDefault()?.Sequence ?? 0;
            Send(socket, new LanMessage
            {
                Type = "list_data",
                Data = new Dictionary<string, object?>
                {
                    ["items"] = items.Select(i => new Dictionary<string, object?>
                    {
                        ["id"] = i.Id,
                        ["type"] = i.Type,
                        ["name"] = i.Name,
                        ["size"] = i.Size,
                        ["time"] = i.AddedTime,
                        ["sequence"] = i.Sequence,
                        ["favorite"]=i.Favorite,["revision"]=i.Revision,["sourceRevision"]=i.SourceRevision,["sourceMissing"]=i.SourceMissing,["source"]=i.Source,["sourceDevice"]=i.SourceDevice,
                        ["group"] = i.Group, ["tags"] = i.Tags, ["note"] = i.Note, ["hasFile"]=i.HasFile || i.Type!="Text"
                    }).ToList(),
                    ["sequence"] = snapshotSeq
                }
            });
            Logger.Run("Lan: list_sync sent {0} items (sequence={1})", items.Count, snapshotSeq);
    }

    /// <summary>广播新增素材摘要给所有已连接设备（接收页实时同步）</summary>
    public void BroadcastItemAdded(LanItemSummary item)
    {
        Broadcast(new LanMessage
        {
            Type = "item_added",
            Data = new Dictionary<string, object?>
            {
                ["id"] = item.Id,
                ["type"] = item.Type,
                ["name"] = item.Name,
                ["size"] = item.Size,
                ["time"] = item.AddedTime,
                ["sequence"] = item.Sequence,
                ["favorite"]=item.Favorite,["revision"]=item.Revision,["sourceRevision"]=item.SourceRevision,["sourceMissing"]=item.SourceMissing,["source"]=item.Source,["sourceDevice"]=item.SourceDevice,
                ["group"] = item.Group, ["tags"] = item.Tags, ["note"] = item.Note, ["hasFile"]=item.HasFile || item.Type!="Text"
            }
        });
        Logger.Run("Lan: broadcast item_added id={0} sequence={1}", item.Id, item.Sequence);
    }

    /// <summary>广播删除素材给所有已连接设备</summary>
    public void BroadcastItemDeleted(string id, long sequence)
    {
        Broadcast(new LanMessage
        {
            Type = "item_deleted",
            Data = new Dictionary<string, object?>
            {
                ["id"] = id,
                ["sequence"] = sequence
            }
        });
        Logger.Run("Lan: broadcast item_deleted id={0} sequence={1}", id, sequence);
    }

    /// <summary>广播清空素材给所有已连接设备</summary>
    public void BroadcastItemCleared(long sequence)
    {
        Broadcast(new LanMessage
        {
            Type = "item_cleared",
            Data = new Dictionary<string, object?> { ["sequence"] = sequence }
        });
        Logger.Run("Lan: broadcast item_cleared sequence={0}", sequence);
    }

    /// <summary>处理素材详情请求：文字返回全文，文件返回下载地址</summary>
    private void HandleGetItem(IWebSocketConnection socket, LanMessage msg)
    {
        if (!IsAuthenticated(socket)) return;
        try
        {
            var id = msg.Data?.GetValueOrDefault("id")?.ToString() ?? "";
            var item = ItemLookup?.Invoke(id);
            if (item == null)
            {
                Send(socket, new LanMessage
                {
                    Type = "item_data",
                    Data = new Dictionary<string, object?> { ["id"] = id, ["error"] = "not_found" }
                });
                return;
            }

            var data = new Dictionary<string, object?>
            {
                ["id"] = item.Id,
                ["type"] = item.Type.ToString(),
                ["name"] = item.DisplayName
            };
            if (item.IsFile || !string.IsNullOrWhiteSpace(item.FilePath))
            {
                data["downloadUrl"] = $"http://{LanNetUtil.GetLanIp()}:{LanConfig.Port + 1}/file/{item.Id}?auth={LanConfig.PasswordHash}";
                data["size"] = item.Size;
            }
            else
            {
                data["content"] = item.TextContent;
            }
            Send(socket, new LanMessage { Type = "item_data", Data = data });
            Logger.Run("Lan: get_item served {0} ({1})", item.DisplayName, item.Type);
        }
        catch (Exception ex)
        {
            Logger.Error("Lan: get_item failed: {0}", ex.Message);
        }
    }

    private bool IsAuthenticated(IWebSocketConnection socket) =>
        _sessions.Values.Any(active => ReferenceEquals(active, socket));

    private void RemoveSession(IWebSocketConnection socket)
    {
        var key = _sessions.FirstOrDefault(kv => kv.Value == socket).Key;
        if (key != null)
        {
            var device = _sessionDevices.TryGetValue(key, out var d) ? d : "unknown";
            _sessions.TryRemove(key, out _);
            _sessionDevices.TryRemove(key, out _);
            _clientIds.TryRemove(key,out _);
            _microphoneLastRequest.TryRemove(key, out _);
            _configurationLast.TryRemove(key, out _);
            _automationLast.TryRemove(key, out _);
            Logger.Run("Lan: device disconnected: {0}", device);
            ClientCountChanged?.Invoke(_sessions.Count);
        }
    }

    private static void Send(IWebSocketConnection socket, LanMessage msg)
    {
        socket.Send(JsonSerializer.Serialize(msg));
    }

    /// <summary>向所有已连接设备广播（M4 用）</summary>
    public void Broadcast(LanMessage msg)
    {
        var json = JsonSerializer.Serialize(msg);
        foreach (var s in _sessions.Values)
        {
            try { s.Send(json); } catch { }
        }
    }
}
