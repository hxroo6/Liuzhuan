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

    private MaterialType _type;
    private long _sourceRevision;
    public MaterialType Type
    {
        get => _type;
        set
        {
            if (_type == value) return;
            _type = value;
            Interlocked.Increment(ref _sourceRevision);
            OnPropertyChanged();
            OnPropertyChanged(nameof(KindLabel));
            OnPropertyChanged(nameof(DetailLabel));
            OnPropertyChanged(nameof(IsFile));
            OnPropertyChanged(nameof(HasFileSource));
            RefreshSourceStatus();
        }
    }

    /// <summary>来源变化代数，防止 A→B→A 的旧异步结果重新写入；不持久化。</summary>
    [JsonIgnore]
    public long SourceRevision => Interlocked.Read(ref _sourceRevision);

    [JsonIgnore]
    public string KindLabel => Type switch
    {
        MaterialType.Image => "图片", MaterialType.Text => "文字", MaterialType.Video => "视频",
        MaterialType.Audio => "音频", _ => "文件"
    };

    [JsonIgnore]
    public string DetailLabel => Type == MaterialType.Text
        ? $"{TextContent.Length} 字 · {AddedTime:MM/dd}"
        : $"{Utils.FileClassifier.FormatSize(Size)} · {AddedTime:MM/dd}";

    private string _filePath = string.Empty;
    /// <summary>本地文件绝对路径；纯文字为空，文字文件也可有路径。</summary>
    public string FilePath
    {
        get => _filePath;
        set
        {
            var next = value ?? string.Empty;
            if (_filePath != next)
            {
                _filePath = next;
                Interlocked.Increment(ref _sourceRevision);
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasFileSource));
            }
            RefreshSourceStatus();
        }
    }

    private bool _isSourceMissing;
    [JsonIgnore]
    public bool HasFileSource => IsFile || !string.IsNullOrWhiteSpace(_filePath);

    /// <summary>上一次显式检查的文件状态；不把会过期的 Exists 结果写入索引。</summary>
    [JsonIgnore]
    public bool IsSourceMissing => _isSourceMissing;

    [JsonIgnore]
    public string SourceStatusLabel => _isSourceMissing ? "原文件失效" : string.Empty;

    /// <summary>加载、来源修复及用户主动检查时更新状态，不在 UI getter 中访问磁盘。</summary>
    public bool RefreshSourceStatus()
    {
        return ApplySourceStatus(File.Exists(_filePath));
    }

    internal bool ApplySourceStatus(bool exists)
    {
        var missing = HasFileSource && !exists;
        if (_isSourceMissing == missing) return false;
        _isSourceMissing = missing;
        OnPropertyChanged(nameof(IsSourceMissing));
        OnPropertyChanged(nameof(SourceStatusLabel));
        return true;
    }

    /// <summary>显示名称</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>文字内容（仅文字类有值）</summary>
    public string TextContent { get; set; } = string.Empty;

    /// <summary>拖入时间戳</summary>
    public DateTime AddedTime { get; set; } = DateTime.Now;

    private long _size;
    /// <summary>文件大小（字节，纯文字为内容字符数）</summary>
    public long Size
    {
        get => _size;
        set
        {
            if (_size == value) return;
            _size = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DetailLabel));
        }
    }

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
    private double _duration;
    public double Duration
    {
        get => _duration;
        set { if (_duration != value) { _duration = value; OnPropertyChanged(); } }
    }

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

    private string _groupName = string.Empty;
    /// <summary>电脑端自定义分组，空字符串表示未分组。</summary>
    public string GroupName
    {
        get => _groupName;
        set
        {
            var next = value?.Trim() ?? string.Empty;
            if (_groupName == next) return;
            _groupName = next;
            OnOrganizationChanged();
        }
    }

    private string[] _tags = Array.Empty<string>();
    /// <summary>电脑端标签；返回副本，避免直接修改数组绕过通知及保存。</summary>
    public string[] Tags
    {
        get => (string[])_tags.Clone();
        set
        {
            var next = MaterialOrganization.NormalizeTags(value);
            if (_tags.SequenceEqual(next, StringComparer.Ordinal)) return;
            _tags = next;
            OnOrganizationChanged();
        }
    }

    private string _note = string.Empty;
    /// <summary>电脑端备注，不更改素材正文或源文件。</summary>
    public string Note
    {
        get => _note;
        set
        {
            var next = value ?? string.Empty;
            if (_note == next) return;
            _note = next;
            OnOrganizationChanged();
        }
    }

    [JsonIgnore]
    public bool HasMetadata => _groupName.Length > 0 || _tags.Length > 0 || _note.Length > 0;

    [JsonIgnore]
    public string MetadataLabel => string.Join(" · ",
        new[] { _groupName, string.Join(" ", _tags.Select(x => "#" + x)), _note.Length > 0 ? "有备注" : string.Empty }
            .Where(x => x.Length > 0));

    [JsonIgnore]
    public string MetadataSummary => string.Join(Environment.NewLine,
        new[] { _groupName.Length > 0 ? "分组：" + _groupName : string.Empty,
            _tags.Length > 0 ? "标签：" + string.Join("、", _tags) : string.Empty, _note }
            .Where(x => x.Length > 0));

    private void OnOrganizationChanged([CallerMemberName] string? propertyName = null)
    {
        OnPropertyChanged(propertyName);
        OnPropertyChanged(nameof(HasMetadata));
        OnPropertyChanged(nameof(MetadataLabel));
        OnPropertyChanged(nameof(MetadataSummary));
    }

    /// <summary>是否为文件类素材</summary>
    public bool IsFile => Type != MaterialType.Text;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
