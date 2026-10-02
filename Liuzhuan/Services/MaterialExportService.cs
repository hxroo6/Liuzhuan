using System.Diagnostics;
using System.IO;
using System.Text;
using Liuzhuan.Models;

namespace Liuzhuan.Services;

/// <summary>界面线程捕获的内容快照；不保留可变的素材对象或实时集合。</summary>
public sealed record MaterialExportSource(string Id, string DisplayName, MaterialType Type, string FilePath, string TextContent);

public sealed record MaterialExportProgress(int CompletedItems, int TotalItems, long CopiedBytes, long TotalBytes, string CurrentName);
public sealed record MaterialExportResult(string DirectoryPath, int ItemCount, long CopiedBytes);

/// <summary>选中素材另存为普通文件。只读来源，不携带配置或内部素材索引。</summary>
public static class MaterialExportService
{
    private const int BufferSize = 81920;
    private const int MaxFileNameLength = 160;
    private static readonly UTF8Encoding TextEncoding = new(false);
    private sealed record Entry(MaterialExportSource Source, string Name, string? SourcePath, long Length);

    // 须在读取素材集合的界面线程调用，随后可安全交给后台任务。
    public static IReadOnlyList<MaterialExportSource> Snapshot(IEnumerable<MaterialItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return Array.AsReadOnly(items.Select(item => new MaterialExportSource(
            item.Id, item.DisplayName, item.Type, item.FilePath, item.TextContent)).ToArray());
    }

    /// <summary>
    /// destinationParent 必须是已存在的文件夹。重复 Id 仅导出第一项；不同 Id 的重复文件引用分别导出。
    /// 有 FilePath 的文字素材复制原文件，确保不使用可能截断的预览文字。
    /// </summary>
    public static Task<MaterialExportResult> ExportAsync(string destinationParent,
        IReadOnlyList<MaterialExportSource> snapshot, IProgress<MaterialExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        // 后台不再访问调用方集合，耗时文件检查及复制也不占用界面线程。
        var sources = snapshot.ToArray();
        return Task.Run(() => ExportCoreAsync(destinationParent, sources, progress, cancellationToken), cancellationToken);
    }

    private static async Task<MaterialExportResult> ExportCoreAsync(string destinationParent,
        MaterialExportSource[] sources, IProgress<MaterialExportProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var parent = Path.GetFullPath(destinationParent);
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("请选择已存在的导出文件夹。");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<Entry>();
        long totalBytes = 0;
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            if (source == null || string.IsNullOrWhiteSpace(source.Id) || !Enum.IsDefined(typeof(MaterialType), source.Type))
                throw new ArgumentException("素材快照无效，请重新选择素材。", nameof(sources));
            if (!ids.Add(source.Id)) continue;
            var sourcePath = string.IsNullOrWhiteSpace(source.FilePath) ? null : Path.GetFullPath(source.FilePath);
            long length;
            string name;
            if (sourcePath != null)
            {
                if (!File.Exists(sourcePath)) throw new FileNotFoundException("素材原文件不存在：" + source.DisplayName, sourcePath);
                length = new FileInfo(sourcePath).Length;
                name = SanitizeFileName(Path.GetFileName(sourcePath));
            }
            else
            {
                if (source.Type != MaterialType.Text)
                    throw new FileNotFoundException("素材没有可导出的原文件：" + source.DisplayName);
                length = TextEncoding.GetByteCount(source.TextContent ?? "");
                name = SanitizeFileName(source.DisplayName ?? "文字素材");
                if (!name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) name += ".txt";
            }
            name = UniqueName(name, names);
            totalBytes = checked(totalBytes + length);
            entries.Add(new Entry(source, name, sourcePath, length));
        }
        if (entries.Count == 0) throw new ArgumentException("请先选择要导出的素材。", nameof(sources));

        // 全新随机子目录，绝不覆盖目标文件夹中已有文件。清理仅移除记录在案的输出，且不递归删除。
        var root = ContainedPath(parent, "流转素材-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException("导出目录已存在，请重试。");
        Directory.CreateDirectory(root);
        var createdFiles = new List<string>();
        long copiedBytes = 0;
        int completedItems = 0;
        try
        {
            var buffer = new byte[BufferSize];
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new(completedItems, entries.Count, copiedBytes, totalBytes, entry.Name));
                ct.ThrowIfCancellationRequested();
                var outputPath = ContainedPath(root, entry.Name);
                bool firstByteReport = true;
                long nextByteReport = 0;
                void ReportBytes()
                {
                    if (progress == null) return;
                    var now = Stopwatch.GetTimestamp();
                    if (!firstByteReport && now < nextByteReport) return;
                    firstByteReport = false;
                    // 首次写入立即反馈，其余连续字节更新最多约每 125 ms 一次，避免堆积界面消息。
                    nextByteReport = now + Stopwatch.Frequency / 8;
                    progress.Report(new(completedItems, entries.Count, copiedBytes, totalBytes, entry.Name));
                }
                if (entry.SourcePath != null)
                {
                    // FileShare.Read 禁止在复制期间写入/替换来源，避免悄悄导出变化中的文件。
                    await using var input = new FileStream(entry.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                        BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (input.Length != entry.Length) throw new IOException("素材在导出前发生变化，请重试：" + entry.Source.DisplayName);
                    await using var output = CreateOutput(outputPath, createdFiles);
                    long itemBytes = 0;
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        var read = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
                        if (read == 0) break;
                        if (read > entry.Length - itemBytes) throw new IOException("素材在导出中发生变化：" + entry.Source.DisplayName);
                        await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        itemBytes += read;
                        copiedBytes += read;
                        ReportBytes();
                    }
                    if (itemBytes != entry.Length) throw new IOException("素材未完整复制：" + entry.Source.DisplayName);
                }
                else
                {
                    await using var output = CreateOutput(outputPath, createdFiles);
                    var text = entry.Source.TextContent ?? "";
                    var encoder = TextEncoding.GetEncoder();
                    int offset = 0;
                    while (offset < text.Length)
                    {
                        ct.ThrowIfCancellationRequested();
                        var encoded = EncodeChunk(text, offset, encoder, buffer);
                        await output.WriteAsync(buffer.AsMemory(0, encoded.Bytes), ct).ConfigureAwait(false);
                        offset += encoded.Characters;
                        copiedBytes += encoded.Bytes;
                        ReportBytes();
                    }
                    if (output.Length != entry.Length) throw new IOException("文字未完整导出：" + entry.Source.DisplayName);
                }
                completedItems++;
                progress?.Report(new(completedItems, entries.Count, copiedBytes, totalBytes, entry.Name));
            }
            ct.ThrowIfCancellationRequested();
            return new MaterialExportResult(root, completedItems, copiedBytes);
        }
        catch
        {
            // 最佳努力清理；保留原异常。若目录出现外部文件或被锁定，宁可留下残余也不扩展删除范围。
            try
            {
                // 若外部把临时目录替换成链接，停止清理，避免通过链接删除其他位置。
                if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0)
                {
                    foreach (var path in createdFiles)
                    {
                        try { if (ContainedPath(root, Path.GetFileName(path)) == path) File.Delete(path); }
                        catch { }
                    }
                    Directory.Delete(root, false);
                }
            }
            catch { }
            throw;
        }
    }

    private static FileStream CreateOutput(string path, List<string> createdFiles)
    {
        var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        createdFiles.Add(path);
        return output;
    }

    private static (int Characters, int Bytes) EncodeChunk(string text, int offset, Encoder encoder, byte[] buffer)
    {
        var count = Math.Min(4096, text.Length - offset);
        // Encoder 保留分块边界上的高代理项，避免 Emoji 被拆成替代字符。
        encoder.Convert(text.AsSpan(offset, count), buffer, offset + count == text.Length,
            out var characters, out var bytes, out _);
        return (characters, bytes);
    }

    private static string SanitizeFileName(string name)
    {
        var clean = new string(name.Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.', ' ');
        if (clean.Length == 0 || clean is "." or "..") clean = "素材";
        var stem = clean.Split('.')[0].TrimEnd('.', ' ');
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase)
            || ((stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && stem.Length == 4 && "0123456789¹²³".Contains(stem[3]))) clean = "_" + clean;
        return clean;
    }

    private static string UniqueName(string name, HashSet<string> names)
    {
        var extension = Path.GetExtension(name);
        if (extension.Length > 32) extension = "";
        var stem = extension.Length > 0 ? name[..^extension.Length] : name;
        for (int suffix = 1; ; suffix++)
        {
            var ending = suffix == 1 ? "" : " (" + suffix + ")";
            var maxStem = MaxFileNameLength - extension.Length - ending.Length;
            var length = Math.Min(stem.Length, maxStem);
            if (length > 0 && char.IsHighSurrogate(stem[length - 1])) length--;
            var candidate = stem[..length].TrimEnd('.', ' ') + ending + extension;
            if (names.Add(candidate)) return candidate;
        }
    }

    private static string ContainedPath(string parent, string name)
    {
        var prefix = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(prefix, name));
        if (!result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetDirectoryName(result)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                prefix.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new IOException("导出文件名无效。");
        return result;
    }
}
