using System.IO;
using Liuzhuan.Models;

namespace Liuzhuan.Utils;

/// <summary>
/// 文件分类器 — 根据扩展名判断素材类型
/// </summary>
public static class FileClassifier
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".avi", ".mkv", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".ts", ".3gp", ".rm", ".rmvb"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".tiff", ".tif", ".ico", ".svg", ".heic", ".heif"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".wma", ".aiff", ".opus", ".amr"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".log", ".csv", ".json", ".xml", ".yaml", ".yml", ".ini", ".conf", ".bat", ".ps1", ".py", ".cs", ".js", ".ts", ".html", ".css"
    };

    /// <summary>根据文件扩展名分类素材类型，无法识别返回 Other</summary>
    public static MaterialType ClassifyByExtension(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext)) return MaterialType.Other;

        if (VideoExtensions.Contains(ext)) return MaterialType.Video;
        if (ImageExtensions.Contains(ext)) return MaterialType.Image;
        if (AudioExtensions.Contains(ext)) return MaterialType.Audio;
        if (TextExtensions.Contains(ext)) return MaterialType.Text;

        return MaterialType.Other;
    }

    /// <summary>判断文件是否为支持的素材类型</summary>
    public static bool IsSupported(string filePath)
    {
        return true; // 支持所有格式
    }

    /// <summary>格式化时长（秒 → mm:ss 或 hh:mm:ss）</summary>
    public static string FormatDuration(double seconds)
    {
        if (seconds <= 0) return "--:--";
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? ts.ToString(@"h\:mm\:ss")
            : ts.ToString(@"mm\:ss");
    }

    /// <summary>格式化文件大小</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}
