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

    static AppSettings() => Load();

    public static void Load()
    {
        try
        {
            if (!File.Exists(SettingsFile)) return;
            var json = File.ReadAllText(SettingsFile);
            var cfg = JsonSerializer.Deserialize<Dictionary<string, object?>>(json, JsonOpts);
            if (cfg == null) return;
            if (cfg.TryGetValue("ClipboardMonitorEnabled", out var v) &&
                v is JsonElement el && el.ValueKind == JsonValueKind.True)
            {
                ClipboardMonitorEnabled = true;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("AppSettings.Load failed: {0}", ex.Message);
        }
    }

    public static void Save()
    {
        try
        {
            var dir = ConfigService.GetEffectiveDataDir();
            Directory.CreateDirectory(dir);
            var cfg = new Dictionary<string, object?>
            {
                ["ClipboardMonitorEnabled"] = ClipboardMonitorEnabled
            };
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(cfg, JsonOpts));
            Logger.Run("AppSettings saved: ClipboardMonitor={0}", ClipboardMonitorEnabled);
        }
        catch (Exception ex)
        {
            Logger.Error("AppSettings.Save failed: {0}", ex.Message);
        }
    }
}
