using System.Net;
using System.Net.Sockets;
using System.Text;
using Liuzhuan.Utils;

namespace Liuzhuan.Services.Lan;

/// <summary>
/// UDP 自动发现（端口 = 控制面 + 2，默认 8901）
/// 手机端广播 LIUZHUAN_DISCOVER → 电脑端回复 LIUZHUAN_OFFER（IP/端口/设备名）
/// 免去手动输入 IP 的麻烦
/// </summary>
public class UdpDiscovery : IDisposable
{
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private const string DiscoverMagic = "LIUZHUAN_DISCOVER";
    private const string OfferMagic = "LIUZHUAN_OFFER";

    public void Start(int wsPort)
    {
        if (_udp != null) return;
        var port = wsPort + 2;
        _udp = new UdpClient();
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ListenLoop(_udp, wsPort, _cts.Token));
        Logger.Run("UdpDiscovery: listening on UDP port {0}", port);
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _udp?.Close();
            _udp = null;
            _cts = null;
            Logger.Run("UdpDiscovery: stopped");
        }
        catch (Exception ex)
        {
            Logger.Error("UdpDiscovery.Stop failed: {0}", ex.Message);
        }
    }

    private async Task ListenLoop(UdpClient udp, int wsPort, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(ct);
                var msg = Encoding.UTF8.GetString(result.Buffer).Trim();
                if (msg != DiscoverMagic) continue;

                // 收到探测 → 回复 OFFER（本机 IP + WS 端口 + 设备名）
                var ip = LanNetUtil.GetLanIp(result.RemoteEndPoint.Address.ToString());
                if (ip == "127.0.0.1") continue;
                var device = Environment.MachineName;
                var offer = $"{OfferMagic}|{ip}|{wsPort}|{device}";
                var bytes = Encoding.UTF8.GetBytes(offer);
                await udp.SendAsync(bytes, bytes.Length, result.RemoteEndPoint);
                Logger.Run("UdpDiscovery: answered discover from {0}", result.RemoteEndPoint.Address);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Logger.Error("UdpDiscovery: {0}", ex.Message);
            }
        }
    }

    public void Dispose() => Stop();
}
