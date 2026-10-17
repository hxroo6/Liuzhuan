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
            _microphoneLastRequest.Clear();
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

            switch (msg.Type)
            {
                case "hello":
                    HandleHello(socket, msg, passwordHash);
                    break;

                case "heartbeat":
                    Send(socket, new LanMessage { Type = "heartbeat" });
                    break;

                case "sync_text":
                case "clipboard_push":
                    HandleText(socket, msg);
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

        var sessionId = Guid.NewGuid().ToString("N");
        // 同设备名重连 → 先清理旧 session（防止设备列表叠加）
        var oldKey = _sessionDevices.FirstOrDefault(kv => kv.Value == device).Key;
        if (oldKey != null)
        {
            try { _sessions[oldKey]?.Close(); } catch { }
            _sessions.TryRemove(oldKey, out _);
            _sessionDevices.TryRemove(oldKey, out _);
            Logger.Run("Lan: replaced old session for device {0}", device);
        }
        _sessions[sessionId] = socket;
        _sessionDevices[sessionId] = device;
        Send(socket, new LanMessage
        {
            Type = "welcome",
            Data = new Dictionary<string, object?>
            {
                ["serverVersion"] = "1.0",
                ["features"] = MicrophoneInviteProvider == null
                    ? new[] { "text", "file", LanMessage.ClipboardReceiptsFeature }
                    : new[] { "text", "file", LanMessage.ClipboardReceiptsFeature, LanMessage.MicrophonePairingFeature },
                ["sessionId"] = sessionId
            }
        });
        Logger.Run("Lan: device connected: {0} ({1})", device, socket.ConnectionInfo.ClientIpAddress);
        ClientCountChanged?.Invoke(_sessions.Count);
        ClientAuthenticated?.Invoke();
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

    private static void SendTextReceipt(IWebSocketConnection socket, string requestId, string status, string? reason = null)
    {
        Send(socket, new LanMessage
        {
            Type = "ack",
            Id = requestId,
            Data = new Dictionary<string, object?> { ["status"] = status, ["requestId"] = requestId, ["reason"] = reason }
        });
    }

    /// <summary>处理最近素材列表同步请求（返回最近 50 条摘要 + 快照序号）</summary>
    private void HandleListSync(IWebSocketConnection socket)
    {
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
                        ["sequence"] = i.Sequence
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
                ["sequence"] = item.Sequence
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
            if (item.IsFile)
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

    private void RemoveSession(IWebSocketConnection socket)
    {
        var key = _sessions.FirstOrDefault(kv => kv.Value == socket).Key;
        if (key != null)
        {
            var device = _sessionDevices.TryGetValue(key, out var d) ? d : "unknown";
            _sessions.TryRemove(key, out _);
            _sessionDevices.TryRemove(key, out _);
            _microphoneLastRequest.TryRemove(key, out _);
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
