using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Liuzhuan.Models;

namespace Liuzhuan.Services;

public sealed record ImageProcessingSource(string Id, string DisplayName, string FilePath);
public sealed record ImageProcessingItemResult(string Id, string DisplayName, string SourcePath, string? OutputPath,
    int SourceWidth, int SourceHeight, int OutputWidth, int OutputHeight, long InputBytes, long OutputBytes, string? Error)
{
    public bool Succeeded => OutputPath != null && Error == null;
}
public sealed record ImageProcessingProgress(int CompletedItems, int TotalItems, string CurrentName,
    string Stage, ImageProcessingItemResult? ItemResult = null);
public sealed record ImageProcessingResult(string? DirectoryPath, IReadOnlyList<ImageProcessingItemResult> Items)
{
    public int SuccessCount => Items.Count(item => item.Succeeded);
    public int FailureCount => Items.Count - SuccessCount;
}

/// <summary>批量生成图片副本；WIC 与渲染只在独立 STA 工作线程执行，不访问素材库。</summary>
public static class ImageProcessingService
{
    // 限制解码后的像素数，避免损坏文件或超大图片把桌面进程内存耗尽。
    private const long MaxPixels = 64_000_000;
    private const int MaxDimension = 32768;

    public static IReadOnlyList<ImageProcessingSource> Snapshot(IEnumerable<MaterialItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return Array.AsReadOnly(items.Where(item => item.Type == MaterialType.Image)
            .Select(item => new ImageProcessingSource(item.Id, item.DisplayName, item.FilePath)).ToArray());
    }

    /// <summary>
    /// 输出独立子目录，同名编号，重复 Id 保留第一项。单项失败继续处理并进入结果；取消则清理本批输出。
    /// 多帧输入仅使用第一帧；输出不复制源元数据，JPEG 使用白色背景。
    /// </summary>
    public static Task<ImageProcessingResult> ProcessAsync(string destinationParent, IReadOnlyList<ImageProcessingSource> sources,
        ImageProcessingOptions options, IProgress<ImageProcessingProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        var snapshot = sources.ToArray();
        var completion = new TaskCompletionSource<ImageProcessingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try { completion.TrySetResult(Process(destinationParent, snapshot, options, progress, cancellationToken)); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { completion.TrySetCanceled(cancellationToken); }
            catch (Exception exception) { completion.TrySetException(exception); }
        }) { IsBackground = true, Name = "Liuzhuan image processing" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        return completion.Task;
    }

    private static ImageProcessingResult Process(string destinationParent, ImageProcessingSource[] sources,
        ImageProcessingOptions options, IProgress<ImageProcessingProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(typeof(ImageOutputFormat), options.Format) || options.JpegQuality is < 1 or > 100
            || options.MaxEdge is <= 0 or > MaxDimension) throw new ArgumentException("图片处理参数无效。");
        var parent = Path.GetFullPath(destinationParent);
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("请选择已存在的图片保存文件夹。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var selected = new List<ImageProcessingSource>();
        foreach (var source in sources)
        {
            if (source == null || string.IsNullOrWhiteSpace(source.Id)) throw new ArgumentException("图片快照无效，请重新选择。");
            if (ids.Add(source.Id)) selected.Add(source);
        }
        if (selected.Count == 0) throw new ArgumentException("请先选择至少一张图片。");
        var root = ContainedPath(parent, "流转图片-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException("图片输出目录已存在，请重试。");
        Directory.CreateDirectory(root);
        var createdFiles = new List<string>();
        var results = new List<ImageProcessingItemResult>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var item in selected)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new(results.Count, selected.Count, item.DisplayName, "正在处理图片"));
                ct.ThrowIfCancellationRequested();
                ImageProcessingItemResult result;
                string? outputPath = null;
                int originalWidth = 0, originalHeight = 0;
                long originalBytes = 0;
                try
                {
                    if (string.IsNullOrWhiteSpace(item.FilePath) || !File.Exists(item.FilePath))
                        throw new FileNotFoundException("原图片不存在，请重新选择文件。");
                    var inputPath = Path.GetFullPath(item.FilePath);
                    using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    originalBytes = input.Length;
                    // OnDemand 先取尺寸并检查边界，渲染/编码完成前保持来源只读锁。
                    var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
                    if (decoder.Frames.Count == 0) throw new InvalidDataException("图片中没有可读取的画面。");
                    BitmapSource frame = decoder.Frames[0];
                    if (frame.PixelWidth > MaxDimension || frame.PixelHeight > MaxDimension
                        || (long)frame.PixelWidth * frame.PixelHeight > MaxPixels)
                        throw new InvalidDataException("图片过大（最多 6400 万像素，单边不超过 32768 像素），请先缩小后再处理。");
                    frame = ApplyOrientation(frame, ReadOrientation(decoder.Frames[0]));
                    originalWidth = frame.PixelWidth;
                    originalHeight = frame.PixelHeight;
                    var scale = options.MaxEdge.HasValue ? Math.Min(1.0, (double)options.MaxEdge.Value / Math.Max(originalWidth, originalHeight)) : 1.0;
                    var width = Math.Max(1, (int)Math.Round(originalWidth * scale));
                    var height = Math.Max(1, (int)Math.Round(originalHeight * scale));
                    ct.ThrowIfCancellationRequested();
                    var visual = new DrawingVisual();
                    RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
                    using (var drawing = visual.RenderOpen())
                    {
                        var bounds = new Rect(0, 0, width, height);
                        if (options.Format == ImageOutputFormat.Jpeg) drawing.DrawRectangle(Brushes.White, null, bounds);
                        drawing.DrawImage(frame, bounds);
                    }
                    var rendered = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    rendered.Render(visual);
                    ct.ThrowIfCancellationRequested();
                    var extension = options.Format == ImageOutputFormat.Jpeg ? ".jpg" : ".png";
                    outputPath = ContainedPath(root, UniqueName(Path.GetFileNameWithoutExtension(inputPath), extension, names));
                    BitmapEncoder encoder = options.Format == ImageOutputFormat.Jpeg
                        ? new JpegBitmapEncoder { QualityLevel = options.JpegQuality } : new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(rendered));
                    using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        createdFiles.Add(outputPath);
                        using var cancellable = new CancellationWriteStream(output, ct);
                        encoder.Save(cancellable);
                    }
                    ct.ThrowIfCancellationRequested();
                    result = new(item.Id, item.DisplayName, inputPath, outputPath, originalWidth, originalHeight,
                        width, height, originalBytes, new FileInfo(outputPath).Length, null);
                }
                catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
                catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
                {
                    if (outputPath != null && createdFiles.Contains(outputPath)) TryDeleteOwnedFile(root, outputPath);
                    var message = exception switch
                    {
                        FileNotFoundException => "原图片不存在，请重新选择文件。",
                        FileFormatException or NotSupportedException => "无法解码图片。HEIC 等格式需要电脑已安装相应图片解码器。",
                        _ => exception.Message
                    };
                    result = new(item.Id, item.DisplayName, item.FilePath, null, originalWidth, originalHeight, 0, 0, originalBytes, 0, message);
                }
                results.Add(result);
                progress?.Report(new(results.Count, selected.Count, item.DisplayName, result.Succeeded ? "已完成" : "处理失败", result));
            }
            ct.ThrowIfCancellationRequested();
            string? directory = root;
            if (results.All(item => !item.Succeeded))
            {
                Cleanup(root, createdFiles);
                directory = null;
            }
            return new(directory, Array.AsReadOnly(results.ToArray()));
        }
        catch { Cleanup(root, createdFiles); throw; }
    }

    private static int ReadOrientation(BitmapFrame frame)
    {
        if (frame.Metadata is not BitmapMetadata metadata) return 1;
        foreach (var query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
        {
            try
            {
                if (metadata.ContainsQuery(query) && metadata.GetQuery(query) is object value)
                {
                    var orientation = Convert.ToInt32(value);
                    if (orientation is >= 1 and <= 8) return orientation;
                }
            }
            catch (Exception exception) when (exception is NotSupportedException or ArgumentException or InvalidCastException or OverflowException) { }
        }
        return 1;
    }

    private static BitmapSource ApplyOrientation(BitmapSource frame, int orientation)
    {
        var matrix = orientation switch
        {
            2 => new Matrix(-1, 0, 0, 1, 0, 0), 3 => new Matrix(-1, 0, 0, -1, 0, 0),
            4 => new Matrix(1, 0, 0, -1, 0, 0), 5 => new Matrix(0, 1, 1, 0, 0, 0),
            6 => new Matrix(0, 1, -1, 0, 0, 0), 7 => new Matrix(0, -1, -1, 0, 0, 0),
            8 => new Matrix(0, -1, 1, 0, 0, 0), _ => Matrix.Identity
        };
        return matrix.IsIdentity ? frame : new TransformedBitmap(frame, new MatrixTransform(matrix));
    }

    private static string UniqueName(string stem, string extension, HashSet<string> names)
    {
        stem = new string(stem.Select(character => char.IsControl(character) || "<>:\"/\\|?*".Contains(character) ? '_' : character).ToArray()).Trim().TrimEnd('.', ' ');
        if (stem.Length == 0 || stem is "." or "..") stem = "图片";
        var firstPart = stem.Split('.')[0].TrimEnd('.', ' ');
        if (new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" }.Contains(firstPart, StringComparer.OrdinalIgnoreCase)
            || (firstPart.Length == 4 && (firstPart.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || firstPart.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && "0123456789¹²³".Contains(firstPart[3]))) stem = "_" + stem;
        for (int index = 1; ; index++)
        {
            var suffix = index == 1 ? "" : " (" + index + ")";
            int length = Math.Min(stem.Length, 160 - suffix.Length - extension.Length);
            if (length > 0 && char.IsHighSurrogate(stem[length - 1])) length--;
            var name = stem[..length].TrimEnd('.', ' ') + suffix + extension;
            if (names.Add(name)) return name;
        }
    }

    private static string ContainedPath(string parent, string name)
    {
        var prefix = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(prefix, name));
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetDirectoryName(path)?.TrimEnd(Path.DirectorySeparatorChar), prefix.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new IOException("图片输出文件名无效。");
        return path;
    }

    private static void TryDeleteOwnedFile(string root, string path)
    {
        try
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0 && ContainedPath(root, Path.GetFileName(path)) == path) File.Delete(path);
        }
        catch { }
    }

    private static void Cleanup(string root, List<string> createdFiles)
    {
        foreach (var path in createdFiles) TryDeleteOwnedFile(root, path);
        try { if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0) Directory.Delete(root, false); }
        catch { }
    }

    private sealed class CancellationWriteStream : Stream
    {
        private readonly Stream _inner;
        private readonly CancellationToken _cancellation;
        public CancellationWriteStream(Stream inner, CancellationToken cancellation) { _inner = inner; _cancellation = cancellation; }
        public override bool CanRead => false;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set { _cancellation.ThrowIfCancellationRequested(); _inner.Position = value; } }
        public override void Flush() { _cancellation.ThrowIfCancellationRequested(); _inner.Flush(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) { _cancellation.ThrowIfCancellationRequested(); return _inner.Seek(offset, origin); }
        public override void SetLength(long value) { _cancellation.ThrowIfCancellationRequested(); _inner.SetLength(value); }
        public override void Write(byte[] buffer, int offset, int count) { _cancellation.ThrowIfCancellationRequested(); _inner.Write(buffer, offset, count); }
    }
}
