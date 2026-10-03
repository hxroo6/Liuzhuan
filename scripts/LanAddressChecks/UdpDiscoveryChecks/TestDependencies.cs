using System.Collections.Concurrent;

namespace Liuzhuan.Utils;

// 协议/生命周期检查不枚举真实网卡，不写用户运行日志。
internal static class LanNetUtil
{
    internal static string GetLanIp(string? peer = null) => "192.168.50.10";
}

internal static class Logger
{
    internal static readonly ConcurrentQueue<string> Errors = new();
    internal static void Run(string message, params object[] args) { }
    internal static void Error(string message, params object[] args) => Errors.Enqueue(string.Format(message, args));
}
