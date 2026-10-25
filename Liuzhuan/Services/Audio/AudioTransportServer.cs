using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Liuzhuan.Services.Audio;

public sealed record MicSnapshot(string Status, string Phone = "", bool Ready = false, bool Requested = false,
    bool Receiving = false, long Frames = 0, int BufferedMs = 0, long Dropped = 0,
    float Peak = 0, float Dbfs = -96, bool DiagnosticOnly = false,
    long ConnectionId = 0, long StopRevision = 0, bool Quiet = false,
    long TotalFrames = 0, long? ReadPcmBytes = null, long SessionSeconds = 0, long HeartbeatAgeMs = 0,
    MicrophoneIssue Issue = MicrophoneIssue.None);

/// <summary>Independent plaintext LAN service. All transitions are serialized; audio is never persisted.</summary>
public sealed class AudioTransportServer : IAsyncDisposable
{
    private sealed class Peer
    {
        public readonly WebSocket Socket;
        public Peer(WebSocket socket) => Socket = socket;
        public readonly Channel<string> Commands = Channel.CreateBounded<string>(16);
        public long Generation, Sequence = -1, Frames, LastStatus = Stopwatch.GetTimestamp(), Started, LastFrame;
        public bool Ready, Requested, Receiving, SinkEnabled;
        public readonly long Id = Interlocked.Increment(ref _connectionSequence);
        public long StopRevision;
        public bool Quiet;
        public bool StartWhenReady;
        public bool Paused;
        public long TotalFrames, ReadPcmBaseline;
        public readonly long ConnectedAt = Stopwatch.GetTimestamp();
        public string Name = "手机";
    }
    private readonly SemaphoreSlim _gate = new(1);
    private static long _connectionSequence;
    private readonly IAudioSink _sink;
    private readonly CancellationTokenSource _life = new();
    private WebApplication? _app;
    private Task? _watchdog;
    private int _disposed;
    private Peer? _peer;
    private string _invite = "";
    private long _inviteAt;
    private bool _inviteStartWhenReady;
    private MicSnapshot _snapshot = new("音频服务已关闭");
    private long _lastPcmAt;
    private MicrophoneIssue _issue;
    public AudioLevel LiveLevel => Interlocked.Read(ref _lastPcmAt) is var at && at != 0 && Stopwatch.GetElapsedTime(at).TotalMilliseconds < 200
        ? _sink.Level : new(0, 0, -96);
    public MicSnapshot Snapshot => Volatile.Read(ref _snapshot);
    private MicSnapshot? _lastSession;
    public MicSnapshot? LastSession => Volatile.Read(ref _lastSession);
    // Sparse evidence notification, at most once per connection. Handlers only enqueue background work.
    public event Action<MicSnapshot>? SignalVerified;
    private long _verifiedConnection;
    private readonly Action<string>? _diagnostic;
    public int Port { get; private set; }
    public AudioTransportServer(IAudioSink sink, Action<string>? diagnostic = null)
    { _sink = sink; _diagnostic = diagnostic; }

    public async Task StartAsync(int port = AudioProtocol.Port)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>(), ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Any, port);
            options.Limits.MaxConcurrentConnections = 8;
            options.Limits.MaxRequestBodySize = AudioProtocol.MaxMessageSize;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
        });
        _app = builder.Build();
        _app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(5) });
        _app.Run(HandleAsync);
        await _app.StartAsync(_life.Token);
        Port = port;
        await RotateInvitationAsync();
        _watchdog = WatchdogAsync();
    }

    public async Task<string> RotateInvitationAsync(bool startWhenReady = false)
    {
        await _gate.WaitAsync();
        try
        {
            if (_peer != null) throw new InvalidOperationException("请先结束当前手机会话，再生成新二维码。 ");
            _invite = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            _inviteAt = Stopwatch.GetTimestamp();
            _inviteStartWhenReady = startWhenReady;
            Publish(startWhenReady ? "流转握手已完成，等待手机开启麦克风" : "等待手机扫码 · 二维码两分钟有效");
            return _invite;
        }
        finally { _gate.Release(); }
    }

    public async Task<string> PairingPayloadAsync(string ip)
    {
        var token = await RotateInvitationAsync();
        return JsonSerializer.Serialize(new { kind = AudioProtocol.Kind, v = AudioProtocol.Version, ip, port = Port, token });
    }

    private static bool IsLocal(IPAddress? address)
    {
        if (address == null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var b = address.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31);
    }

    private async Task HandleAsync(HttpContext context)
    {
        if (context.Request.Path != "/audio" || !IsLocal(context.Connection.RemoteIpAddress) || !context.WebSockets.IsWebSocketRequest)
        { context.Response.StatusCode = 400; return; }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        Peer? peer = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_life.Token, context.RequestAborted);
        try
        {
            var buffer = new byte[AudioProtocol.MaxMessageSize];
            using var authDeadline = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            authDeadline.CancelAfter(TimeSpan.FromSeconds(5));
            var first = await ReceiveAsync(socket, buffer, authDeadline.Token);
            if (first.Type != WebSocketMessageType.Text) return;
            using var hello = JsonDocument.Parse(buffer.AsMemory(0, first.Count));
            var json = hello.RootElement;
            if (json.GetProperty("v").GetInt32() != AudioProtocol.Version || json.GetProperty("type").GetString() != "hello") return;
            var token = json.GetProperty("token").GetString() ?? "";
            await _gate.WaitAsync(linked.Token);
            try
            {
                if (_peer != null || _invite.Length == 0 || Stopwatch.GetElapsedTime(_inviteAt).TotalMinutes >= 2 ||
                    !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(_invite))) return;
                _invite = "";
                _issue = MicrophoneIssue.None;
                peer = new Peer(socket) { Name = (json.GetProperty("device").GetString() ?? "手机")[..Math.Min(64, (json.GetProperty("device").GetString() ?? "手机").Length)] };
                _peer = peer;
                peer.ReadPcmBaseline = _sink.ReadPcmBytes ?? 0;
                peer.StartWhenReady = _inviteStartWhenReady; _inviteStartWhenReady = false;
                Queue(peer, AudioProtocol.Message("welcome", data: new { rate = AudioProtocol.Rate, samples = AudioProtocol.Samples, leaseMs = 5000, silenceSuppression = true }));
                Publish("手机已连接，等待麦克风准备");
            }
            finally { _gate.Release(); }
            var sender = SendAsync(peer, linked.Token);
            try
            {
                while (!linked.IsCancellationRequested)
                {
                    var message = await ReceiveAsync(socket, buffer, linked.Token);
                    if (message.Type == WebSocketMessageType.Close) break;
                    await _gate.WaitAsync(linked.Token);
                    try
                    {
                        if (_peer != peer) break;
                        if (message.Type == WebSocketMessageType.Binary)
                        {
                            if (!peer.Receiving) continue;
                            if (!AudioProtocol.TryFrame(buffer.AsSpan(0, message.Count), peer.Generation, peer.Sequence, out var sequence))
                                throw new InvalidDataException("音频帧无效");
                            bool firstOrResumed = peer.Quiet || peer.Frames == 0;
                            peer.Sequence = sequence; peer.Frames++; peer.TotalFrames++; peer.LastFrame = Stopwatch.GetTimestamp();
                            peer.Quiet = false;
                            if (!peer.SinkEnabled) { _sink.SetForwarding(true); peer.SinkEnabled = true; }
                            _sink.Accept(buffer, AudioProtocol.HeaderSize, AudioProtocol.PayloadSize);
                            Interlocked.Exchange(ref _lastPcmAt, Stopwatch.GetTimestamp());
                            if (firstOrResumed) Publish("正在接收手机音频");
                            if (peer.TotalFrames == 1) Diagnose("first_pcm");
                        }
                        else HandleControl(peer, buffer.AsMemory(0, message.Count));
                    }
                    finally { _gate.Release(); }
                }
            }
            finally { linked.Cancel(); socket.Abort(); peer.Commands.Writer.TryComplete(); await sender; }
        }
        catch (Exception ex)
        {
            // Keep native endpoint/COM failures isolated too. Never log payloads or exception text.
            if (!_life.IsCancellationRequested && peer != null)
                _issue = ex is JsonException or InvalidDataException or KeyNotFoundException or FormatException
                    ? MicrophoneIssue.Protocol : ex is WebSocketException or OperationCanceledException
                    ? MicrophoneIssue.Network : MicrophoneIssue.Driver;
        }
        finally
        {
            socket.Abort();
            await _gate.WaitAsync();
            try
            {
                if (peer != null && _peer == peer)
                {
                    try { _sink.SetForwarding(false); } catch { _issue = MicrophoneIssue.Driver; }
                    Publish("手机会话已结束");
                    Volatile.Write(ref _lastSession, Snapshot);
                    Diagnose("session_end");
                    _peer = null;
                    Publish("手机会话已结束；在手机点左上音波图连接当前电脑，无需重新扫码");
                }
            }
            finally { _gate.Release(); }
        }
    }

    private void HandleControl(Peer peer, ReadOnlyMemory<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.GetProperty("v").GetInt32() != AudioProtocol.Version) throw new InvalidDataException();
        var type = root.GetProperty("type").GetString();
        var generation = root.GetProperty("generation").GetInt64();
        if (type == "status")
        {
            peer.LastStatus = Stopwatch.GetTimestamp();
            bool wasReady = peer.Ready;
            peer.Ready = root.GetProperty("ready").GetBoolean();
            if (peer.Ready) peer.Paused = false;
            if (wasReady && !peer.Ready) peer.StopRevision++;
            if (!peer.Ready) StopLocked(peer);
            if (peer.Ready && peer.StartWhenReady) { peer.StartWhenReady = false; StartLocked(peer); }
            bool quiet = peer.Ready && peer.Requested && peer.Receiving && generation == peer.Generation &&
                root.TryGetProperty("quiet", out var quietValue) && quietValue.ValueKind == JsonValueKind.True;
            if (quiet && !peer.Quiet) { peer.Quiet = true; peer.SinkEnabled = false; _sink.SetForwarding(false); }
            if (!quiet && peer.Quiet) { peer.Quiet = false; peer.LastFrame = Stopwatch.GetTimestamp(); }
            // Resume only on valid PCM. A status update alone cannot reopen the sink.
            Publish(peer.Quiet ? "安静待命 · 已暂停音频数据，仅保留心跳" : peer.Requested ? peer.Receiving && peer.Frames > 0 ? "正在接收手机音频" : "等待有效音频帧" : peer.Ready ? "手机正在采音待命，暂未转发" : peer.Paused ? "手机已暂停传输；先在手机允许再次请求，再点电脑开始转发" : "手机麦克风未准备");
        }
        else if (type == "forward_started" && peer.Requested && !peer.Receiving && generation == peer.Generation)
        {
            peer.Receiving = true; peer.LastFrame = Stopwatch.GetTimestamp();
            Publish("转发已确认，等待有效音频帧");
        }
        else if (type is "paused" or "ended" or "error")
        {
            peer.StartWhenReady = false;
            peer.Paused = type == "paused";
            peer.StopRevision++;
            StopLocked(peer); peer.Ready = false;
            Publish(type == "paused" ? "手机已暂停传输" : "手机麦克风会话已结束或被系统阻断");
        }
        else if (type is not ("forward_stopped" or "forward_started")) throw new InvalidDataException("未知控制消息");
    }

    public async Task SetForwardingAsync(bool value, long? expectedConnection = null, long? expectedStopRevision = null)
    {
        await _gate.WaitAsync();
        try
        {
            var peer = _peer ?? throw new InvalidOperationException("请先让手机扫码并开启麦克风会话。");
            if (expectedConnection.HasValue && (peer.Id != expectedConnection || peer.StopRevision != expectedStopRevision))
                throw new InvalidOperationException("手机会话已变化，请重新确认自动跟随。");
            if (value && !peer.Ready) throw new InvalidOperationException("手机麦克风尚未准备，请在手机查看权限和系统状态。");
            if (!value) peer.StartWhenReady = false;
            if (value == peer.Requested) return;
            if (!value) { StopLocked(peer); Publish("已停止转发，手机仍可能采音待命"); return; }
            StartLocked(peer);
        }
        finally { _gate.Release(); }
    }

    private void StartLocked(Peer peer)
    {
        peer.Generation++; peer.Requested = true; peer.Receiving = false; peer.Quiet = false; peer.Sequence = -1; peer.Frames = 0;
        peer.Started = Stopwatch.GetTimestamp(); _sink.SetForwarding(false);
        Queue(peer, AudioProtocol.Message("start_forward", peer.Generation));
        Publish("已请求，等待手机确认转发");
    }

    public async Task EndPhoneAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_peer is not { } peer) return;
            peer.StopRevision++;
            peer.StartWhenReady = false;
            StopLocked(peer); peer.Ready = false;
            Queue(peer, AudioProtocol.Message("end_session", peer.Generation));
            Publish("已停止电脑输出，等待手机释放麦克风");
        }
        finally { _gate.Release(); }
    }

    private void StopLocked(Peer peer)
    {
        peer.Quiet = false; peer.SinkEnabled = false;
        _sink.SetForwarding(false);
        if (!peer.Requested && !peer.Receiving) return;
        peer.Requested = peer.Receiving = false; peer.Generation++;
        Queue(peer, AudioProtocol.Message("stop_forward", peer.Generation));
    }
    private static void Queue(Peer peer, string message)
    { if (!peer.Commands.Writer.TryWrite(message)) peer.Socket.Abort(); }
    private void Publish(string status)
    {
        var snapshot = new MicSnapshot(status, _peer?.Name ?? "", _peer?.Ready ?? false,
        _peer?.Requested ?? false, _peer?.Receiving == true && !_peer.Quiet && _peer.Frames > 0, _peer?.Frames ?? 0, _sink.BufferedMilliseconds, _sink.DroppedFrames,
        _sink.Level.Peak, _sink.Level.Dbfs, _sink.DiagnosticOnly, _peer?.Id ?? 0, _peer?.StopRevision ?? 0, _peer?.Quiet ?? false,
        _peer?.TotalFrames ?? 0, _peer != null && _sink.ReadPcmBytes is long read ? Math.Max(0, read - _peer.ReadPcmBaseline) : null,
        _peer != null ? (long)Stopwatch.GetElapsedTime(_peer.ConnectedAt).TotalSeconds : 0,
        _peer != null ? (long)Stopwatch.GetElapsedTime(_peer.LastStatus).TotalMilliseconds : 0, _issue);
        Volatile.Write(ref _snapshot, snapshot);
        if (snapshot.ConnectionId > 0 && snapshot.ConnectionId != _verifiedConnection && !snapshot.DiagnosticOnly &&
            snapshot.Requested && snapshot.Receiving && snapshot.TotalFrames >= 25 && snapshot.ReadPcmBytes >= 16000 &&
            snapshot.Issue == MicrophoneIssue.None && snapshot.Peak > 0 && float.IsFinite(snapshot.Peak))
        {
            _verifiedConnection = snapshot.ConnectionId;
            try { SignalVerified?.Invoke(snapshot); } catch { /* Configuration protection cannot interrupt audio. */ }
        }
    }

    private void Diagnose(string phase)
    {
        // Sparse phase metadata only; no phone name, address, invitation, payload or exception text.
        try { var state = Snapshot; _diagnostic?.Invoke($"phase={phase} receivedFrames={state.TotalFrames} renderReadBytes={state.ReadPcmBytes?.ToString() ?? "unknown"} seconds={state.SessionSeconds} diagnosticOnly={state.DiagnosticOnly}"); }
        catch { /* Observability must never interrupt audio. */ }
    }

    private async Task WatchdogAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(_life.Token))
            {
                await _gate.WaitAsync(_life.Token);
                try
                {
                    if (_peer is not { } peer) continue;
                    if (Stopwatch.GetElapsedTime(peer.LastStatus).TotalSeconds > 6 ||
                        peer.Requested && !peer.Receiving && Stopwatch.GetElapsedTime(peer.Started).TotalSeconds > 3 ||
                        peer.Receiving && !peer.Quiet && Stopwatch.GetElapsedTime(peer.LastFrame).TotalSeconds > 2)
                    { _issue = MicrophoneIssue.Network; StopLocked(peer); peer.Socket.Abort(); Publish("手机未及时响应，已停止输出"); continue; }
                    Queue(peer, AudioProtocol.Message("lease", peer.Generation, new { forwarding = peer.Requested }));
                    Publish(Snapshot.Status);
                }
                finally { _gate.Release(); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static async Task SendAsync(Peer peer, CancellationToken token)
    {
        try
        {
            await foreach (var message in peer.Commands.Reader.ReadAllAsync(token))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(2000);
                await peer.Socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, timeout.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { peer.Socket.Abort(); }
    }

    private static async Task<(WebSocketMessageType Type, int Count)> ReceiveAsync(WebSocket socket, byte[] buffer, CancellationToken token)
    {
        int count = 0; WebSocketReceiveResult result;
        do
        {
            if (count == buffer.Length) throw new InvalidDataException("消息超过上限");
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, count, buffer.Length - count), token);
            count += result.Count;
        } while (!result.EndOfMessage);
        return (result.MessageType, count);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _life.Cancel();
        await _gate.WaitAsync();
        try { _invite = ""; _peer?.Socket.Abort(); _sink.SetForwarding(false); }
        finally { _gate.Release(); }
        try
        {
            if (_watchdog != null) await _watchdog;
            if (_app != null)
            {
                using var timeout = new CancellationTokenSource(2000);
                try { await _app.StopAsync(timeout.Token); }
                catch (OperationCanceledException) { }
                finally { await _app.DisposeAsync(); }
            }
        }
        finally { _sink.Dispose(); _life.Dispose(); Publish("音频服务已关闭"); }
    }
}
