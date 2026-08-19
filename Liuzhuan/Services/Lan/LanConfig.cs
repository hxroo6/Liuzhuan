using System.IO;
using System.Text.Json;
using Liuzhuan.Utils;

namespace Liuzhuan.Services.Lan;

/// <summary>
/// 局域网服务器配置 — 持久化到 data/lan.json
/// </summary>
public static class LanConfig
{
    private static string ConfigFile => Path.Combine(ConfigService.GetEffectiveDataDir(), "lan.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>服务器开关</summary>
    public static bool Enabled { get; set; } = false;

    /// <summary>控制面端口（数据面自动 +1）</summary>
    public static int Port { get; set; } = 8899;

    /// <summary>口令</summary>
    public static string Password { get; set; } = "";

    /// <summary>口令哈希（认证用）</summary>
    public static string PasswordHash => AuthService.HashPassword(Password);

    static LanConfig() => Load();

    public static void Load()
    {
        try
        {
            if (!File.Exists(ConfigFile)) return;
            var json = File.ReadAllText(ConfigFile);
            var cfg = JsonSerializer.Deserialize<Dictionary<string, object?>>(json, JsonOpts);
            if (cfg == null) return;
            if (cfg.TryGetValue("Enabled", out var en) && en is JsonElement ej && ej.ValueKind == System.Text.Json.JsonValueKind.True)
                Enabled = true;
            if (cfg.TryGetValue("Port", out var pt) && pt is JsonElement pj && pj.TryGetInt32(out var p) && p is > 0 and < 65535)
                Port = p;
            if (cfg.TryGetValue("Password", out var pw) && pw is JsonElement pwj && pwj.ValueKind == System.Text.Json.JsonValueKind.String)
                Password = pwj.GetString() ?? "";
        }
        catch (Exception ex)
        {
            Logger.Error("LanConfig.Load failed: {0}", ex.Message);
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
                ["Enabled"] = Enabled,
                ["Port"] = Port,
                ["Password"] = Password
            };
            File.WriteAllText(ConfigFile, JsonSerializer.Serialize(cfg, JsonOpts));
            Logger.Run("LanConfig saved: port={0} enabled={1}", Port, Enabled);
        }
        catch (Exception ex)
        {
            Logger.Error("LanConfig.Save failed: {0}", ex.Message);
        }
    }

    /// <summary>生成 6 位随机纯数字口令（易输入）</summary>
    public static string GeneratePassword()
    {
        var rng = new Random();
        var sb = new System.Text.StringBuilder(6);
        for (int i = 0; i < 6; i++) sb.Append(rng.Next(0, 10));
        return sb.ToString();
    }
}
