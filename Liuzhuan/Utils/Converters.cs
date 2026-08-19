using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Liuzhuan.Models;
using Liuzhuan.Utils;

namespace Liuzhuan.Utils;

/// <summary>视频类型显示徽章</summary>
public class VideoVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is MaterialType type)
            return type == MaterialType.Video ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>素材类型 → 图标字符</summary>
public class TypeIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is MaterialType type)
        {
            return type switch
            {
                MaterialType.Video => "🎬",
                MaterialType.Image => "🖼",
                MaterialType.Audio => "🎵",
                MaterialType.Text => "📝",
                MaterialType.Other => "📄",
                _ => "📄"
            };
        }
        return "📄";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>文件路径缩短显示</summary>
public class PathShortConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string path && !string.IsNullOrEmpty(path))
        {
            if (path.Length <= 40) return path;
            var dir = Path.GetDirectoryName(path);
            var fileName = Path.GetFileName(path);
            if (string.IsNullOrEmpty(dir)) return fileName;
            return ".../" + fileName;
        }
        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>时长格式化</summary>
public class DurationConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double seconds)
            return FileClassifier.FormatDuration(seconds);
        return "--:--";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>空字符串 → Visible，非空 → Collapsed（用于无缩略图时显示占位图标）</summary>
public class EmptyToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string s)
            return string.IsNullOrEmpty(s) ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>
/// 字符串路径 → ImageSource 转换器
/// 解决 WPF 默认 ImageSourceConverter 在空字符串→有效路径变化时不重新转换的问题。
/// 空路径返回 null（让 Image 不显示源，占位图标接管），有效路径创建 BitmapImage(OnLoad) 并 Freeze。
/// </summary>
public class StringToImageConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string path && !string.IsNullOrEmpty(path) && File.Exists(path))
        {
            try
            {
                var uri = new Uri(path, UriKind.Absolute);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = uri;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
