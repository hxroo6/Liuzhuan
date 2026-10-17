using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Liuzhuan.Services.Audio;
using NAudio.CoreAudioApi;

class Program
{
    private static int _passed;
    private static void Check(bool condition, string name)
    { if (!condition) throw new Exception("FAIL: " + name); _passed++; Console.WriteLine("PASS: " + name); }
    private static byte[] Frame(long generation, long sequence)
    {
        var frame = new byte[AudioProtocol.FrameSize];
        BinaryPrimitives.WriteUInt16BigEndian(frame, AudioProtocol.Version);
        BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(4), (ulong)generation);
        BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(12), (ulong)sequence);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(36), AudioProtocol.Samples);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(40), AudioProtocol.PayloadSize);
        Array.Fill(frame, (byte)23, AudioProtocol.HeaderSize, AudioProtocol.PayloadSize);
        return frame;
    }
    private static async Task Wait(Func<bool> test)
    { var timer = Stopwatch.StartNew(); while (!test()) { if (timer.ElapsedMilliseconds > 8000) throw new Exception("Timeout"); await Task.Delay(30); } }
    private static async Task Send(ClientWebSocket ws, string type, long generation = 0, bool? ready = null, bool? quiet = null)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { v = 1, type, generation, ready, quiet }));
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }
    private static async Task<JsonElement> Read(ClientWebSocket ws, string wanted)
    {
        using var timeout = new CancellationTokenSource(5000);
        var bytes = new byte[8192];
        while (true)
        {
            var result = await ws.ReceiveAsync(bytes, timeout.Token);
            using var json = JsonDocument.Parse(bytes.AsMemory(0, result.Count));
            if (json.RootElement.GetProperty("type").GetString() == wanted) return json.RootElement.Clone();
        }
    }
    private static async Task<ClientWebSocket> Connect(AudioTransportServer server, int port, string? supplied = null)
    {
        var token = supplied;
        if (token == null)
        {
            using var payload = JsonDocument.Parse(await server.PairingPayloadAsync("127.0.0.1"));
            token = payload.RootElement.GetProperty("token").GetString();
        }
        var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/audio"), CancellationToken.None);
        await ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { v = 1, type = "hello", device = "Test phone", token })), WebSocketMessageType.Text, true, CancellationToken.None);
        return ws;
    }
    private sealed class Sink : IAudioSink
    {
        public bool Enabled;
        public int Count;
        public long DroppedFrames => 0;
        public int BufferedMilliseconds => 0;
        public void Accept(byte[] frame, int offset, int count) { if (Enabled) Interlocked.Increment(ref Count); }
        public void SetForwarding(bool value) => Enabled = value;
        public void Dispose() => Enabled = false;
    }
    private static async Task SilenceChecks()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var sink = new Sink(); await using var server = new AudioTransportServer(sink); await server.StartAsync(port);
        using var ws = await Connect(server, port); var welcome = await Read(ws, "welcome");
        Check(welcome.GetProperty("data").GetProperty("silenceSuppression").GetBoolean(), "silence suppression is negotiated explicitly with new phones");
        await Send(ws, "status", ready: true); await Wait(() => server.Snapshot.Ready);
        var identity = server.Snapshot;
        bool stale = false; try { await server.SetForwardingAsync(true, identity.ConnectionId + 1, identity.StopRevision); } catch (InvalidOperationException) { stale = true; }
        Check(stale && !server.Snapshot.Requested, "conditional start rejects a stale connection atomically");
        await server.SetForwardingAsync(true, identity.ConnectionId, identity.StopRevision);
        var start = await Read(ws, "start_forward"); var gen = start.GetProperty("generation").GetInt64();
        await Send(ws, "forward_started", gen); await Send(ws, "status", gen, true, true); await Wait(() => server.Snapshot.Quiet);
        for (int i = 0; i < 4; i++) { await Read(ws, "lease"); await Send(ws, "status", gen, true, true); }
        Check(server.Snapshot.Ready && server.Snapshot.Requested && !sink.Enabled && sink.Count == 0,
            "quiet authorized phone stays connected past frame timeout with zero audio frames");
        await ws.SendAsync(Frame(gen, 0), WebSocketMessageType.Binary, true, CancellationToken.None); await Wait(() => sink.Count == 1);
        Check(sink.Enabled && !server.Snapshot.Quiet, "valid PCM resumes the sink after silence without a new generation");
        await Send(ws, "status", gen - 1, true, true); await Task.Delay(100);
        Check(!server.Snapshot.Quiet && sink.Enabled, "stale-generation quiet status cannot suppress current audio");
        await Send(ws, "status", gen, true, true); await Wait(() => server.Snapshot.Quiet);
        Check(!sink.Enabled, "entering quiet mode immediately clears queued playback");
        await Send(ws, "status", gen, true, false); await Wait(() => !server.Snapshot.Quiet);
        Check(!sink.Enabled, "claiming sound without actual PCM cannot reopen the output");
        for (int i = 0; i < 4 && server.Snapshot.Phone.Length > 0; i++)
        { try { await Read(ws, "lease"); await Send(ws, "status", gen, true, false); } catch { break; } }
        await Wait(() => server.Snapshot.Phone.Length == 0);
        Check(!sink.Enabled, "sound claim without audio times out instead of leaving a stuck forwarding state");
    }
    private static async Task IntegratedAudioChecks()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var sink = new Sink(); await using var server = new AudioTransportServer(sink); await server.StartAsync(port);
        using (var phone = await Connect(server, port, await server.RotateInvitationAsync(startWhenReady: true)))
        {
            await Read(phone, "welcome");
            Check(!server.Snapshot.Requested && !sink.Enabled, "integrated invitation does not forward before phone capture is ready");
            await Send(phone, "status", ready: false); await Send(phone, "status", ready: true);
            var start = await Read(phone, "start_forward"); long gen = start.GetProperty("generation").GetInt64();
            Check(server.Snapshot.Requested && !sink.Enabled, "integrated handshake requests forwarding automatically after readiness, without a second QR or PC start click");
            await Send(phone, "forward_started", gen);
            await phone.SendAsync(Frame(gen, 0), WebSocketMessageType.Binary, true, CancellationToken.None); await Wait(() => sink.Count == 1);
            Check(sink.Enabled, "one-scan readiness and acknowledgement deliver real PCM to the selected sink");
            await Send(phone, "paused", gen); var stop = await Read(phone, "stop_forward");
            gen = stop.GetProperty("generation").GetInt64();
            await Send(phone, "status", gen, true); await Read(phone, "lease");
            Check(!server.Snapshot.Requested && !sink.Enabled, "integrated start is one-shot and cannot override later phone pause/re-arm");
            phone.Abort(); await Wait(() => server.Snapshot.Phone.Length == 0);
        }
        using (var legacy = await Connect(server, port))
        {
            await Read(legacy, "welcome"); await Send(legacy, "status", ready: true); await Read(legacy, "lease");
            Check(!server.Snapshot.Requested, "legacy dedicated audio QR keeps its original manual forwarding semantics");
            legacy.Abort(); await Wait(() => server.Snapshot.Phone.Length == 0);
        }
        using (var stopped = await Connect(server, port, await server.RotateInvitationAsync(startWhenReady: true)))
        {
            await Read(stopped, "welcome"); await server.SetForwardingAsync(false);
            await Send(stopped, "status", ready: true); await Read(stopped, "lease");
            Check(!server.Snapshot.Requested, "explicit PC stop before readiness cancels the one-scan start intent");
        }
    }
    static async Task Main(string[] args)
    {
        if (args.Contains("--host-check"))
        {
            var render = VirtualAudioOutput.List(DataFlow.Render).First(x => x.Name.StartsWith("CABLE Input"));
            var capture = VirtualAudioOutput.List(DataFlow.Capture).First(x => x.Name.StartsWith("CABLE Output"));
            var host = new MicrophoneHost();
            try
            {
                await host.StartAsync(render.Id, capture.Id);
                await host.StartObservingAsync(); await host.StartObservingAsync();
                await Task.Delay(500);
                Check(host.Server != null && host.Observing && !host.Automatic, "host starts one observer without enabling automatic demand");
                await host.BeginCalibrationAsync();
                await Task.WhenAll(host.StopObservingAsync(), host.StopAsync()).WaitAsync(TimeSpan.FromSeconds(5));
                Check(host.Server == null && !host.Observing && !host.Automatic && !host.Calibrated,
                    "simultaneous observer/server stop releases resources without deadlock or retained calibration");
            }
            finally { await host.StopAsync(); }
            return;
        }
        if (args.Length == 2 && args[0] == "--observe-all")
        {
            var observers = VirtualAudioOutput.List(DataFlow.Capture).Select(x => (x.Name, Observer: new CaptureSessionObserver(x.Id))).ToArray();
            try
            {
                var previous = new Dictionary<string, string>();
                for (int i = 0; i < int.Parse(args[1]) * 5; i++)
                {
                    await Task.Delay(200);
                    foreach (var item in observers)
                        if (!previous.TryGetValue(item.Name, out var status) || status != item.Observer.Status)
                        { previous[item.Name] = item.Observer.Status; Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {item.Name}: {item.Observer.Status}"); }
                }
            }
            finally { foreach (var item in observers) item.Observer.Dispose(); }
            return;
        }
        if (args.Contains("--observer-check"))
        {
            var endpoint = VirtualAudioOutput.List(DataFlow.Capture).First(x => x.Name.StartsWith("CABLE Output"));
            using var observer = new CaptureSessionObserver(endpoint.Id);
            await Task.Delay(600);
            Check(observer.Snapshot.Healthy, "native capture session observer starts on selected endpoint");
            using var enumerator = new MMDeviceEnumerator(); using var device = enumerator.GetDevice(endpoint.Id);
            using var capture = new WasapiCapture(device);
            capture.DataAvailable += (_, _) => { }; // discard samples, no recording or physical input
            capture.StartRecording();
            await Wait(() => observer.Snapshot.OtherActive > 0);
            Check(observer.Snapshot.ActiveOwners.Length == 0, "a virtual capture opened by this test cannot impersonate Codex");
            capture.StopRecording();
            await Wait(() => observer.Snapshot.OtherActive == 0);
            Check(observer.Snapshot.Healthy, "session creation and stop are observed without restarting the observer");
            return;
        }
        if (args.Length == 2 && args[0] == "--observe-cable")
        {
            var endpoint = VirtualAudioOutput.List(DataFlow.Capture).First(x => x.Name.StartsWith("CABLE Output"));
            using var observer = new CaptureSessionObserver(endpoint.Id);
            string previous = "";
            for (int i = 0; i < int.Parse(args[1]) * 5; i++)
            {
                await Task.Delay(200);
                if (observer.Status != previous) { previous = observer.Status; Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {previous}"); }
            }
            return;
        }
        if (args.Contains("--cable-check")) { await CableProbe.Run(); return; }
        if (args.Length == 2 && args[0] == "--capture-cable") { await CableProbe.CaptureOnly(int.Parse(args[1])); return; }
        if (args.Contains("--defaults"))
        {
            using var devices = new MMDeviceEnumerator();
            foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture }) foreach (var role in new[] { Role.Multimedia, Role.Communications })
            { using var device = devices.GetDefaultAudioEndpoint(flow, role); Console.WriteLine($"Default {flow} {role}: {device.FriendlyName}"); }
            return;
        }
        if (args.Contains("--devices"))
        {
            foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
                foreach (var endpoint in VirtualAudioOutput.List(flow)) Console.WriteLine($"{flow}: {endpoint.Name}");
            return;
        }
        await IntegratedAudioChecks();
        {
            var listenerForLevel = new TcpListener(IPAddress.Loopback, 0); listenerForLevel.Start();
            int levelPort = ((IPEndPoint)listenerForLevel.LocalEndpoint).Port; listenerForLevel.Stop();
            await using var levelServer = new AudioTransportServer(new DiagnosticAudioSink()); await levelServer.StartAsync(levelPort);
            Check(levelServer.LiveLevel.Peak == 0, "header audio source is silent before any actual PCM");
            using var levelPhone = await Connect(levelServer, levelPort, await levelServer.RotateInvitationAsync(startWhenReady: true));
            await Read(levelPhone, "welcome"); await Send(levelPhone, "status", ready: true);
            var demand = await Read(levelPhone, "start_forward"); long levelGeneration = demand.GetProperty("generation").GetInt64();
            await Send(levelPhone, "forward_started", levelGeneration);
            await levelPhone.SendAsync(Frame(levelGeneration, 0), WebSocketMessageType.Binary, true, CancellationToken.None);
            await Wait(() => levelServer.LiveLevel.Peak > 0);
            Check(levelServer.LiveLevel.Peak > 0, "header peaks are sourced from valid received PCM rather than animation");
            await Task.Delay(250);
            Check(levelServer.LiveLevel.Peak == 0, "header meter expires old samples instead of holding a stale loud level");
        }
        var privateState = new MicSnapshot("SECRET_STATUS", Phone: "SECRET_PHONE", ConnectionId: 12,
            TotalFrames: 50, ReadPcmBytes: 16000, SessionSeconds: 9, HeartbeatAgeMs: 500);
        var report = MicrophoneDiagnostics.Format(privateState);
        Check(!report.Contains("SECRET") && !report.Contains("12") && report.Contains("0.50") && report.Contains("50 帧"),
            "shareable diagnostics allowlist counters without phone identity or arbitrary status text");
        Check(MicrophoneDiagnostics.Format(privateState with { DiagnosticOnly = true }).Contains("播放端已读取：不输出") &&
            MicrophoneDiagnostics.Format(privateState with { ReadPcmBytes = null }).Contains("尚无可用统计"),
            "receive-only and unknown sinks cannot claim render consumption");
        Check(MicrophoneDiagnostics.Format(new MicSnapshot("waiting"), privateState).Contains("最近已结束") &&
            MicrophoneDiagnostics.Format(new MicSnapshot("waiting"), privateState).Contains("状态：已结束"),
            "last-session evidence is clearly distinguished from an active connection");
        FollowChecks.Run(Check);
        await SilenceChecks();
        var valid = Frame(7, 0);
        var meter = new PcmSignalMeter(); meter.Measure(new byte[640]);
        Check(meter.Level.Peak == 0 && meter.Level.Dbfs == -96, "PCM meter silence is finite and zero");
        meter.Measure(new byte[] { 0, 64, 0, 192 });
        Check(Math.Abs(meter.Level.Rms - 0.5f) < 0.00001 && meter.Level.Peak == 0.5f && Math.Abs(meter.Level.Dbfs + 6.0206f) < 0.01, "PCM meter measures signed little-endian RMS and dBFS");
        meter.Measure(new byte[] { 0, 128 });
        Check(meter.Level.Peak == 1, "negative full-scale sample does not overflow");
        using (var diagnostic = new DiagnosticAudioSink())
        {
            diagnostic.SetForwarding(true); diagnostic.Accept(valid, 44, 640);
            Check(diagnostic.DiagnosticOnly && diagnostic.Level.Peak > 0, "receive-only diagnostics measures PCM without audio device");
            diagnostic.SetForwarding(false);
            Check(diagnostic.Level.Peak == 0, "stop resets receive-only meter");
        }
        if (args.Length == 2 && args[0] == "--android-frame")
        {
            var android = File.ReadAllBytes(args[1]);
            Check(AudioProtocol.TryFrame(android, 7, -1, out _) && android.AsSpan(44).ToArray().All(b => b == 23), "Android production PCM packet accepted unchanged by Windows parser");
        }
        Check(AudioProtocol.TryFrame(valid, 7, -1, out _), "valid PCM frame");
        Check(!AudioProtocol.TryFrame(valid, 8, -1, out _), "stale generation rejected");
        Check(!AudioProtocol.TryFrame(valid, 7, 0, out _), "repeated sequence rejected");
        Check(!AudioProtocol.TryFrame(valid.AsSpan(1), 7, -1, out _), "truncated frame rejected");
        var malformed = (byte[])valid.Clone(); malformed[43]++;
        Check(!AudioProtocol.TryFrame(malformed, 7, -1, out _), "payload length validated");
        var buffer = new LivePcmBuffer(); buffer.Enable(true);
        var pcm = Enumerable.Repeat((byte)23, AudioProtocol.PayloadSize).ToArray();
        for (int i = 0; i < 100; i++) buffer.Write(pcm, 0, pcm.Length);
        Check(buffer.BufferedMilliseconds <= 200 && buffer.DroppedFrames > 0, "overflow remains bounded and drops old data");
        var rendered = new byte[pcm.Length]; buffer.Enable(false); buffer.Read(rendered, 0, rendered.Length);
        Check(rendered.All(b => b == 0) && buffer.BufferedMilliseconds == 0, "stop discards queued audio and emits silence");
        Check(buffer.ReadPcmBytes == 0, "discarded and generated silence does not count as render consumption");
        buffer.Enable(true); buffer.Write(pcm, 0, pcm.Length); buffer.Read(rendered, 0, rendered.Length);
        Check(rendered.All(b => b == 0), "jitter priming does not consume short queue");
        Check(buffer.ReadPcmBytes == 0, "priming silence does not inflate output evidence");
        buffer.Write(pcm, 0, pcm.Length); buffer.Write(pcm, 0, pcm.Length); buffer.Read(rendered, 0, rendered.Length);
        Check(rendered.All(b => b == 23), "primed queue renders PCM");
        Check(buffer.ReadPcmBytes == pcm.Length, "only actual dequeued PCM counts as render consumption");
        buffer.Read(new byte[pcm.Length * 4], 0, pcm.Length * 4);
        Check(buffer.ReadPcmBytes == pcm.Length * 3, "underrun padding counts only PCM present in the queue");
        buffer.Enable(false); buffer.Read(rendered, 0, rendered.Length);
        Check(buffer.ReadPcmBytes == pcm.Length * 3, "stopping output retains the cumulative evidence without counting silence");

        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var sink = new Sink(); await using var server = new AudioTransportServer(sink); await server.StartAsync(port);
        using (var invalid = await Connect(server, port, "wrong"))
        {
            bool rejected = false; try { await Read(invalid, "welcome"); } catch { rejected = true; }
            Check(rejected && !server.Snapshot.Ready, "wrong invitation cannot authorize microphone");
        }
        using var ws = await Connect(server, port); await Read(ws, "welcome");
        Check(server.Snapshot.Phone == "Test phone" && !server.Snapshot.Ready, "authentication alone is not microphone readiness");
        bool notReady = false; try { await server.SetForwardingAsync(true); } catch (InvalidOperationException) { notReady = true; }
        Check(notReady, "PC cannot start an unprepared phone");
        await Send(ws, "status", ready: true); await Wait(() => server.Snapshot.Ready);
        using (var other = await Connect(server, port, "wrong"))
        { try { await Read(other, "welcome"); } catch { } Check(server.Snapshot.Phone == "Test phone", "second connection does not replace active phone"); }
        await server.SetForwardingAsync(true); var start = await Read(ws, "start_forward"); long gen = start.GetProperty("generation").GetInt64();
        Check(!sink.Enabled && !server.Snapshot.Receiving, "request is not a forwarding acknowledgement");
        await Send(ws, "forward_started", gen); await Wait(() => server.Snapshot.Status.StartsWith("转发已确认"));
        Check(!sink.Enabled, "acknowledgement alone does not open the audio sink");
        await ws.SendAsync(Frame(gen, 0), WebSocketMessageType.Binary, true, CancellationToken.None); await Wait(() => sink.Count == 1);
        Check(sink.Count == 1, "authenticated current-generation PCM reaches sink");
        await server.SetForwardingAsync(false); var stop = await Read(ws, "stop_forward");
        Check(!sink.Enabled && stop.GetProperty("generation").GetInt64() > gen, "stop immediately closes output and advances generation");
        await Send(ws, "forward_started", gen);
        await ws.SendAsync(Frame(gen, 1), WebSocketMessageType.Binary, true, CancellationToken.None); await Send(ws, "status", gen, true);
        await Read(ws, "lease");
        Check(!sink.Enabled && sink.Count == 1, "late start acknowledgement and stale PCM cannot revive output");
        await server.SetForwardingAsync(true); start = await Read(ws, "start_forward"); gen = start.GetProperty("generation").GetInt64();
        await Send(ws, "forward_started", gen); await Wait(() => server.Snapshot.Status.StartsWith("转发已确认"));
        await ws.SendAsync(Frame(gen, 0), WebSocketMessageType.Binary, true, CancellationToken.None); await Wait(() => sink.Count == 2);
        await Send(ws, "status", gen, true); await Wait(() => server.Snapshot.TotalFrames == 2);
        Check(server.Snapshot.Frames == 1 && server.Snapshot.TotalFrames == 2,
            "session received count survives manual stop/start while generation frames reset");
        await Send(ws, "paused", gen); await Wait(() => !sink.Enabled && !server.Snapshot.Requested);
        Check(!server.Snapshot.Requested && !server.Snapshot.Ready, "phone pause immediately clears readiness and overrides PC demand");
        await Send(ws, "status", gen, false); await Wait(() => server.Snapshot.Status.Contains("先在手机"));
        Check(server.Snapshot.Status.Contains("再次请求"), "phone pause guidance survives periodic status updates");
        ws.Abort(); await Wait(() => server.Snapshot.Phone == "");
        Check(!sink.Enabled && !server.Snapshot.Ready, "disconnect clears output and readiness");
        Check(server.LastSession?.TotalFrames == 2 && server.Snapshot.TotalFrames == 0 && server.Snapshot.Status.Contains("无需重新扫码"),
            "disconnect retains completed session counters and offers reconnect without rescanning");
        using var hung = await Connect(server, port); await Read(hung, "welcome"); await Send(hung, "status", ready: true); await Wait(() => server.Snapshot.Ready);
        await server.SetForwardingAsync(true); await Read(hung, "start_forward");
        await Wait(() => server.Snapshot.Phone == "");
        Check(!sink.Enabled, "unacknowledged start times out and disconnects");
        Console.WriteLine($"Audio checks: {_passed} passed. No physical microphone or Codex ASR tested.");
    }
}
