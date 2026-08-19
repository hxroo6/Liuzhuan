using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Liuzhuan.Utils;

/// <summary>局域网 IP 工具（电脑端多处共用）</summary>
public static class LanNetUtil
{
    /// <summary>
    /// 获取本机 LAN 段 IPv4（过滤回环/虚拟网卡，优先 192.168./10./172. 内网段）
    /// </summary>
    public static string GetLanIp()
    {
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                            && !n.Description.Contains("VMware", StringComparison.OrdinalIgnoreCase)
                            && !n.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
                            && !n.Description.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase)
                            && !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork
                            && !System.Net.IPAddress.IsLoopback(a.Address))
                .Select(a => a.Address.ToString())
                .ToList();

            foreach (var ip in candidates)
                if (ip.StartsWith("192.168.") || ip.StartsWith("10.") || ip.StartsWith("172."))
                    return ip;
            return candidates.FirstOrDefault() ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }
}
