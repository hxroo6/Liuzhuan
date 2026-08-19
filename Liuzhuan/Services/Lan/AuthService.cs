using System.Security.Cryptography;
using System.Text;
using Liuzhuan.Utils;

namespace Liuzhuan.Services.Lan;

/// <summary>
/// 认证服务 — 口令校验 + 时间戳防重放
/// 协议：客户端连接 WS 时带 ?auth=sha256(口令)&device=X&ts=Unix秒
/// 校验：① ts 与服务器时间差 < 5min ② sha256(口令) 匹配
/// </summary>
public static class AuthService
{
    public const int MaxTsSkewSeconds = 300; // ±5 分钟

    /// <summary>口令 → 认证哈希（两端用同一算法）</summary>
    public static string HashPassword(string password)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>校验认证参数</summary>
    public static (bool ok, string reason) Validate(string? auth, string? ts)
    {
        if (string.IsNullOrEmpty(auth) || string.IsNullOrEmpty(ts))
            return (false, "缺少认证参数");

        if (!long.TryParse(ts, out var tsValue))
            return (false, "时间戳格式错误");

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(now - tsValue) > MaxTsSkewSeconds)
            return (false, "时间偏差过大");

        var stored = LanConfig.PasswordHash;
        if (string.IsNullOrEmpty(stored))
            return (false, "服务器未启用");

        var ok = string.Equals(auth, stored, StringComparison.OrdinalIgnoreCase);
        if (!ok)
            Logger.Run("Lan: auth failed from device, ts={0}", ts);
        return ok ? (true, "ok") : (false, "口令错误");
    }
}
