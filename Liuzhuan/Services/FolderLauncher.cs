using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Liuzhuan.Services;

/// <summary>当前电脑的文件管理器偏好；程序路径不随迁移包复制到其他电脑。</summary>
public static class FolderLauncher
{
    public record Preference(string Executable="", string Arguments="{folder}");
    private static string SettingsFile=>Path.Combine(ConfigService.GetEffectiveDataDir(),"file-browser.json");
    public static Preference Load() {
        try { return File.Exists(SettingsFile)?JsonSerializer.Deserialize<Preference>(File.ReadAllText(SettingsFile)) ?? new():new(); }
        catch { return new(); }
    }
    public static IReadOnlyList<string> BuildArguments(string template,string folder) {
        if(!template.Contains("{folder}",StringComparison.Ordinal)) throw new ArgumentException("参数必须包含 {folder} 占位符。");
        var args=new List<string>();var current=new StringBuilder();bool quoted=false,started=false;
        foreach(char c in template) {
            if(c=='"') { quoted=!quoted;started=true; }
            else if(char.IsWhiteSpace(c) && !quoted) { if(started) {args.Add(current.ToString().Replace("{folder}",folder,StringComparison.Ordinal));current.Clear();started=false;} }
            else {current.Append(c);started=true;}
        }
        if(quoted) throw new ArgumentException("参数中的双引号未闭合。");
        if(started) args.Add(current.ToString().Replace("{folder}",folder,StringComparison.Ordinal));
        return args;
    }
    public static void Save(Preference preference) {
        if(!string.IsNullOrWhiteSpace(preference.Executable)) {
            if(!Path.IsPathFullyQualified(preference.Executable) || !File.Exists(preference.Executable) ||
                !string.Equals(Path.GetExtension(preference.Executable),".exe",StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请选择存在的文件管理器 EXE。");
            BuildArguments(preference.Arguments,"sample folder");
        }
        var path=SettingsFile;var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {File.WriteAllText(temp,JsonSerializer.Serialize(preference));File.Move(temp,path,true);}
        finally {if(File.Exists(temp)) File.Delete(temp);}
    }
    public static ProcessStartInfo CreateStartInfo(Preference preference,string folder) {
        var start=new ProcessStartInfo(string.IsNullOrWhiteSpace(preference.Executable)?"explorer.exe":preference.Executable) {UseShellExecute=false};
        if(string.IsNullOrWhiteSpace(preference.Executable)) start.ArgumentList.Add(folder);
        else foreach(var arg in BuildArguments(preference.Arguments,folder)) start.ArgumentList.Add(arg);
        return start;
    }
    public static void Open(string folder) {
        var path=Path.GetFullPath(folder);
        if(!Directory.Exists(path)) throw new DirectoryNotFoundException("目录不存在，可能已移动或被删除。");
        Process.Start(CreateStartInfo(Load(),path));
    }
}
