using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Liuzhuan.Utils;

/// <summary>局域网 IP 工具（电脑端多处共用）</summary>
public static class LanNetUtil
{
    public record LanAddress(string Address, int PrefixLength, string Name, string Description,
        NetworkInterfaceType Type, bool HasGateway)
    {
        public string Label => $"{Name} · {Address}";
    }

    private static readonly string[] VirtualNames =
        { "virtual", "vmware", "hyper-v", "wintun", "wireguard", "tailscale", "zerotier", "vgate", "tap-", "vpn", "docker", "wsl" };

    // 不依赖网卡枚举顺序；发现响应优先手机所在子网，二维码优先有网关的真实 LAN。
    public static IReadOnlyList<LanAddress> SelectAddresses(IEnumerable<LanAddress> addresses, string? peer = null) =>
        addresses.Where(a =>
            a.Type is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet
            && a.PrefixLength is >= 1 and <= 30
            && !VirtualNames.Any(word => (a.Name + " " + a.Description).Contains(word, StringComparison.OrdinalIgnoreCase))
            && IPAddress.TryParse(a.Address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork
            && ip.GetAddressBytes()[0] is > 0 and < 224 && !IPAddress.IsLoopback(ip)
            && !(ip.GetAddressBytes()[0] == 169 && ip.GetAddressBytes()[1] == 254))
        .OrderByDescending(a => peer != null && SameSubnet(a.Address, peer, a.PrefixLength))
        .ThenByDescending(a => a.HasGateway)
        .ThenByDescending(a => a.Type == NetworkInterfaceType.Wireless80211)
        .ThenBy(a => a.Name, StringComparer.Ordinal).ThenBy(a => a.Address, StringComparer.Ordinal)
        .DistinctBy(a => a.Address).ToList();

    private static bool SameSubnet(string local, string peer, int prefix)
    {
        if (!IPAddress.TryParse(peer, out var remote) || remote.AddressFamily != AddressFamily.InterNetwork) return false;
        var left = IPAddress.Parse(local).GetAddressBytes(); var right = remote.GetAddressBytes();
        for (int i = 0; i < 4; i++)
        {
            int bits = Math.Clamp(prefix - i * 8, 0, 8);
            int mask = (0xff << (8 - bits)) & 0xff;
            if ((left[i] & mask) != (right[i] & mask)) return false;
        }
        return true;
    }

    public static IReadOnlyList<LanAddress> GetLanAddresses(string? peer = null)
    {
        var addresses = new List<LanAddress>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                    var props = adapter.GetIPProperties();
                    bool gateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                    foreach (var ip in props.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                        addresses.Add(new(ip.Address.ToString(), ip.PrefixLength, adapter.Name, adapter.Description, adapter.NetworkInterfaceType, gateway));
                }
                catch (NetworkInformationException) { /* 热拔插：跳过失效网卡。 */ }
            }
        }
        catch (NetworkInformationException) { }
        return SelectAddresses(addresses, peer);
    }

    public static string GetLanIp(string? peer = null) => GetLanAddresses(peer).FirstOrDefault()?.Address ?? "127.0.0.1";
}
