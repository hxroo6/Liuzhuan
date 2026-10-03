using System.IO;
using System.Text.Json;
using Liuzhuan.Utils;

namespace Liuzhuan.Services;

/// <summary>
/// 应用设置 — 持久化到 data/appsettings.json
/// </summary>
public static class AppSettings
{
    private static string SettingsFile => Path.Combine(ConfigService.GetEffectiveDataDir(), "appsettings.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>剪贴板监控：电脑上任意复制 → 自动读取剪贴板加载进流转</summary>
    public static bool ClipboardMonitorEnabled { get; set; } = false;

    /// <summary>收到 HEIC 时默认转为无损 PNG；可关闭或选择 JPEG。</summary>
    public static HeicConversionMode HeicConversion { get; set; } = HeicConversionMode.Png;

    private static string[] _excludedClipboardApps = Array.Empty<string>();
    public static string[] ExcludedClipboardApps => (string[])_excludedClipboardApps.Clone();
    public static DateTimeOffset? ClipboardPausedUntil { get; private set; }
    public static string? ClipboardPrivacyLoadError { get; private set; }
    public static bool IsClipboardPaused => ClipboardPausedUntil > DateTimeOffset.UtcNow;

    /// <summary>Persist first; a failed save must not silently change the active privacy rules.</summary>
    public static void ConfigurePrivacy(IEnumerable<string> exclusions, DateTimeOffset? pausedUntil)
    {
        var normalized = ClipboardPrivacyPolicy.NormalizeApps(exclusions);
        WriteSettings(normalized, pausedUntil);
        _excludedClipboardApps = normalized;
        ClipboardPausedUntil = pausedUntil;
        ClipboardPrivacyLoadError = null;
    }

    static AppSettings() => Load();

    public static void Load()
    {
        ClipboardMonitorEnabled = false;
        HeicConversion = HeicConversionMode.Png;
        _excludedClipboardApps = Array.Empty<string>();
        ClipboardPausedUntil = null;
        ClipboardPrivacyLoadError = null;
        try
        {
            if (!File.Exists(SettingsFile)) return;
            var json = File.ReadAllText(SettingsFile);
            var cfg = JsonSerializer.Deserialize<Dictionary<string, object?>>(json, JsonOpts);
            if (cfg == null) return;
            if (cfg.TryGetValue("HeicConversion", out var modeValue) && modeValue is JsonElement modeElement &&
                modeElement.ValueKind == JsonValueKind.String &&
                Enum.TryParse<HeicConversionMode>(modeElement.GetString(), out var mode) && Enum.IsDefined(typeof(HeicConversionMode), mode))
                HeicConversion = mode;
            if (cfg.TryGetValue("ClipboardMonitorEnabled", out var v) && v is JsonElement el)
                ClipboardMonitorEnabled = el.ValueKind == JsonValueKind.True;
            if (cfg.TryGetValue("ExcludedClipboardApps", out var apps) && apps is JsonElement list)
            {
                if (list.ValueKind == JsonValueKind.Array) _excludedClipboardApps = ClipboardPrivacyPolicy.NormalizeApps(list.Deserialize<string[]>());
                else if (list.ValueKind != JsonValueKind.Null) throw new InvalidDataException("应用排除名单格式无效。");
            }
            if (cfg.TryGetValue("ClipboardPausedUntil", out var pause) && pause is JsonElement p && p.ValueKind != JsonValueKind.Null)
            {
                if (p.ValueKind != JsonValueKind.String || !DateTimeOffset.TryParse(p.GetString(), out var deadline))
                    throw new InvalidDataException("自动采集暂停时间格式无效。");
                ClipboardPausedUntil = deadline;
            }
        }
        catch (Exception ex)
        {
            ClipboardPrivacyLoadError = "自动采集配置读取失败，请检查隐私设置后重新保存。";
            Logger.Error("AppSettings.Load failed: {0}", ex.Message);
        }
    }

    public static void Save()
    {
        try
        {
            if (ClipboardPrivacyLoadError != null) throw new InvalidDataException("请先检查并保存剪贴板隐私设置，原配置已保留。");
            WriteSettings(_excludedClipboardApps, ClipboardPausedUntil);
            Logger.Run("AppSettings saved: ClipboardMonitor={0}", ClipboardMonitorEnabled);
        }
        catch (Exception ex)
        {
            Logger.Error("AppSettings.Save failed: {0}", ex.Message);
        }
    }

    private static void WriteSettings(string[] exclusions, DateTimeOffset? pausedUntil)
    {
        var path = SettingsFile;
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var cfg = new Dictionary<string, object?>
        {
            ["ClipboardMonitorEnabled"] = ClipboardMonitorEnabled,
            ["HeicConversion"] = HeicConversion.ToString(),
            ["ExcludedClipboardApps"] = exclusions,
            ["ClipboardPausedUntil"] = pausedUntil
        };
        try { File.WriteAllText(temp, JsonSerializer.Serialize(cfg, JsonOpts)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
