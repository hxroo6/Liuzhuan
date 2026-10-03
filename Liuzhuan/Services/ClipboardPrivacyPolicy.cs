namespace Liuzhuan.Services;

/// <summary>Rules for automatic collection only. Unknown ownership is blocked when exclusions are configured.</summary>
public static class ClipboardPrivacyPolicy
{
    public static string[] NormalizeApps(IEnumerable<string>? apps)
    {
        var result = (apps ?? Array.Empty<string>()).Select(x => x?.Trim() ?? "").Where(x => x.Length > 0)
            .Select(x => x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? x : x + ".exe")
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (result.Length > 50) throw new ArgumentException("最多排除 50 个应用。");
        if (result.Any(x => x.Length > 128 || x.Length <= 4 || x.Any(c => !char.IsLetterOrDigit(c) && c is not ('_' or '-' or '.' or ' '))))
            throw new ArgumentException("请输入进程名称，例如 WeChat.exe；不要填写目录路径。");
        return result;
    }

    public static bool ShouldCapture(DateTimeOffset now, DateTimeOffset? pausedUntil, IReadOnlyList<string> exclusions, string? ownerApp)
    {
        if (pausedUntil > now) return false;
        if (exclusions.Count == 0) return true;
        if (string.IsNullOrWhiteSpace(ownerApp)) return false;
        return !exclusions.Contains(ownerApp, StringComparer.OrdinalIgnoreCase);
    }
}
