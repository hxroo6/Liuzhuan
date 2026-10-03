using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Liuzhuan.Models;

namespace Liuzhuan.Services;

public sealed record IntegritySource(string Id, string DisplayName, string FilePath, MaterialType Type,
    long SourceRevision, long IntegrityRevision, MaterialIntegrityBaseline? Baseline);
public sealed record IntegrityResult(IntegritySource Source, MaterialIntegrityStatus Status, string? Sha256,
    long Size, DateTime LastWriteUtc, DateTimeOffset CheckedUtc, string Message)
{
    public bool CanRecord => Sha256 != null && Status is MaterialIntegrityStatus.Normal or MaterialIntegrityStatus.Changed or MaterialIntegrityStatus.Unrecorded;
}
public sealed record IntegrityProgress(int CompletedItems, int TotalItems, string CurrentName, long ReadBytes, long FileBytes);

/// <summary>显式、只读、可取消的 SHA-256 检查；只计算调用方选择的文件，不自动建立基线。</summary>
public static class IntegrityService
{
    public static IReadOnlyList<IntegritySource> Snapshot(IEnumerable<MaterialItem> items) => Array.AsReadOnly(items.Where(x => x.HasFileSource)
        .Select(x => new IntegritySource(x.Id, x.DisplayName, x.FilePath, x.Type, x.SourceRevision, x.IntegrityRevision, x.IntegrityBaseline)).ToArray());

    public static MaterialIntegrityBaseline? NormalizeBaseline(MaterialIntegrityBaseline? baseline)
    {
        if (baseline == null) return null;
        if (baseline.Sha256 == null || baseline.Sha256.Length != 64 || !Regex.IsMatch(baseline.Sha256, "^[A-Fa-f0-9]{64}$") || baseline.Size < 0 ||
            string.IsNullOrWhiteSpace(baseline.SourcePath) || !Enum.IsDefined(typeof(MaterialType), baseline.SourceType) ||
            baseline.RecordedUtc == DateTimeOffset.MinValue)
            throw new ArgumentException("素材完整性基线无效。");
        return baseline with { Sha256 = baseline.Sha256.ToUpperInvariant(), RecordedUtc = baseline.RecordedUtc.ToUniversalTime() };
    }
    public static bool MatchesSource(MaterialItem item, IntegritySource source) => item.Id == source.Id && item.FilePath == source.FilePath &&
        item.Type == source.Type && item.SourceRevision == source.SourceRevision && item.IntegrityRevision == source.IntegrityRevision;

    public static Task<IReadOnlyList<IntegrityResult>> CheckAsync(IReadOnlyList<IntegritySource> snapshot,
        IProgress<IntegrityProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var sources = snapshot.ToArray();
        return Task.Run<IReadOnlyList<IntegrityResult>>(async () =>
        {
            var results = new List<IntegrityResult>();
            var buffer = new byte[81920];
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (source == null || string.IsNullOrWhiteSpace(source.Id)) throw new ArgumentException("素材快照无效。");
                progress?.Report(new(results.Count, sources.Length, source.DisplayName, 0, 0));
                IntegrityResult result;
                try
                {
                    if (string.IsNullOrWhiteSpace(source.FilePath)) throw new FileNotFoundException("未记录原文件路径。");
                    var path = Path.GetFullPath(source.FilePath);
                    await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, true);
                    var length = input.Length;
                    var lastWrite = File.GetLastWriteTimeUtc(path);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    long read = 0, lastProgress = 0;
                    int count;
                    while ((count = await input.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
                    {
                        hash.AppendData(buffer, 0, count); read += count;
                        if (Environment.TickCount64 - lastProgress >= 100)
                        {
                            lastProgress = Environment.TickCount64;
                            progress?.Report(new(results.Count, sources.Length, source.DisplayName, read, length));
                        }
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (read != length) throw new IOException("读取期间文件大小变化，请重新检查。");
                    var digest = Convert.ToHexString(hash.GetHashAndReset());
                    MaterialIntegrityBaseline? baseline = null;
                    try { baseline = NormalizeBaseline(source.Baseline); } catch (ArgumentException) { }
                    var bound = baseline != null && baseline.SourceType == source.Type && string.Equals(baseline.SourcePath, source.FilePath, StringComparison.OrdinalIgnoreCase);
                    var status = !bound ? MaterialIntegrityStatus.Unrecorded : baseline!.Sha256 == digest && baseline.Size == length
                        ? MaterialIntegrityStatus.Normal : MaterialIntegrityStatus.Changed;
                    var message = status switch
                    {
                        MaterialIntegrityStatus.Normal => "本次读取内容与已记录的 SHA-256 基线一致。",
                        MaterialIntegrityStatus.Changed => "与基线不同；合法编辑后可明确确认并重建基线，不会自动接受变更。",
                        _ => source.Baseline != null ? "当前来源没有可用基线，旧基线失配或格式无效；确认后可记录本次读取结果。" : "尚未记录基线；确认后可记录本次读取结果。"
                    };
                    result = new(source, status, digest, length, lastWrite, DateTimeOffset.UtcNow, message);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
                {
                    var status = ex is FileNotFoundException or DirectoryNotFoundException ? MaterialIntegrityStatus.Missing : MaterialIntegrityStatus.ReadFailed;
                    result = new(source, status, null, 0, default, DateTimeOffset.UtcNow, ex.Message);
                }
                results.Add(result);
                progress?.Report(new(results.Count, sources.Length, source.DisplayName, 0, 0));
            }
            cancellationToken.ThrowIfCancellationRequested();
            return Array.AsReadOnly(results.ToArray());
        }, cancellationToken);
    }
}
