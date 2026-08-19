using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Liuzhuan.Models;

/// <summary>
/// 素材类型枚举
/// </summary>
public enum MaterialType
{
    Video,
    Image,
    Audio,
    Text,
    Other
}

/// <summary>
/// 素材数据模型 — 仅存储路径索引或文本内容，绝不复制源文件
/// 实现 INotifyPropertyChanged 使 UI 绑定属性变更时自动刷新
/// </summary>
public class MaterialItem : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public MaterialType Type { get; set; }

    /// <summary>本地文件绝对路径（文字类为 null）</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>显示名称</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>文字内容（仅文字类有值）</summary>
    public string TextContent { get; set; } = string.Empty;

    /// <summary>拖入时间戳</summary>
    public DateTime AddedTime { get; set; } = DateTime.Now;

    /// <summary>文件大小（字节，文字类为内容字符数）</summary>
    public long Size { get; set; }

    private string _thumbnailPath = string.Empty;
    /// <summary>缩略图缓存路径（持久化用）</summary>
    public string ThumbnailPath
    {
        get => _thumbnailPath;
        set
        {
            if (_thumbnailPath != value)
            {
                _thumbnailPath = value;
                OnPropertyChanged();
                // 路径变化时重置 ThumbnailSource 缓存（下次 getter 会懒加载新路径）
                _thumbnailSource = null;
                OnPropertyChanged(nameof(ThumbnailSource));
            }
        }
    }

    private ImageSource? _thumbnailSource;
    /// <summary>
    /// 缩略图 ImageSource（UI 绑定用）
    /// Getter 懒加载：若 _thumbnailSource 为 null 且有有效 ThumbnailPath，自动创建 BitmapImage
    /// 解决 JSON 加载后旧素材无缩略图的回归问题
    /// </summary>
    [JsonIgnore]
    public ImageSource? ThumbnailSource
    {
        get
        {
            if (_thumbnailSource != null) return _thumbnailSource;
            // 懒加载：从 ThumbnailPath 创建
            if (!string.IsNullOrEmpty(_thumbnailPath) && File.Exists(_thumbnailPath))
            {
                TryLoadThumbnailSync();
            }
            return _thumbnailSource;
        }
        set
        {
            if (_thumbnailSource != value)
            {
                _thumbnailSource = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>从 ThumbnailPath 同步加载缩略图到 ThumbnailSource（须在 UI 线程调用）</summary>
    public void TryLoadThumbnailSync()
    {
        if (string.IsNullOrEmpty(_thumbnailPath) || !File.Exists(_thumbnailPath))
            return;
        try
        {
            var uri = new Uri(_thumbnailPath, UriKind.Absolute);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = uri;
            bmp.EndInit();
            bmp.Freeze();
            _thumbnailSource = bmp;
            OnPropertyChanged(nameof(ThumbnailSource));
        }
        catch
        {
            _thumbnailSource = null;
        }
    }

    /// <summary>音频/视频时长（秒），无则为 0</summary>
    public double Duration { get; set; }

    private bool _isFavorite;
    /// <summary>是否已收藏（收藏项不会被清空操作移除）</summary>
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite != value)
            {
                _isFavorite = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>是否为文件类素材</summary>
    public bool IsFile => Type != MaterialType.Text;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
