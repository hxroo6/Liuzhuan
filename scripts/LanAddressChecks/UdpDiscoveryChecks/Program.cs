using System.Net;
using System.Net.Sockets;
using System.Text;
using Liuzhuan.Services.Lan;
using Liuzhuan.Utils;

internal static class Program
{
    private static int _checks;
    private static void Check(bool value, string label)
    {
        if (!value) throw new Exception(label);
        _checks++;
        Console.WriteLine("PASS " + label);
    }

    // 必须先停止真实流转应用；不启动 App，不读取库或网络配置。
    private static UdpClient ReservePort()
    {
        var socket = new UdpClient { ExclusiveAddressUse = true };
        try { socket.Client.Bind(new IPEndPoint(IPAddress.Any, UdpDiscovery.DiscoveryPort)); }
        catch { socket.Dispose(); throw new InvalidOperationException("UDP 8901 is occupied; stop the desktop app before running these checks."); }
        return socket;
    }

    private static async Task<string?> ReadOffer(UdpClient client, int timeoutMs = 500)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        try
        {
            var response = await client.ReceiveAsync(timeout.Token);
            return Encoding.UTF8.GetString(response.Buffer);
        }
        catch (OperationCanceledException) { return null; }
    }

    private static async Task Send(UdpClient client, int destinationPort, string message = "LIUZHUAN_DISCOVER")
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await client.SendAsync(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, destinationPort));
    }

    private static async Task CheckOffer(UdpClient client, int wsPort, string label)
    {
        await Send(client, UdpDiscovery.DiscoveryPort);
        var offer = await ReadOffer(client);
        var parts = offer?.Split('|', 4);
        Check(parts is { Length: 4 } && parts[0] == "LIUZHUAN_OFFER" && parts[1] == "192.168.50.10"
            && parts[2] == wsPort.ToString() && parts[3] == Environment.MachineName, label);
    }

    private static async Task Main()
    {
        using (ReservePort()) { }
        Check(UdpDiscovery.DiscoveryPort == 8901, "discovery uses the Android-compatible fixed port");
        using var discovery = new UdpDiscovery();
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

        discovery.Start(8899);
        await CheckOffer(client, 8899, "default WS port is offered on fixed UDP port");
        discovery.Start(19099);
        await CheckOffer(client, 8899, "duplicate Start preserves the active service");
        Check(await ReadOffer(client, 100) == null, "one request produces one offer after duplicate Start");
        await Send(client, UdpDiscovery.DiscoveryPort, "OTHER_DISCOVER");
        Check(await ReadOffer(client, 100) == null, "unrelated datagrams produce no offer");

        discovery.Stop();
        discovery.Start(19099);
        await CheckOffer(client, 19099, "custom WS port remains discoverable on UDP 8901");
        // Windows 对未监听的 UDP 端口会回 ICMP 并令下一次 Receive 报 10054。
        // 静默接收器让负向探测只验证没有 OFFER，不依赖平台的 ICMP 行为。
        using (var silentLegacyReceiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 19101)))
        {
            await Send(client, 19101);
            Check(await ReadOffer(client, 100) == null, "discovery no longer requires custom WS port plus two");
        }

        discovery.Stop();
        discovery.Start(65534);
        await CheckOffer(client, 65534, "highest valid WS port does not overflow the discovery port");
        discovery.Stop();
        discovery.Stop();
        using (ReservePort()) { }
        Check(true, "Stop is idempotent and releases UDP 8901");

        using (ReservePort())
        {
            var failed = false;
            try { discovery.Start(19099); }
            catch (SocketException) { failed = true; }
            Check(failed, "occupied discovery port reports bind failure");
        }
        discovery.Start(19099);
        await CheckOffer(client, 19099, "same instance can start after a failed bind");
        discovery.Stop();

        var invalid = false;
        try { discovery.Start(65535); }
        catch (ArgumentOutOfRangeException) { invalid = true; }
        Check(invalid, "invalid WS port is rejected before opening discovery");
        for (var i = 0; i < 30; i++) { discovery.Start(19099); discovery.Stop(); }
        discovery.Start(19099);
        await CheckOffer(client, 19099, "rapid stop and restart retain one responsive listener");
        Check(await ReadOffer(client, 100) == null, "rapid lifecycle changes produce no duplicate offer");
        discovery.Stop();
        await Task.Delay(200);
        Check(Logger.Errors.IsEmpty, "normal stop and restart have no disposal or stale-task errors");
        Console.WriteLine($"All {_checks} UDP discovery checks passed.");
    }
}
