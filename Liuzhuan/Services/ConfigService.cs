using System.IO;
using System.Text.Json;

namespace Liuzhuan.Services;

/// <summary>
/// 配置服务 — 读写 data/config.json，管理数据目录路径
/// </summary>
public static class ConfigService
{
    private static string ConfigFile => Path.Combine(App.DataDir, "config.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>已配置的数据目录（空则使用默认 App.DataDir）</summary>
    public static string DataDir
    {
        get
        {
            try
            {
                if (File.Exists(ConfigFile))
                {
                    var json = File.ReadAllText(ConfigFile);
                    var config = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOpts);
                    if (config != null && config.TryGetValue("DataDir", out var dir) && !string.IsNullOrWhiteSpace(dir))
                        return dir;
                }
            }
            catch { }
            return App.DataDir; // 默认：exe/../data
        }
        set
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigFile)!);
            var config = new Dictionary<string, string> { ["DataDir"] = value };
            File.WriteAllText(ConfigFile, JsonSerializer.Serialize(config, JsonOpts));
        }
    }

    /// <summary>重置为默认路径</summary>
    public static void ResetToDefault() => SetDataDir("");

    /// <summary>设置自定义数据目录</summary>
    public static void SetDataDir(string path)
    {
        DataDir = string.IsNullOrWhiteSpace(path) ? App.DataDir : path;
    }

    /// <summary>获取有效数据目录（解析后）</summary>
    public static string GetEffectiveDataDir()
    {
        var dir = DataDir;
        try { dir = Path.GetFullPath(dir); } catch { dir = App.DataDir; }
        Directory.CreateDirectory(dir);
        return dir;
    }
}
