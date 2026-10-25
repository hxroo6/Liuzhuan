using System.Net;
using System.Net.Sockets;
using System.Text;
using Liuzhuan.Utils;

namespace Liuzhuan.Services.Lan;

/// <summary>
/// UDP 自动发现固定使用 8901；控制面端口可独立配置并通过 OFFER 返回。
/// 手机端广播 LIUZHUAN_DISCOVER → 电脑端回复 LIUZHUAN_OFFER（IP/端口/设备名）
/// 免去手动输入 IP 的麻烦
/// </summary>
public class UdpDiscovery : IDisposable
{
    public Func<string,int,string>? IdentityOffer {get;set;}
    public const int DiscoveryPort = 8901;
    private readonly object _lifecycleLock = new();
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private const string DiscoverMagic = "LIUZHUAN_DISCOVER";
    private const string OfferMagic = "LIUZHUAN_OFFER";

    public void Start(int wsPort)
    {
        lock (_lifecycleLock)
        {
            if (_udp != null) return;
            if (wsPort is < 1 or > 65534) throw new ArgumentOutOfRangeException(nameof(wsPort));
            var udp = new UdpClient();
            var cts = new CancellationTokenSource();
            try
            {
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
                _udp = udp;
                _cts = cts;
                // 捕获本次实例，避免 Stop 把字段置空或重启后旧任务读到新 socket。
                var token = cts.Token;
                _ = Task.Run(() => ListenLoop(udp, wsPort, token));
                Logger.Run("UdpDiscovery: listening on UDP port {0} (WS port {1})", DiscoveryPort, wsPort);
            }
            catch
            {
                udp.Dispose();
                cts.Dispose();
                _udp = null;
                _cts = null;
                throw;
            }
        }
    }

    public void Stop()
    {
        lock (_lifecycleLock)
        {
            var udp = _udp;
            var cts = _cts;
            _udp = null;
            _cts = null;
            if (udp == null) return;
            try
            {
                cts?.Cancel();
                udp.Dispose();
                Logger.Run("UdpDiscovery: stopped");
            }
            catch (Exception ex)
            {
                Logger.Error("UdpDiscovery.Stop failed: {0}", ex.Message);
            }
            finally { cts?.Dispose(); }
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
                if(msg.StartsWith("LIUZHUAN_DISCOVER_V2|",StringComparison.Ordinal)) {
                    var nonce=msg[21..];
                    if(nonce.Length==32 && nonce.All(Uri.IsHexDigit) && IdentityOffer!=null) {
                        var response=Encoding.UTF8.GetBytes(IdentityOffer(nonce,wsPort));
                        await udp.SendAsync(response,response.Length,result.RemoteEndPoint);
                    }
                    continue;
                }
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
            catch (ObjectDisposedException) when (ct.IsCancellationRequested) { break; }
            catch (SocketException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Logger.Error("UdpDiscovery: {0}", ex.Message);
                // 网卡切换等异常不应形成忙循环；取消时立即退出。
                try { await Task.Delay(150, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    public void Dispose() => Stop();
}
