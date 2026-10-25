using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Liuzhuan.Services.Lan;

/// <summary>Real TCP/WebSocket checks against the source-linked production WsHub, with no Windows audio side effects.</summary>
internal static class MicrophoneConfigurationChecks
{
    public static async Task Run()
    {
        int passed = 0, checks = 0, repairs = 0;
        void Check(bool ok, string label)
        {
            if (!ok) throw new Exception(label);
            passed++;
            Console.WriteLine("PASS " + label);
        }
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var hub = new WsHub();
        Func<string, Task<Dictionary<string, object?>>> provider = action =>
        {
            if (action == "check") Interlocked.Increment(ref checks);
            else if (action == "repair") Interlocked.Increment(ref repairs);
            else throw new InvalidOperationException("unexpected provider action");
            return Task.FromResult(new Dictionary<string, object?> { ["status"] = "ready", ["canRepair"] = true });
        };
        hub.MicrophoneConfigurationProvider = provider;
        hub.Start(port, LanConfig.PasswordHash);

        static string RequestId() => Guid.NewGuid().ToString("D");
        async Task Send(ClientWebSocket socket, string type, string id, object data) =>
            await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { v = 1, type, id, device = "configuration-fixture", data })),
                WebSocketMessageType.Text, true, CancellationToken.None);
        async Task Request(ClientWebSocket socket, string id, string action) =>
            await Send(socket, LanMessage.MicrophoneConfigurationRequest, id, new { action });
        async Task<JsonDocument> Read(ClientWebSocket socket)
        {
            using var deadline = new CancellationTokenSource(5000);
            using var result = new MemoryStream();
            var buffer = new byte[4096];
            WebSocketReceiveResult part;
            do
            {
                part = await socket.ReceiveAsync(buffer, deadline.Token);
                if (part.MessageType != WebSocketMessageType.Text) throw new Exception("expected a text frame");
                result.Write(buffer, 0, part.Count);
                if (result.Length > 65536) throw new Exception("reply exceeds test budget");
            } while (!part.EndOfMessage);
            return JsonDocument.Parse(result.ToArray());
        }
        async Task Heartbeat(ClientWebSocket socket, string label)
        {
            await Send(socket, "heartbeat", RequestId(), new { });
            using var reply = await Read(socket);
            Check(reply.RootElement.GetProperty("type").GetString() == "heartbeat", label);
        }
        static bool Status(JsonDocument reply, string id, string status) =>
            reply.RootElement.GetProperty("type").GetString() == LanMessage.MicrophoneConfigurationReply &&
            reply.RootElement.GetProperty("data").GetProperty("requestId").GetString() == id &&
            reply.RootElement.GetProperty("data").GetProperty("status").GetString() == status;
        async Task<ClientWebSocket> Connect(bool expectedFeature = true, string? clientId = null)
        {
            var socket = new ClientWebSocket();
            try
            {
                await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), CancellationToken.None);
                await Send(socket, "hello", RequestId(), new { auth = LanConfig.PasswordHash,
                    ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), clientId = clientId ?? RequestId() });
                using var welcome = await Read(socket);
                Check(welcome.RootElement.GetProperty("type").GetString() == "welcome" &&
                    welcome.RootElement.GetProperty("data").GetProperty("features").EnumerateArray()
                        .Any(x => x.GetString() == LanMessage.MicrophoneConfigurationFeature) == expectedFeature,
                    expectedFeature ? "authenticated material welcome advertises microphone configuration" :
                        "welcome omits microphone configuration when no provider exists");
                return socket;
            }
            catch { socket.Dispose(); throw; }
        }

        try
        {
            using (var anonymous = new ClientWebSocket())
            {
                await anonymous.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), CancellationToken.None);
                await Request(anonymous, RequestId(), "check");
                await Request(anonymous, RequestId(), "repair");
                await Heartbeat(anonymous, "anonymous check and repair yield no configuration result before the heartbeat barrier");
                Check(checks == 0 && repairs == 0, "anonymous requests invoke neither inspection nor repair provider");
            }
            using (var rejected = new ClientWebSocket())
            {
                await rejected.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), CancellationToken.None);
                await Send(rejected, "hello", RequestId(), new { auth = "wrong", ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
                using var denial = await Read(rejected);
                Check(denial.RootElement.GetProperty("type").GetString() == "auth_fail" && checks == 0 && repairs == 0,
                    "failed authentication cannot advertise the configuration capability or invoke providers");
            }
            hub.MicrophoneConfigurationProvider = null;
            using (var unsupported = await Connect(expectedFeature: false))
            {
                var id = RequestId();
                await Request(unsupported, id, "repair");
                using var reply = await Read(unsupported);
                Check(Status(reply, id, "failed") && !reply.RootElement.GetProperty("data").GetProperty("canRepair").GetBoolean(),
                    "unsupported provider returns a correlated failure without claiming repair");
            }
            hub.MicrophoneConfigurationProvider = provider;

            using (var phone = await Connect())
            {
                var inspect = RequestId();
                await Request(phone, inspect, "check");
                using (var reply = await Read(phone))
                    Check(Status(reply, inspect, "ready") && reply.RootElement.GetProperty("id").GetString() == inspect && checks == 1 && repairs == 0,
                        "canonical UUID inspection is correlated in envelope and data, and never calls repair");
                var limited = RequestId();
                await Request(phone, limited, "repair");
                using (var reply = await Read(phone))
                    Check(Status(reply, limited, "busy") && checks == 1 && repairs == 0,
                        "two-second per-session admission blocks rapid repair after inspection");
                await Task.Delay(2100);
                var repaired = RequestId();
                await Request(phone, repaired, "repair");
                using (var reply = await Read(phone))
                    Check(Status(reply, repaired, "ready") && checks == 1 && repairs == 1,
                        "explicit repair invokes exactly one repair action after the admission interval");
                var invalid = RequestId();
                await Request(phone, invalid, "reset_windows_defaults");
                using (var reply = await Read(phone))
                    Check(Status(reply, invalid, "failed") && repairs == 1 && checks == 1,
                        "unknown actions are rejected before provider dispatch");
                await Request(phone, "not-a-uuid", "repair");
                await Request(phone, Guid.NewGuid().ToString("N"), "repair");
                await Send(phone, LanMessage.MicrophoneConfigurationRequest, RequestId(), new { action = "repair", padding = new string('x', 3000) });
                await Heartbeat(phone, "malformed UUIDs and oversized requests return no result and leave heartbeat responsive");
                Check(checks == 1 && repairs == 1, "invalid IDs and payload bounds reject work without any provider calls");
            }

            using (var throwing = await Connect())
            {
                hub.MicrophoneConfigurationProvider = _ => throw new IOException("test-only-secret");
                var id = RequestId();
                await Request(throwing, id, "repair");
                using (var reply = await Read(throwing))
                    Check(Status(reply, id, "failed") && !reply.RootElement.GetRawText().Contains("test-only-secret", StringComparison.Ordinal),
                        "provider exception returns a correlated bounded failure without leaking exception details");
                await Heartbeat(throwing, "provider exception preserves the material heartbeat");
            }

            var pending = new TaskCompletionSource<Dictionary<string, object?>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            hub.MicrophoneConfigurationProvider = action => { Interlocked.Increment(ref repairs); entered.TrySetResult(); return pending.Task; };
            var sameClient = RequestId();
            using (var old = await Connect(clientId: sameClient))
            {
                var original = RequestId();
                await Request(old, original, "repair");
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var busy = RequestId();
                await Request(old, busy, "check");
                using (var reply = await Read(old))
                    Check(Status(reply, busy, "busy") && repairs == 2,
                        "in-flight repair rejects concurrent inspection without invoking another provider");
                await Heartbeat(old, "in-flight configuration work does not block the material heartbeat");
                old.Abort();
                using var replacement = await Connect(clientId: sameClient);
                pending.SetResult(new() { ["status"] = "ready", ["canRepair"] = true, ["marker"] = "old-socket-only" });
                await Task.Delay(150);
                await Heartbeat(replacement, "late result from the disconnected socket never reaches its authenticated replacement");
                hub.MicrophoneConfigurationProvider = provider;
                var fresh = RequestId();
                await Request(replacement, fresh, "check");
                using var freshReply = await Read(replacement);
                Check(Status(freshReply, fresh, "ready") && checks == 2 && repairs == 2,
                    "replacement session owns fresh rate and in-flight state after old completion");
            }
            Console.WriteLine($"Microphone configuration websocket checks: {passed} passed. Real WsHub/TCP; no Windows audio mutation.");
        }
        finally { hub.Stop(); }
    }
}
