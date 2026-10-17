using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Liuzhuan.Services.Lan;

internal static class MicrophonePairingChecks
{
    public static async Task Run()
    {
        int passed = 0, issued = 0, authenticated = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(); int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        var hub = new WsHub { MicrophoneInviteProvider = () => {
            Interlocked.Increment(ref issued);
            return Task.FromResult(new Dictionary<string, object?> { ["status"] = "ready", ["port"] = 8902, ["token"] = new string('A', 48) });
        }};
        hub.ClientAuthenticated += () => Interlocked.Increment(ref authenticated);
        hub.Start(port, LanConfig.PasswordHash);
        async Task Send(ClientWebSocket socket, string type, string id) => await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
            v = 1, type, id, device = "pairing-test", data = new { auth = LanConfig.PasswordHash, ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }
        })), WebSocketMessageType.Text, true, CancellationToken.None);
        async Task<JsonDocument> Read(ClientWebSocket socket) {
            using var deadline = new CancellationTokenSource(5000); var bytes = new byte[8192];
            var received = await socket.ReceiveAsync(bytes, deadline.Token); return JsonDocument.Parse(bytes.AsMemory(0, received.Count));
        }
        try
        {
            using var phone = new ClientWebSocket(); await phone.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), CancellationToken.None);
            await Send(phone, LanMessage.MicrophoneInviteRequest, "before-auth"); await Task.Delay(150);
            Check(issued == 0 && authenticated == 0, "unauthenticated material socket cannot obtain an audio invitation or trigger a connection-success notification");
            await Send(phone, "hello", "hello"); using var welcome = await Read(phone);
            await Task.Delay(100);
            Check(authenticated == 1, "connection-success event is raised after a valid material handshake");
            Check(welcome.RootElement.GetProperty("data").GetProperty("features").EnumerateArray().Any(x => x.GetString() == LanMessage.MicrophonePairingFeature),
                "material welcome advertises one-scan microphone pairing without changing existing authentication");
            await Send(phone, LanMessage.MicrophoneInviteRequest, "one-scan"); using var invite = await Read(phone);
            Check(invite.RootElement.GetProperty("type").GetString() == LanMessage.MicrophoneInviteReply &&
                invite.RootElement.GetProperty("data").GetProperty("requestId").GetString() == "one-scan" &&
                invite.RootElement.GetProperty("data").GetProperty("status").GetString() == "ready" && issued == 1,
                "one authenticated material handshake returns a correlated fresh microphone invitation");
            await Send(phone, LanMessage.MicrophoneInviteRequest, "too-soon"); using var limited = await Read(phone);
            Check(limited.RootElement.GetProperty("data").GetProperty("status").GetString() == "retry_later" && issued == 1,
                "rapid duplicate requests cannot repeatedly rotate microphone invitations");
            await Task.Delay(2100);
            hub.MicrophoneInviteProvider = () => throw new InvalidOperationException("injected audio failure");
            await Send(phone, LanMessage.MicrophoneInviteRequest, "failed-audio"); using var failure = await Read(phone);
            Check(failure.RootElement.GetProperty("data").GetProperty("status").GetString() == "unavailable", "audio setup failure returns a bounded result rather than dropping material connectivity");
            await Send(phone, "heartbeat", "still-alive"); using var heartbeat = await Read(phone);
            Check(heartbeat.RootElement.GetProperty("type").GetString() == "heartbeat", "material heartbeat remains usable after microphone setup failure");
            await Task.Delay(2100);
            var pending = new TaskCompletionSource<Dictionary<string, object?>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            hub.MicrophoneInviteProvider = () => { entered.TrySetResult(); return pending.Task; };
            await Send(phone, LanMessage.MicrophoneInviteRequest, "old-connection"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            phone.Abort(); await Task.Delay(150);
            pending.SetResult(new() { ["status"] = "ready", ["token"] = "test-only" });
            await Task.Delay(150);
            using var next = new ClientWebSocket(); await next.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), CancellationToken.None);
            await Send(next, "hello", "new-hello"); using var nextWelcome = await Read(next);
            Check(nextWelcome.RootElement.GetProperty("type").GetString() == "welcome", "late invitation from a disconnected socket is not sent to its replacement");
            Console.WriteLine($"Microphone material pairing checks: {passed} passed.");
        }
        finally { hub.Stop(); }
    }
}
