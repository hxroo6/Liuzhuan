using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Liuzhuan.Utils;

namespace Liuzhuan.Services;

/// <summary>
/// 开机自启管理 — 通过注册表 HKCU\...\Run 实现
/// </summary>
public static class StartupService
{
    private const string AppName = "Liuzhuan";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>获取当前可执行文件路径</summary>
    private static string GetExecutablePath()
    {
        return Process.GetCurrentProcess().MainModule?.FileName
            ?? Environment.ProcessPath
            ?? string.Empty;
    }

    /// <summary>检查是否已启用开机自启</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            var value = key?.GetValue(AppName) as string;
            return !string.IsNullOrEmpty(value);
        }
        catch (Exception ex)
        {
            Logger.Error("StartupService.IsEnabled failed: {0}", ex.Message);
            return false;
        }
    }

    /// <summary>启用或禁用开机自启</summary>
    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key == null) return;

            if (enabled)
            {
                var exePath = GetExecutablePath();
                if (!string.IsNullOrEmpty(exePath))
                {
                    key.SetValue(AppName, $"\"{exePath}\"");
                    Logger.Run("StartupService: enabled auto-start, path={0}", exePath);
                }
            }
            else
            {
                key.DeleteValue(AppName, false);
                Logger.Run("StartupService: disabled auto-start");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("StartupService.SetEnabled failed: {0}", ex.Message);
        }
    }
}
