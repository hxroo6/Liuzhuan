using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Liuzhuan.Models;

namespace Liuzhuan.Services;

public sealed record LibraryLocation(string Root, string Label, int Items, long IndexBytes, bool Readable, string Message)
{
    public string Detail => Readable ? $"{Items} 项 · 索引 {Utils.FileClassifier.FormatSize(IndexBytes)}" : Message;
}
public sealed record LibraryIndexVersion(string Path, DateTime CreatedUtc, long Bytes)
{
    public string Label => $"{CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {Utils.FileClassifier.FormatSize(Bytes)}";
}
public sealed record LibraryIndexDiff(int Reappearing, int RemovedFromIndex, int Changed, int Unchanged, string[] Details, bool CurrentUnreadable = false);

/// <summary>Only data.json is versioned. Originals, receipts, identities and settings are never rolled back.</summary>
public static class LibraryRecoveryService
{
    public const int MaxVersions = 10;
    public const long MaxIndexBytes = 64L * 1024 * 1024;
    public const long MaxHistoryBytes = 128L * 1024 * 1024;
    private const string HistoryName = "index-history-v1";
    private const string PendingName = "library-action-pending.json";
    private const string RootsName = "library-roots-v1.json";
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions IndexJson = new() { WriteIndented=true, Converters={new JsonStringEnumConverter()} };
    private static readonly Dictionary<string, long> Attempts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> Warnings = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex VersionName = new("^[0-9]{17}-[A-F0-9]{64}\\.json$", RegexOptions.CultureInvariant);
    private sealed record Plan(int Version, string Id, string Kind, string SourceRoot, string TargetRoot, string SnapshotName, string ExpectedHash, string DesiredHash);
    private sealed record KnownRoots(int Version, string[] Roots);

    public static string? HistoryWarning(string root) { lock (Gate) return Warnings.GetValueOrDefault(Full(root)); }

    // Called by the existing serialized persistence worker. A history problem must not reject a primary durable save.
    public static void TryPreservePreviousIndex(string root)
    {
        lock (Gate) {
            root = Full(root);
            if (Attempts.TryGetValue(root, out var last) && Environment.TickCount64 - last < 300_000) return;
            if (Attempts.Count >= 64) { var oldest = Attempts.Keys.First(); Attempts.Remove(oldest); Warnings.Remove(oldest); }
            Attempts[root] = Environment.TickCount64;
            try { PreservePreviousIndex(root); Warnings.Remove(root); }
            catch (Exception) { Warnings[root] = "索引历史暂不可用；当前素材仍按原方式保存。请检查磁盘空间或目录权限。"; }
        }
    }

    private static void PreservePreviousIndex(string root)
    {
        root = SafeRoot(root);
        var index = Path.Combine(root, "data.json");
        if (!File.Exists(index)) return;
        var bytes = ReadIndexBytes(index); _ = ReadRecords(bytes);
        var hash = Hash(bytes); var directory = Path.Combine(root, HistoryName);
        EnsureNoLinks(directory); Directory.CreateDirectory(directory);
        var versions = History(root);
        if (!versions.Any(x => Path.GetFileName(x.Path).EndsWith("-" + hash + ".json", StringComparison.Ordinal))) {
            var destination = Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + hash + ".json");
            DurableJson.WriteText(destination, System.Text.Encoding.UTF8.GetString(bytes));
            versions = History(root);
        }
        var pinned = PinnedVersion(root);
        long total = 0; int kept = 0;
        foreach (var version in versions.OrderByDescending(x => Path.GetFileName(x.Path) == pinned)) {
            if (kept < MaxVersions && version.Bytes <= MaxHistoryBytes - total) { total += version.Bytes; kept++; continue; }
            // Delete only our own correctly named, content-bound, valid index snapshots. Unknown files are retained.
            var oldBytes = ReadIndexBytes(version.Path); _ = ReadRecords(oldBytes);
            if (!Path.GetFileName(version.Path).EndsWith("-" + Hash(oldBytes) + ".json", StringComparison.Ordinal)) continue;
            EnsureNoLinks(version.Path); File.Delete(version.Path);
        }
    }

    private static string? PinnedVersion(string root)
    {
        if (string.IsNullOrWhiteSpace(App.DataDir)) return null;
        var path = Path.Combine(App.DataDir, PendingName); EnsureNoLinks(path); if (!File.Exists(path)) return null;
        var plan = ReadSmall<Plan>(path);
        return plan?.Version == 1 && plan.Kind == "restore" && Same(plan.TargetRoot, root) && VersionName.IsMatch(plan.SnapshotName) ? plan.SnapshotName : null;
    }

    public static Task<IReadOnlyList<LibraryIndexVersion>> HistoryAsync(string root, CancellationToken ct = default) => Task.Run<IReadOnlyList<LibraryIndexVersion>>(() => { ct.ThrowIfCancellationRequested(); lock (Gate) return History(root); }, ct);
    private static LibraryIndexVersion[] History(string root)
    {
        root = SafeRoot(root); var directory = Path.Combine(root, HistoryName); EnsureNoLinks(directory);
        if (!Directory.Exists(directory)) return Array.Empty<LibraryIndexVersion>();
        var files = Directory.EnumerateFiles(directory).Where(x => VersionName.IsMatch(Path.GetFileName(x))).Take(101).ToArray();
        if (files.Length > 100) throw new IOException("索引历史超过检查预算，请保留原文件并检查。");
        return files.Select(path => { EnsureNoLinks(path); var f = new FileInfo(path); return new LibraryIndexVersion(path, f.LastWriteTimeUtc, f.Length); })
            .OrderByDescending(x => Path.GetFileName(x.Path), StringComparer.Ordinal).ToArray();
    }

    public static Task<IReadOnlyList<LibraryLocation>> ListLibrariesAsync(string appData, string activeRoot, CancellationToken ct = default) => Task.Run<IReadOnlyList<LibraryLocation>>(() =>
    {
        appData = SafeRoot(appData); var roots = new List<string> { Full(activeRoot), appData };
        var saved = Path.Combine(appData, RootsName); EnsureNoLinks(saved);
        if (File.Exists(saved)) {
            try { var known = ReadSmall<KnownRoots>(saved); if (known?.Version == 1 && known.Roots?.Length <= 16) roots.AddRange(known.Roots); }
            catch { /* Discovery is read-only. A malformed list is retained and never resets the active library. */ }
        }
        var imports = Path.Combine(appData, "imports"); EnsureNoLinks(imports);
        if (Directory.Exists(imports)) roots.AddRange(Directory.EnumerateDirectories(imports).Where(x => Guid.TryParseExact(Path.GetFileName(x), "N", out _)).Take(64));
        var result = new List<LibraryLocation>();
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase).Take(82)) {
            ct.ThrowIfCancellationRequested(); var label = Same(root, activeRoot) ? "当前素材库" : Same(root, appData) ? "默认素材库" : "保留的素材库";
            try { var safe = SafeRoot(root); var path = Path.Combine(safe, "data.json"); var bytes = ReadIndexBytes(path, allowMissing:true); var records = ReadRecords(bytes);
                result.Add(new(safe, label, records.Count, bytes.LongLength, true, "")); }
            catch { result.Add(new(root, label, 0, 0, false, "索引不可读取，原文件保留")); }
        }
        return result;
    }, ct);

    public static Task<LibraryIndexDiff> DiffAsync(string root, string versionPath, CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested(); root = SafeRoot(root); var history = ReadOwnedVersion(root, versionPath);
        Dictionary<string,MaterialRecord> before; bool unreadable = false;
        try { before = ReadRecords(ReadIndexBytes(Path.Combine(root, "data.json"), allowMissing:true)).ToDictionary(x => x.Id, StringComparer.Ordinal); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException) { before = new(StringComparer.Ordinal); unreadable = true; }
        var after = ReadRecords(history).ToDictionary(x => x.Id, StringComparer.Ordinal);
        var reappearing = 0; var removed = 0; var changed = 0; var unchanged = 0; var details = new List<string>();
        void Detail(string value) { if (details.Count < 100) details.Add(value); }
        foreach (var row in after.Values) {
            ct.ThrowIfCancellationRequested();
            if (!before.TryGetValue(row.Id, out var old)) { reappearing++; Detail("恢复索引 · " + row.DisplayName); }
            else if (JsonSerializer.Serialize(old, IndexJson) != JsonSerializer.Serialize(row, IndexJson)) { changed++; Detail("恢复旧信息 · " + row.DisplayName); }
            else unchanged++;
        }
        foreach (var row in before.Values.Where(x => !after.ContainsKey(x.Id))) { removed++; Detail("移出索引 · " + row.DisplayName); }
        return new LibraryIndexDiff(reappearing, removed, changed, unchanged, details.ToArray(), unreadable);
    }, ct);

    public static Task QueueSwitchAsync(string appData, string sourceRoot, string targetRoot, CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested(); lock (Gate) {
            appData = SafeRoot(appData); sourceRoot = SafeRoot(sourceRoot); targetRoot = SafeRoot(targetRoot);
            if (Same(sourceRoot, targetRoot)) throw new InvalidOperationException("当前已经使用这个素材库。");
            var bytes = ReadIndexBytes(Path.Combine(targetRoot, "data.json"), allowMissing:true); _ = ReadRecords(bytes);
            Queue(appData, new(1, Guid.NewGuid().ToString("N"), "switch", sourceRoot, targetRoot, "", Hash(bytes), ""));
        }
    }, ct);

    public static Task QueueRestoreAsync(string appData, string root, string versionPath, CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested(); lock (Gate) {
            appData = SafeRoot(appData); root = SafeRoot(root); var desired = ReadOwnedVersion(root, versionPath);
            var current = ReadIndexBytes(Path.Combine(root, "data.json"), allowMissing:true);
            Queue(appData, new(1, Guid.NewGuid().ToString("N"), "restore", root, root, Path.GetFileName(versionPath), Hash(current), Hash(desired)));
        }
    }, ct);

    private static void Queue(string appData, Plan plan)
    {
        using var lease = Lease(appData);
        if (File.Exists(Path.Combine(appData, "migration-pending.json")) || File.Exists(Path.Combine(appData, PendingName))) throw new IOException("已有下次启动计划，请先应用或取消。");
        if (!Same(ReadConfiguredRoot(appData), plan.SourceRoot)) throw new IOException("当前素材库指针已变化，请重新打开恢复中心。");
        DurableJson.Write(Path.Combine(appData, PendingName), plan);
    }

    public static string? PendingDescription(string appData)
    {
        try { var path = Path.Combine(SafeRoot(appData), PendingName); EnsureNoLinks(path); if (!File.Exists(path)) return null;
            var plan = ReadSmall<Plan>(path); return plan?.Kind == "restore" ? "已安排下次启动恢复索引；期间继续编辑会使计划失效。" : "已安排下次启动切换素材库；当前会话继续使用原库。"; }
        catch { return "下次启动计划不可读取，原文件保留。"; }
    }
    public static void CancelPending(string appData)
    {
        lock (Gate) { appData = SafeRoot(appData); using var lease = Lease(appData); var path = Path.Combine(appData, PendingName); EnsureNoLinks(path); if (File.Exists(path)) File.Delete(path); }
    }

    /// <summary>Call before constructing DataStore or loading root-dependent static configuration. Failure preserves the plan.</summary>
    public static string? ApplyPending(string appData)
    {
        lock (Gate) {
            var full = Full(appData);
            if (!full.StartsWith("\\\\", StringComparison.Ordinal) && !Directory.Exists(full)) { EnsureNoLinks(full); return null; }
            appData = SafeRoot(appData); using var lease = Lease(appData); var path = Path.Combine(appData, PendingName); EnsureNoLinks(path);
            if (!File.Exists(path)) return null;
            if (File.Exists(Path.Combine(appData, "migration-pending.json"))) throw new IOException("存在两个启动计划，保留原库；请取消其中一个再重试。");
            var plan = ReadSmall<Plan>(path) ?? throw new InvalidDataException("恢复计划为空。");
            if (plan.Version != 1 || !Guid.TryParseExact(plan.Id, "N", out _) || plan.Kind is not ("switch" or "restore")) throw new InvalidDataException("恢复计划无效。");
            var source = SafeRoot(plan.SourceRoot); var target = SafeRoot(plan.TargetRoot); var configured = ReadConfiguredRoot(appData);
            if (plan.Kind == "switch") {
                var bytes = ReadIndexBytes(Path.Combine(target, "data.json"), allowMissing:true); _ = ReadRecords(bytes);
                if (Hash(bytes) != plan.ExpectedHash) throw new IOException("目标素材库在预览后变化，计划已保留，请重新核对。");
                if (!Same(configured, source) && !Same(configured, target)) throw new IOException("当前库与启动计划不符，未切换。");
                RememberRoots(appData, source, target);
                DurableJson.WriteText(Path.Combine(appData, "config.json"), JsonSerializer.Serialize(new { DataDir = target }));
            } else {
                if (!Same(source, target) || !Same(configured, source)) throw new IOException("恢复计划不属于当前素材库。");
                var desired = ReadOwnedVersion(target, Path.Combine(target, HistoryName, plan.SnapshotName));
                if (Hash(desired) != plan.DesiredHash) throw new IOException("索引历史已变化，未恢复。");
                var current = ReadIndexBytes(Path.Combine(target, "data.json"), allowMissing:true);
                if (Hash(current) != plan.DesiredHash) {
                    if (Hash(current) != plan.ExpectedHash) throw new IOException("安排恢复后素材库又有变化，未覆盖当前索引；请重新预览。");
                    BackupBeforeRestore(target, current, plan.Id);
                    TryPreservePreviousIndex(target);
                    DurableJson.WriteText(Path.Combine(target, "data.json"), System.Text.Encoding.UTF8.GetString(desired));
                }
            }
            File.Delete(path);
            return plan.Kind == "switch" ? "已切换到所选素材库，原库完整保留。" : "已恢复索引历史，原文件和配置保持现状。";
        }
    }

    private static void BackupBeforeRestore(string root, byte[] bytes, string actionId)
    {
        var directory = Path.Combine(root, "index-recovery-originals-v1"); EnsureNoLinks(directory); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, actionId + "-" + Hash(bytes) + ".bin"); EnsureNoLinks(path);
        if (!File.Exists(path)) {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
            try { using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(bytes); output.Flush(true); } File.Move(temporary, path); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        } else if (new FileInfo(path).Length > MaxIndexBytes || Hash(File.ReadAllBytes(path)) != Hash(bytes)) throw new IOException("恢复前备份不匹配，未覆盖索引。");
        long total = bytes.Length; int count = 1;
        foreach (var file in Directory.EnumerateFiles(directory).Where(x => x != path && Regex.IsMatch(Path.GetFileName(x), "^[a-f0-9]{32}-[A-F0-9]{64}\\.bin$")).OrderByDescending(File.GetLastWriteTimeUtc).Take(101)) {
            EnsureNoLinks(file); var size = new FileInfo(file).Length;
            if (count < 3 && size <= MaxHistoryBytes - total) { count++; total += size; continue; }
            if (size <= MaxIndexBytes && Path.GetFileName(file).EndsWith("-" + Hash(File.ReadAllBytes(file)) + ".bin", StringComparison.Ordinal)) File.Delete(file);
        }
    }

    private static void RememberRoots(string appData, string source, string target)
    {
        var path = Path.Combine(appData, RootsName); EnsureNoLinks(path); var roots = new List<string> { source, target };
        if (File.Exists(path)) { var saved = ReadSmall<KnownRoots>(path); if (saved?.Version != 1 || saved.Roots?.Length > 16) throw new IOException("素材库历史不可读取，未更改。如何恢复请先保留原文件。"); roots.AddRange(saved?.Roots ?? Array.Empty<string>()); }
        DurableJson.Write(path, new KnownRoots(1, roots.Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToArray()));
    }
    private static FileStream Lease(string appData) { var path = Path.Combine(appData, ".library-action.lock"); EnsureNoLinks(path); return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
    private static string ReadConfiguredRoot(string appData)
    {
        var path = Path.Combine(appData, "config.json"); EnsureNoLinks(path); if (!File.Exists(path)) return appData;
        var settings = ReadSmall<Dictionary<string,string>>(path) ?? throw new IOException("素材库指针不可读取。");
        return settings.TryGetValue("DataDir", out var root) && !string.IsNullOrWhiteSpace(root) ? Full(root) : appData;
    }
    private static T? ReadSmall<T>(string path) { EnsureNoLinks(path); if (new FileInfo(path).Length > 65_536) throw new IOException("配置记录超过检查预算。"); return JsonSerializer.Deserialize<T>(File.ReadAllText(path)); }
    private static byte[] ReadOwnedVersion(string root, string path)
    {
        var name = Path.GetFileName(path); var expected = Path.Combine(root, HistoryName, name);
        if (!VersionName.IsMatch(name) || !Same(path, expected)) throw new IOException("只能恢复当前库的索引历史。");
        var bytes = ReadIndexBytes(expected); _ = ReadRecords(bytes);
        if (!name.EndsWith("-" + Hash(bytes) + ".json", StringComparison.Ordinal)) throw new IOException("索引历史校验不匹配，原文件保留。");
        return bytes;
    }
    private static byte[] ReadIndexBytes(string path, bool allowMissing = false)
    {
        EnsureNoLinks(path); if (allowMissing && !File.Exists(path)) return "[]"u8.ToArray();
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaxIndexBytes) throw new IOException("索引超过64 MiB恢复预算，当前库不会改动。");
        using var output = new MemoryStream(); var buffer = new byte[81920]; int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0) { if (read > MaxIndexBytes - output.Length) throw new IOException("索引读取超过预算。"); output.Write(buffer, 0, read); }
        return output.ToArray();
    }
    private static List<MaterialRecord> ReadRecords(byte[] bytes)
    {
        var json = System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        var rows = JsonSerializer.Deserialize<List<MaterialRecord>>(json, IndexJson) ?? throw new InvalidDataException("索引不能为null。");
        if (rows.Count > 100_000) throw new IOException("索引条目超过恢复预算。"); var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows) { if (row == null || !ids.Add(row.Id)) throw new InvalidDataException("索引条目无效。"); row.Normalize(); }
        return rows;
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool Same(string a, string b) => string.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase);
    private static string SafeRoot(string path)
    {
        var root = Full(path);
        if (root.StartsWith("\\\\", StringComparison.Ordinal) || Same(root, Path.GetPathRoot(root)!)) throw new IOException("恢复只支持本地普通文件夹。");
        EnsureNoLinks(root); if (!Directory.Exists(root)) throw new DirectoryNotFoundException("素材库目录不存在。"); return root;
    }
    private static void EnsureNoLinks(string path)
    {
        for (var item = Path.GetFullPath(path); !string.IsNullOrEmpty(item); item = Path.GetDirectoryName(item)) {
            try { if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) throw new IOException("恢复路径包含目录链接，已停止。"); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
    }
}
