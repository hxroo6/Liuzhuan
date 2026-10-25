using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Liuzhuan.Services;

public sealed record ProjectStagingReferences(string Root, long Sequence, string[] Paths);
public sealed record ProjectStagingFile(string Path, string Relative, long Bytes, long ModifiedUtc, string PackageId, string Fingerprint, bool Complete)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Detail => $"{Utils.FileClassifier.FormatSize(Bytes)} · {(Complete ? "准备完整" : "准备未完成")} · {PackageId[..8]}";
}
public sealed record ProjectStagingPreview(string Root, long Sequence, int Packages, long ReferencedBytes, long PreparedBytes,
    long IncompleteBytes, long BlockedBytes, long TrashBytes, bool Complete, ProjectStagingFile[] Files, string[] Warnings);

/// <summary>Explicit staging maintenance. Moves only validated unreferenced payloads; never deletes bindings or originals.</summary>
public static class ProjectStagingService
{
    private const string Imports = "project-imports";
    private const string Trash = "project-staging-trash-v1";
    private const int MaxFiles = 100_000;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private sealed record Binding(string Format, string PackageId, string Fingerprint, bool Complete);
    private sealed record TransferReference(string Path);
    private sealed record TrashEntry(string Source, string Destination, long Bytes, long ModifiedUtc, string Fingerprint);
    private sealed record TrashBatch(string Id, DateTime CreatedUtc, TrashEntry[] Entries);

    public static ProjectStagingReferences Snapshot(DataStore store) => new(store.DataRoot, store.CurrentSequence,
        store.Items.Concat(store.RetainedForUndo).SelectMany(x => new[] { x.FilePath, x.ThumbnailPath }).Where(x => !string.IsNullOrWhiteSpace(x)).Select(Full).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());

    // The project importer must also keep this lease across final validation, UI commit and durable FlushAsync.
    public static IDisposable AcquirePreparationLease(string root)
    {
        root = SafeRoot(root); var directory = Path.Combine(root, Imports); EnsureNoLinks(directory); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ".prepare.lock"); EnsureNoLinks(path);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new IOException("项目包正在准备、合入或整理，请完成后重试。"); }
    }

    public static Task<ProjectStagingPreview> PreviewAsync(ProjectStagingReferences references, CancellationToken ct = default) => Task.Run(() =>
    {
        var root = SafeRoot(references.Root); var warnings = new List<string>(); var candidates = new List<ProjectStagingFile>();
        long referenced = 0, prepared = 0, incomplete = 0, blocked = 0, trash = 0; int packages = 0, count = 0; bool complete = true;
        var imports = Path.Combine(root, Imports);
        using var lease = AcquirePreparationLease(root);
        var protectedPaths = references.Paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        try { ProtectTransfers(root, protectedPaths); } catch { complete = false; warnings.Add("续传记录不可完整读取，已禁止回收。"); }
        foreach (var path in Directory.EnumerateFiles(imports).Where(x => Path.GetFileName(x) != ".prepare.lock").Take(MaxFiles + 1)) {
            ct.ThrowIfCancellationRequested(); complete = false; warnings.Add("暂存根目录存在未知文件，保留原样并禁止回收。");
            try { EnsureNoLinks(path); blocked += new FileInfo(path).Length; } catch { }
            if (++count > MaxFiles) break;
        }
        var directories = Directory.EnumerateDirectories(imports).Take(1001).ToArray();
        if (directories.Length > 1000) { complete = false; warnings.Add("项目目录超过1000个检查预算。"); }
        foreach (var directory in directories.Take(1000)) {
            ct.ThrowIfCancellationRequested(); Binding? binding = null;
            try { binding = ReadBinding(root, Path.GetFileName(directory)); packages++; }
            catch { complete = false; warnings.Add("有项目绑定或路径不可读取；原文件保留，已禁止回收。"); }
            var queue = new Queue<(string Path, int Depth)>(); queue.Enqueue((directory, 0));
            while (queue.Count > 0) {
                ct.ThrowIfCancellationRequested(); var current = queue.Dequeue();
                try {
                    EnsureNoLinks(current.Path);
                    foreach (var path in Directory.EnumerateFileSystemEntries(current.Path)) {
                        ct.ThrowIfCancellationRequested(); if (++count > MaxFiles) throw new IOException("暂存项目达到10万文件检查预算。");
                        EnsureNoLinks(path);
                        if (Directory.Exists(path)) { if (current.Depth >= 3) throw new IOException("项目目录结构超出预算。"); queue.Enqueue((path, current.Depth + 1)); continue; }
                        if (Path.GetFileName(path) == "binding.json" && Same(Path.GetDirectoryName(path)!, directory)) continue;
                        var info = new FileInfo(path); var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                        if (binding == null || !SafeSource(relative, out var packageId)) { blocked += info.Length; complete = false; continue; }
                        if (protectedPaths.Contains(Full(path))) { referenced += info.Length; continue; }
                        if (binding.Complete) prepared += info.Length; else incomplete += info.Length;
                        candidates.Add(new(path, relative, info.Length, info.LastWriteTimeUtc.Ticks, packageId, binding.Fingerprint, binding.Complete));
                    }
                } catch (OperationCanceledException) { throw; }
                catch { complete = false; warnings.Add("有链接、不可访问或超预算内容；扫描不完整，已禁止回收。"); }
                if (count > MaxFiles) { queue.Clear(); break; }
            }
            if (count > MaxFiles) break;
        }
        try { foreach (var batch in Batches(root)) foreach (var entry in batch.Entries) { ct.ThrowIfCancellationRequested(); var path = TrashPath(root, batch.Id, entry.Destination); EnsureNoLinks(path); if (File.Exists(path)) trash += new FileInfo(path).Length; } }
        catch (OperationCanceledException) { throw; } catch { complete = false; warnings.Add("项目回收区不可完整读取，原文件保留。"); }
        return new ProjectStagingPreview(root, references.Sequence, packages, referenced, prepared, incomplete, blocked, trash, complete, candidates.ToArray(), warnings.Distinct().Take(8).ToArray());
    }, ct);

    // Owner-thread commit keeps material/Undo references from changing between final validation and the bounded moves.
    public static int Recycle(DataStore store, ProjectStagingPreview preview, IEnumerable<string> selected)
    {
        System.Windows.Application.Current?.Dispatcher.VerifyAccess();
        if (store.LoadFailed || !Same(store.DataRoot, preview.Root) || store.CurrentSequence != preview.Sequence || !preview.Complete) throw new InvalidOperationException("素材库已变化或扫描不完整，请重新预览。");
        var paths = selected.Select(Full).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (paths.Count is < 1 or > 200) throw new ArgumentException("每批选择1–200个暂存文件。");
        var rows = preview.Files.Where(x => paths.Contains(Full(x.Path))).ToArray(); if (rows.Length != paths.Count) throw new IOException("所选文件不在本次预览中。");
        using var lease = AcquirePreparationLease(preview.Root);
        var protectedPaths = Snapshot(store).Paths.ToHashSet(StringComparer.OrdinalIgnoreCase); ProtectTransfers(preview.Root, protectedPaths);
        foreach (var row in rows) {
            var source = SourcePath(preview.Root, row.Relative); var binding = ReadBinding(preview.Root, row.PackageId); var info = new FileInfo(source);
            if (!Same(source, row.Path) || protectedPaths.Contains(Full(source)) || !info.Exists || info.Length != row.Bytes || info.LastWriteTimeUtc.Ticks != row.ModifiedUtc ||
                binding.Fingerprint != row.Fingerprint || binding.Complete != row.Complete) throw new IOException("暂存文件、绑定或引用已变化，请重新预览。");
        }
        var id = Guid.NewGuid().ToString("N"); var directory = Path.Combine(preview.Root, Trash, id); EnsureNoLinks(directory); Directory.CreateDirectory(directory);
        var batch = new TrashBatch(id, DateTime.UtcNow, rows.Select(row => new TrashEntry(row.Relative, Guid.NewGuid().ToString("N"), row.Bytes, row.ModifiedUtc, row.Fingerprint)).ToArray());
        DurableJson.Write(Path.Combine(directory, "manifest.json"), batch);
        int moved = 0;
        foreach (var row in batch.Entries) {
            var source = SourcePath(preview.Root, row.Source); var destination = TrashPath(preview.Root, id, row.Destination); EnsureNoLinks(source); EnsureNoLinks(destination);
            File.Move(source, destination); moved++;
        }
        return moved;
    }

    public static Task<int> RestoreLatestAsync(string root, CancellationToken ct = default) => Task.Run(() =>
    {
        root = SafeRoot(root); using var lease = AcquirePreparationLease(root);
        var batch = Batches(root).FirstOrDefault(x => x.Entries.Any(y => File.Exists(TrashPath(root, x.Id, y.Destination))));
        if (batch == null) return 0; int restored = 0;
        foreach (var row in batch.Entries) {
            ct.ThrowIfCancellationRequested(); var source = TrashPath(root, batch.Id, row.Destination); var target = SourcePath(root, row.Source);
            if (!File.Exists(source)) continue;
            SafeSource(row.Source, out var package); var binding = ReadBinding(root, package);
            if (binding.Fingerprint != row.Fingerprint) throw new IOException("原项目绑定已变化，回收文件保留。");
            EnsureNoLinks(source); EnsureNoLinks(target); var info = new FileInfo(source);
            if (info.Length != row.Bytes || info.LastWriteTimeUtc.Ticks != row.ModifiedUtc) throw new IOException("回收文件已变化，停止恢复。");
            if (File.Exists(target) || Directory.Exists(target)) continue; // Explicitly never overwrite a re-prepared or user-supplied payload.
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); EnsureNoLinks(target); File.Move(source, target); restored++;
        }
        return restored; // Keep manifests and bindings, including after partial interruption; no recursive cleanup.
    }, ct);

    private static TrashBatch[] Batches(string root)
    {
        var directory = Path.Combine(root, Trash); EnsureNoLinks(directory); if (!Directory.Exists(directory)) return Array.Empty<TrashBatch>();
        var folders = Directory.EnumerateDirectories(directory).Take(1001).ToArray(); if (folders.Length > 1000) throw new IOException("项目回收批次超过检查预算。");
        var result = new List<TrashBatch>(); int entries = 0;
        foreach (var folder in folders) {
            EnsureNoLinks(folder); if (!Directory.EnumerateFileSystemEntries(folder).Any()) continue;
            var path = Path.Combine(folder, "manifest.json"); var batch = ReadSmall<TrashBatch>(path, 262144);
            if (batch == null || !Guid.TryParseExact(batch.Id, "N", out _) || batch.Id != Path.GetFileName(folder) || batch.Entries == null || batch.Entries.Length > 200) throw new IOException("项目回收记录无效。");
            var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in batch.Entries) {
                if (row == null || row.Bytes < 0 || !IsHash(row.Fingerprint) || !sources.Add(row.Source) || !destinations.Add(row.Destination)) throw new IOException("项目回收条目无效。");
                _ = SourcePath(root, row.Source); _ = TrashPath(root, batch.Id, row.Destination);
            }
            entries += batch.Entries.Length; if (entries > MaxFiles) throw new IOException("项目回收条目超过检查预算。"); result.Add(batch);
        }
        return result.OrderByDescending(x => x.CreatedUtc).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
    }
    private static void ProtectTransfers(string root, HashSet<string> paths)
    {
        var directory = Path.Combine(root, "transfers-v1"); EnsureNoLinks(directory); if (!Directory.Exists(directory)) return;
        var files = Directory.EnumerateFiles(directory, "*.json").Take(1002).ToArray(); if (files.Length > 1001) throw new IOException("续传记录超过检查预算。");
        foreach (var file in files) {
            if (Path.GetFileName(file) == "server-id.json") continue;
            var record = ReadSmall<TransferReference>(file, 65536); if (record == null || string.IsNullOrWhiteSpace(record.Path)) throw new IOException("续传记录不可读取。");
            paths.Add(Full(record.Path));
        }
    }
    private static Binding ReadBinding(string root, string package)
    {
        if (!Guid.TryParseExact(package, "D", out var id) || id.ToString("D") != package) throw new IOException("项目目录无效。");
        var directory = Path.Combine(root, Imports, package); EnsureNoLinks(directory);
        var binding = ReadSmall<Binding>(Path.Combine(directory, "binding.json"), 65536);
        if (binding == null || binding.Format != "LiuzhuanProjectPackage" || binding.PackageId != package || !IsHash(binding.Fingerprint)) throw new IOException("项目绑定无效。");
        return binding;
    }
    private static T? ReadSmall<T>(string path, long limit)
    {
        EnsureNoLinks(path); using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); if (input.Length > limit) throw new IOException("项目记录超过读取预算。");
        return JsonSerializer.Deserialize<T>(input, Json);
    }
    private static bool SafeSource(string relative, out string package)
    {
        package = ""; if (relative == null || relative.Contains('\\')) return false; var parts = relative.Split('/');
        if (parts.Length != 5 || parts[0] != Imports || parts[2] != "files" || !Guid.TryParseExact(parts[1], "D", out var id) || id.ToString("D") != parts[1] || !Guid.TryParseExact(parts[3], "N", out _)) return false;
        var name = parts[4]; if (name.Length is < 1 or > 240 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Regex.IsMatch(name.Split('.')[0].TrimEnd('.', ' '), "^(CON|PRN|AUX|NUL|CONIN\\$|CONOUT\\$|COM[0-9¹²³]|LPT[0-9¹²³])$", RegexOptions.IgnoreCase)) return false;
        package = parts[1]; return true;
    }
    private static string SourcePath(string root, string relative)
    {
        if (!SafeSource(relative, out _)) throw new IOException("项目回收来源不在受控暂存目录。"); var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)); EnsureNoLinks(path); return path;
    }
    private static string TrashPath(string root, string batch, string name)
    {
        if (!Guid.TryParseExact(batch, "N", out _) || !Guid.TryParseExact(name, "N", out _)) throw new IOException("项目回收路径无效。"); var path = Path.Combine(root, Trash, batch, name); EnsureNoLinks(path); return path;
    }
    private static bool IsHash(string? value) => value != null && Regex.IsMatch(value, "^[A-Fa-f0-9]{64}$");
    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool Same(string a, string b) => string.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase);
    private static string SafeRoot(string root)
    {
        root = Full(root); if (root.StartsWith("\\\\", StringComparison.Ordinal) || Same(root, Path.GetPathRoot(root)!)) throw new IOException("暂存管理只支持本地普通文件夹。"); EnsureNoLinks(root); if (!Directory.Exists(root)) throw new DirectoryNotFoundException("素材库不存在。"); return root;
    }
    private static void EnsureNoLinks(string path)
    {
        for (var item = Path.GetFullPath(path); !string.IsNullOrEmpty(item); item = Path.GetDirectoryName(item)) {
            try { if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) throw new IOException("项目路径含链接，停止操作。"); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
    }
}
