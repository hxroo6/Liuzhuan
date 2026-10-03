using System.IO;
using Liuzhuan.Models;
using Liuzhuan.Utils;

namespace Liuzhuan.Services;

public sealed record SourceRelinkSource(string Id, string DisplayName, MaterialType Type, string FilePath, long Size);
public sealed record SourceRelinkStatus(string Id, string OriginalPath, bool Exists);
public sealed record SourceRelinkPlan(SourceRelinkSource Source, string CandidatePath, long CandidateSize, bool RequireOriginalSize = false);
public enum SourceRelinkMatchStatus { Missing, Unique, Ambiguous, InvalidSource }
public sealed record SourceRelinkMatch(SourceRelinkSource Source, IReadOnlyList<SourceRelinkPlan> Candidates, SourceRelinkMatchStatus Status, string Message);
public sealed record SourceRelinkIssue(string Path, string Message);
public sealed record SourceRelinkProgress(int ScannedFiles, int ScannedDirectories, string CurrentDirectory);
public sealed record SourceRelinkSearchResult(IReadOnlyList<SourceRelinkMatch> Matches, IReadOnlyList<SourceRelinkIssue> Issues, int ScannedFiles, int ScannedDirectories);

/// <summary>只读来源检查和候选查找；只有 DataStore 接收用户确认后才更新索引。</summary>
public static class SourceRelinkService
{
    private const int MaxScannedFiles = 200000;
    private const int MaxScannedDirectories = 20000;
    private const int MaxScannedEntries = 250000;

    // 在界面线程捕获；后台扫描仅使用不可变快照。
    public static IReadOnlyList<SourceRelinkSource> Snapshot(IEnumerable<MaterialItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return Array.AsReadOnly(items.Select(x => new SourceRelinkSource(x.Id, x.DisplayName, x.Type, x.FilePath, x.Size)).ToArray());
    }

    public static SourceRelinkPlan PrepareSingle(MaterialItem item, string candidatePath)
    {
        ArgumentNullException.ThrowIfNull(item);
        return PrepareSingle(new SourceRelinkSource(item.Id, item.DisplayName, item.Type, item.FilePath, item.Size), candidatePath);
    }

    public static SourceRelinkPlan PrepareSingle(SourceRelinkSource source, string candidatePath) => ReadCandidate(source, candidatePath, false);

    public static Task<IReadOnlyList<SourceRelinkStatus>> CheckSourcesAsync(IReadOnlyList<SourceRelinkSource> snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var sources = snapshot.ToArray();
        return Task.Run<IReadOnlyList<SourceRelinkStatus>>(() =>
        {
            var results = new List<SourceRelinkStatus>();
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (source == null || string.IsNullOrWhiteSpace(source.Id)) throw new ArgumentException("素材快照无效。", nameof(snapshot));
                results.Add(new(source.Id, source.FilePath, File.Exists(source.FilePath)));
            }
            cancellationToken.ThrowIfCancellationRequested();
            return Array.AsReadOnly(results.ToArray());
        }, cancellationToken);
    }

    /// <summary>确认时重验文件和大小，避免使用扫描后已消失或被修改的候选。</summary>
    public static SourceRelinkPlan ValidatePlan(SourceRelinkPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var current = ReadCandidate(plan.Source, plan.CandidatePath, plan.RequireOriginalSize);
        if (current.CandidateSize != plan.CandidateSize)
            throw new InvalidOperationException("候选文件大小已变化，请重新选择并确认。");
        return current;
    }

    public static Task<SourceRelinkSearchResult> FindCandidatesAsync(string folder,
        IReadOnlyList<SourceRelinkSource> snapshot, IProgress<SourceRelinkProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var sources = snapshot.ToArray();
        return Task.Run(() => FindCandidates(folder, sources, progress, cancellationToken), cancellationToken);
    }

    private static SourceRelinkPlan ReadCandidate(SourceRelinkSource source, string candidatePath, bool requireOriginalSize)
    {
        if (source == null || string.IsNullOrWhiteSpace(source.Id) || !Enum.IsDefined(typeof(MaterialType), source.Type) ||
            (source.Type == MaterialType.Text && string.IsNullOrWhiteSpace(source.FilePath)))
            throw new ArgumentException("纯文字或无效素材没有可重新定位的原文件。");
        if (string.IsNullOrWhiteSpace(candidatePath) || !Path.IsPathFullyQualified(candidatePath))
            throw new ArgumentException("请选择原文件的完整路径。", nameof(candidatePath));
        var path = Path.GetFullPath(candidatePath);
        if (!File.Exists(path)) throw new FileNotFoundException("候选文件不存在或无法访问，请重新选择。", path);
        if (FileClassifier.ClassifyByExtension(path) != source.Type)
            throw new InvalidOperationException("候选文件的素材类型不匹配，请选择相同类型的文件。");
        // 仅打开只读句柄检查可访问性，不读取、复制或更改原内容。
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var size = stream.Length;
        if (requireOriginalSize && !string.Equals(Path.GetFileName(source.FilePath), Path.GetFileName(path), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("文件名已变化，请重新查找或逐条选择文件。");
        if (requireOriginalSize && source.Size > 0 && size != source.Size)
            throw new InvalidOperationException("候选文件大小与原记录不一致，请逐条选择并确认。");
        return new SourceRelinkPlan(source, path, size, requireOriginalSize);
    }

    private static SourceRelinkSearchResult FindCandidates(string folder, SourceRelinkSource[] sources,
        IProgress<SourceRelinkProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
            throw new ArgumentException("请选择具体的素材文件夹。", nameof(folder));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (PathsEqual(root, Path.GetPathRoot(root) ?? root))
            throw new ArgumentException("请选具体文件夹，不扫描整个盘符或共享根目录。", nameof(folder));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("所选文件夹不存在或无法访问。");
        for (var directory = new DirectoryInfo(root); directory != null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("请选择实际文件夹，不扫描符号链接或目录联接。", nameof(folder));

        var issues = new List<SourceRelinkIssue>();
        var candidates = new Dictionary<string, List<SourceRelinkPlan>>(StringComparer.Ordinal);
        var wanted = new Dictionary<string, List<SourceRelinkSource>>(StringComparer.OrdinalIgnoreCase);
        var invalid = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            if (source == null || string.IsNullOrWhiteSpace(source.Id)) throw new ArgumentException("素材快照无效。");
            if (candidates.ContainsKey(source.Id)) throw new ArgumentException("素材快照包含重复 ID。");
            candidates.Add(source.Id, new List<SourceRelinkPlan>());
            string name;
            try { name = Path.GetFileName(source.FilePath); }
            catch (ArgumentException) { name = string.Empty; }
            if (string.IsNullOrWhiteSpace(name) || !Enum.IsDefined(typeof(MaterialType), source.Type))
            {
                invalid[source.Id] = "没有可查找的原文件名，请逐条选择文件。";
                continue;
            }
            if (!wanted.TryGetValue(name, out var matching)) wanted[name] = matching = new();
            matching.Add(source);
        }

        var directories = new Stack<string>();
        directories.Push(root);
        var files = 0;
        var scannedDirectories = 0;
        var scannedEntries = 0;
        var limitReached = false;
        var lastProgress = 0L;
        while (directories.Count > 0 && !limitReached)
        {
            ct.ThrowIfCancellationRequested();
            var directory = directories.Pop();
            if (++scannedDirectories > MaxScannedDirectories) { limitReached = true; break; }
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                {
                    issues.Add(new(directory, "已跳过符号链接或目录联接。"));
                    continue;
                }
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    ct.ThrowIfCancellationRequested();
                    if (++scannedEntries > MaxScannedEntries) { limitReached = true; break; }
                    try
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            issues.Add(new(entry, "已跳过符号链接或目录联接。"));
                            continue;
                        }
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            directories.Push(entry);
                            continue;
                        }
                        if (++files > MaxScannedFiles) { limitReached = true; break; }
                        var now = Environment.TickCount64;
                        if (now - lastProgress >= 100)
                        {
                            lastProgress = now;
                            progress?.Report(new(files, scannedDirectories, directory));
                        }
                        if (!wanted.TryGetValue(Path.GetFileName(entry), out var matching)) continue;
                        var info = new FileInfo(entry);
                        var size = info.Length;
                        foreach (var source in matching)
                        {
                            if (FileClassifier.ClassifyByExtension(entry) != source.Type || (source.Size > 0 && source.Size != size)) continue;
                            candidates[source.Id].Add(new(source, Path.GetFullPath(entry), size, true));
                        }
                    }
                    catch (Exception ex) when (IsFileSystemError(ex)) { issues.Add(new(entry, "无法检查文件：" + ex.Message)); }
                }
            }
            catch (Exception ex) when (IsFileSystemError(ex)) { issues.Add(new(directory, "无法检查文件夹：" + ex.Message)); }
        }
        ct.ThrowIfCancellationRequested();
        if (limitReached) issues.Add(new(root, "文件夹过大，已停止查找；请选择更小范围重新查找，当前结果并不完整。"));
        progress?.Report(new(files, scannedDirectories, root));

        var shared = candidates.Values.SelectMany(x => x).GroupBy(x => x.CandidatePath, StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Select(plan => plan.Source.Id).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matches = sources.Select(source =>
        {
            var found = candidates[source.Id].OrderBy(x => x.CandidatePath, StringComparer.OrdinalIgnoreCase).ToArray();
            var status = invalid.ContainsKey(source.Id) ? SourceRelinkMatchStatus.InvalidSource
                : found.Length == 0 ? SourceRelinkMatchStatus.Missing
                : found.Length > 1 || found.Any(x => shared.Contains(x.CandidatePath)) || limitReached ? SourceRelinkMatchStatus.Ambiguous
                : SourceRelinkMatchStatus.Unique;
            var message = status switch
            {
                SourceRelinkMatchStatus.InvalidSource => invalid[source.Id],
                SourceRelinkMatchStatus.Missing => source.Size > 0 ? "未找到同名且大小一致的文件。" : "未找到同名文件。",
                SourceRelinkMatchStatus.Ambiguous => found.Length > 1 ? "找到多个候选，请逐条选择并确认。"
                    : limitReached ? "查找未完成，请缩小范围或逐条确认。" : "同一候选匹配多条素材，请逐条确认。",
                _ => "找到一个同名候选；文件名和大小不能证明内容相同，确认后才修复。"
            };
            return new SourceRelinkMatch(source, Array.AsReadOnly(found), status, message);
        }).ToArray();
        return new(Array.AsReadOnly(matches), Array.AsReadOnly(issues.ToArray()), files, scannedDirectories);
    }

    private static bool IsFileSystemError(Exception ex) => ex is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    public static bool PathsEqual(string left, string right)
    {
        try { return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return string.Equals(left, right, StringComparison.OrdinalIgnoreCase); }
    }
}
