using System.IO;
using System.Security.Cryptography;

namespace Liuzhuan.Services;

public enum DuplicateIssueKind { Missing, ReadFailed, SourceChanged }
public sealed record DuplicateIssue(string Path, string Name, DuplicateIssueKind Kind, string Message);
public sealed record DuplicateFile(string Path, long Size, DateTime LastWriteUtc, string Sha256, IReadOnlyList<IntegritySource> Sources)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Detail => $"{Utils.FileClassifier.FormatSize(Size)} · {Sources.Count} 条素材索引";
}
public sealed record DuplicateGroup(string Sha256, long FileBytes, IReadOnlyList<DuplicateFile> Files)
{
    public long LogicalDuplicateBytes => DuplicateMaterialService.AddBytes(0, FileBytes, Files.Count - 1);
    public string Title => $"{Files.Count} 个不同路径 · 每份 {Utils.FileClassifier.FormatSize(FileBytes)}";
    public string Detail => $"逻辑重复 {Utils.FileClassifier.FormatSize(LogicalDuplicateBytes)} · SHA-256 {Sha256[..12]}…";
}
public sealed record DuplicateProgress(string Stage, int Completed, int Total, string CurrentName, long ReadBytes, long FileBytes);
public sealed record DuplicateReport(int SourceCount, int UniquePaths, int CandidatePaths, int HashedPaths, long ReadBytes,
    IReadOnlyList<DuplicateFile> CheckedFiles, IReadOnlyList<DuplicateGroup> Groups, IReadOnlyList<DuplicateIssue> Issues)
{
    public long LogicalDuplicateBytes => Groups.Aggregate(0L, (sum, group) => DuplicateMaterialService.AddBytes(sum, group.LogicalDuplicateBytes));
}

/// <summary>Explicit, read-only exact-byte comparison. Never trusts saved integrity baselines or deletes an index/file.</summary>
public static class DuplicateMaterialService
{
    public const int MaxSources = 100_000;
    private static readonly SemaphoreSlim ScanSlot = new(1, 1);
    private sealed record Candidate(string Path, long Size, DateTime Modified, IntegritySource[] Sources);
    internal static long AddBytes(long current, long bytes, int copies = 1)
    {
        if (bytes <= 0 || copies <= 0) return current;
        return bytes > (long.MaxValue - current) / copies ? long.MaxValue : current + bytes * copies;
    }
    private static IReadOnlyList<T> ReadOnly<T>(IEnumerable<T> values) => Array.AsReadOnly(values.ToArray());
    private static DuplicateReport Report(int sourceCount, int paths, int candidates, int hashed, long bytes,
        IEnumerable<DuplicateFile> files, IEnumerable<DuplicateIssue> issues)
    {
        var checkedFiles = files.ToArray();
        var groups = checkedFiles.GroupBy(x => (x.Size, x.Sha256)).Where(x => x.Count() > 1)
            .Select(x => new DuplicateGroup(x.Key.Sha256, x.Key.Size, ReadOnly(x.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))))
            .OrderByDescending(x => x.LogicalDuplicateBytes).ThenByDescending(x => x.Files.Count).ThenBy(x => x.Sha256).ToArray();
        return new(sourceCount, paths, candidates, hashed, bytes, ReadOnly(checkedFiles), ReadOnly(groups), ReadOnly(issues));
    }

    public static Task<DuplicateReport> ScanAsync(IReadOnlyList<IntegritySource> snapshot,
        IProgress<DuplicateProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Count > MaxSources) throw new ArgumentException($"每次最多检查 {MaxSources} 条素材，请缩小选择范围。");
        var sources = snapshot.ToArray();
        return Task.Run(async () =>
        {
            await ScanSlot.WaitAsync(cancellationToken);
            try
            {
                var issues = new List<DuplicateIssue>();
                var byPath = new Dictionary<string, List<IntegritySource>>(StringComparer.OrdinalIgnoreCase);
                long lastProgress = 0;
                void Notify(string stage, int completed, int total, string name = "", long read = 0, long length = 0, bool force = false)
                {
                    if (progress == null || !force && Environment.TickCount64 - lastProgress < 100) return;
                    lastProgress = Environment.TickCount64;
                    progress.Report(new(stage, completed, total, name, read, length));
                }
                for (int i = 0; i < sources.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var source = sources[i];
                    if (source == null || string.IsNullOrWhiteSpace(source.Id)) throw new ArgumentException("素材快照无效。");
                    try
                    {
                        if (string.IsNullOrWhiteSpace(source.FilePath)) throw new ArgumentException("未记录文件路径。");
                        var path = Path.GetFullPath(source.FilePath);
                        if (!byPath.TryGetValue(path, out var aliases)) byPath[path] = aliases = new();
                        aliases.Add(source);
                    }
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
                    { issues.Add(new(source.FilePath, source.DisplayName, DuplicateIssueKind.ReadFailed, ex.Message)); }
                }
                var candidates = new List<Candidate>();
                Notify("正在读取大小", 0, byPath.Count, force: true);
                int visited = 0;
                foreach (var (path, aliases) in byPath)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var info = new FileInfo(path);
                        candidates.Add(new(path, info.Length, info.LastWriteTimeUtc, aliases.ToArray()));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
                    { issues.Add(new(path, aliases[0].DisplayName, ex is FileNotFoundException or DirectoryNotFoundException ? DuplicateIssueKind.Missing : DuplicateIssueKind.ReadFailed, ex.Message)); }
                    Notify("正在读取大小", ++visited, byPath.Count, aliases[0].DisplayName);
                }
                // Equal sizes are necessary, never sufficient. Each normalized path is read at most once.
                var comparable = candidates.GroupBy(x => x.Size).Where(x => x.Count() > 1).SelectMany(x => x).ToArray();
                var files = new List<DuplicateFile>();
                var buffer = new byte[81920];
                int completed = 0, hashed = 0;
                long totalRead = 0;
                Notify("正在比较内容", 0, comparable.Length, force: true);
                foreach (var candidate in comparable)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        await using var input = new FileStream(candidate.Path, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, true);
                        if (input.Length != candidate.Size || File.GetLastWriteTimeUtc(candidate.Path) != candidate.Modified)
                            throw new SourceChangedException();
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        long read = 0;
                        int count;
                        while ((count = await input.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            hash.AppendData(buffer, 0, count);
                            read += count; totalRead = AddBytes(totalRead, count);
                            Notify("正在比较内容", completed, comparable.Length, candidate.Sources[0].DisplayName, read, candidate.Size);
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                        if (read != candidate.Size || input.Length != candidate.Size || File.GetLastWriteTimeUtc(candidate.Path) != candidate.Modified)
                            throw new SourceChangedException();
                        files.Add(new(candidate.Path, candidate.Size, candidate.Modified, Convert.ToHexString(hash.GetHashAndReset()), ReadOnly(candidate.Sources)));
                        hashed++;
                    }
                    catch (SourceChangedException)
                    { issues.Add(new(candidate.Path, candidate.Sources[0].DisplayName, DuplicateIssueKind.SourceChanged, "检查期间来源变化，未计入重复结果。")); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
                    { issues.Add(new(candidate.Path, candidate.Sources[0].DisplayName, ex is FileNotFoundException or DirectoryNotFoundException ? DuplicateIssueKind.Missing : DuplicateIssueKind.ReadFailed, ex.Message)); }
                    Notify("正在比较内容", ++completed, comparable.Length, candidate.Sources[0].DisplayName);
                }
                // A previously read file may change while later files are still being compared.
                var verified = new List<DuplicateFile>();
                Notify("正在复核来源", 0, files.Count, force: true);
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var info = new FileInfo(file.Path);
                        if (info.Length != file.Size || info.LastWriteTimeUtc != file.LastWriteUtc) throw new SourceChangedException();
                        verified.Add(file);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    { issues.Add(new(file.Path, file.Name, DuplicateIssueKind.SourceChanged, "检查结束前来源变化或不可读取，未计入重复结果。")); }
                    Notify("正在复核来源", verified.Count, files.Count, file.Name);
                }
                cancellationToken.ThrowIfCancellationRequested();
                Notify("检查完成", comparable.Length, comparable.Length, force: true);
                return Report(sources.Length, byPath.Count, comparable.Length, hashed, totalRead, verified, issues);
            }
            finally { ScanSlot.Release(); }
        }, cancellationToken);
    }

    /// <summary>Apply on the owner thread: removed/relinked A→B→A sources cannot publish old scan results.</summary>
    public static DuplicateReport Reconcile(DuplicateReport report, IReadOnlyList<IntegritySource> current)
    {
        var live = current.ToLookup(x => x.Id, StringComparer.Ordinal);
        var files = new List<DuplicateFile>();
        var issues = report.Issues.ToList();
        foreach (var file in report.CheckedFiles)
        {
            var matching = file.Sources.Where(source => live[source.Id].Any(row => row.FilePath == source.FilePath && row.Type == source.Type && row.SourceRevision == source.SourceRevision)).ToArray();
            if (matching.Length != file.Sources.Count)
                issues.Add(new(file.Path, file.Name, DuplicateIssueKind.SourceChanged, "素材已移除或重新定位，过期索引未计入结果。"));
            if (matching.Length > 0) files.Add(file with { Sources = ReadOnly(matching) });
        }
        return Report(report.SourceCount, report.UniquePaths, report.CandidatePaths, report.HashedPaths, report.ReadBytes, files, issues);
    }
    private sealed class SourceChangedException : IOException { }
}
