using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Liuzhuan.Models;

namespace Liuzhuan.Services;

/// <summary>可移植迁移包：只收集索引引用的文件，不复制整个用户目录。</summary>
public static class MigrationService
{
    public sealed class Settings
    {
        public bool ClipboardMonitorEnabled { get; set; }
        public string[]? ExcludedClipboardApps { get; set; } = Array.Empty<string>();
        public HeicConversionMode HeicConversion { get; set; } = HeicConversionMode.Png;
        public bool LanEnabled { get; set; }
        public int Port { get; set; } = 8899;
        public string Password { get; set; } = "";
        public bool AutoStart { get; set; }
        public List<SavedMaterialView>? SavedViews { get; set; } = new();
        public ImageProcessingPresetDocument? ImagePresets { get; set; }
        public AutomationDocument? Automation { get; set; }
        public StorageRetention? StorageRetention {get;set;}
    }
    public sealed class Payload
    {
        public string Path { get; set; } = "";
        public long Size { get; set; }
        public string Sha256 { get; set; } = "";
    }
    public sealed class Manifest
    {
        [JsonRequired] public string Format { get; set; } = "LiuzhuanMigration";
        [JsonRequired] public int Version { get; set; } = 1;
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        [JsonRequired] public Settings Settings { get; set; } = new();
        [JsonRequired] public List<MaterialItem> Items { get; set; } = new();
        [JsonRequired] public List<Payload> Files { get; set; } = new();
    }
    // No MaterialItem setters may run while parsing an untrusted package.
    private sealed class ManifestRecord
    {
        [JsonRequired] public string Format { get; set; } = "LiuzhuanMigration";
        [JsonRequired] public int Version { get; set; } = 1;
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        [JsonRequired] public Settings Settings { get; set; } = new();
        [JsonRequired] public List<MaterialRecord> Items { get; set; } = new();
        [JsonRequired] public List<Payload> Files { get; set; } = new();
    }
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter() }
    };
    private const long MaxBytes = 1L << 40;
    private const string PendingName = "migration-pending.json";

    // 在界面线程生成快照，后台导出不枚举实时 ObservableCollection。
    public static Manifest Snapshot(IEnumerable<MaterialItem> items, Settings settings)
    {
        var copied=items.Select(item=>MaterialRecord.Capture(item,true).CreateMaterial()).ToList();
        return new(){Items=copied,Settings=JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings,Json),Json)!};
    }

    public static async Task ExportAsync(string destination, Manifest snapshot, IProgress<string>? progress, CancellationToken ct)
    {
        destination = System.IO.Path.GetFullPath(destination);
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        var manifest = Snapshot(snapshot.Items, snapshot.Settings);
        manifest.Format = snapshot.Format; manifest.Version = snapshot.Version; manifest.CreatedUtc = snapshot.CreatedUtc;
        var copied = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                async Task<string> Pack(string source)
                {
                    source = System.IO.Path.GetFullPath(source);
                    if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("导出位置不能覆盖正在打包的素材文件。");
                    if (copied.TryGetValue(source, out var existing)) return existing;
                    var name = System.IO.Path.GetFileName(source);
                    var key = "files/" + Guid.NewGuid().ToString("N") + "/" + name;
                    if (!SafePayloadPath(key)) throw new IOException("素材文件名无法打包：" + name);
                    await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                    var expected = input.Length;
                    if (expected > MaxBytes - manifest.Files.Sum(f => f.Size)) throw new IOException("迁移包超过 1 TB，请分批整理素材。");
                    var entry = zip.CreateEntry(key, CompressionLevel.Fastest);
                    await using var target = entry.Open();
                    var result = await CopyChecked(input, target, expected, name, progress, ct);
                    manifest.Files.Add(new Payload { Path = key, Size = result.Size, Sha256 = result.Hash });
                    copied.Add(source, key);
                    return key;
                }
                foreach (var item in manifest.Items)
                {
                    ct.ThrowIfCancellationRequested();
                    // Only a baseline bound to this source is portable; never replace its digest
                    // with the packaging hash, since legitimate edits must remain detectable.
                    var baseline = IntegrityService.NormalizeBaseline(item.IntegrityBaseline);
                    if (baseline != null && (baseline.SourceType != item.Type || !string.Equals(baseline.SourcePath, item.FilePath, StringComparison.OrdinalIgnoreCase))) baseline = null;
                    if (item.HasFileSource)
                    {
                        if (!File.Exists(item.FilePath)) throw new FileNotFoundException("素材原文件无法访问，请在来源管理中重新定位，或移除失效索引后重试：" + item.DisplayName);
                        item.FilePath = await Pack(item.FilePath);
                        item.Size = manifest.Files.Single(f => f.Path == item.FilePath).Size;
                    }
                    else item.FilePath = "";
                    item.IntegrityBaseline = baseline == null ? null : baseline with { SourcePath = item.FilePath };
                    item.ThumbnailPath = File.Exists(item.ThumbnailPath) ? await Pack(item.ThumbnailPath) : "";
                }
                var metadata = zip.CreateEntry("manifest.json");
                await using var stream = metadata.Open();
                await JsonSerializer.SerializeAsync(stream, manifest, Json, ct);
            }
            ct.ThrowIfCancellationRequested();
            // 输出关闭且索引可解析后，才替换用户选择的目标文件。
            progress?.Report("正在校验迁移包索引…");
            _ = ReadManifest(temp);
            ct.ThrowIfCancellationRequested();
            File.Move(temp, destination, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static Manifest ReadManifest(string package)
    {
        using var zip = ZipFile.OpenRead(package);
        if (zip.Entries.Count > 100001) throw new InvalidDataException("包内文件数量过多。");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            if (!entries.TryAdd(entry.FullName, entry)) throw new InvalidDataException("迁移包包含重复路径。");
            if (entry.FullName != "manifest.json" && !SafePayloadPath(entry.FullName)) throw new InvalidDataException("迁移包包含不安全路径。");
        }
        if (!entries.TryGetValue("manifest.json", out var metadata) || metadata.Length > 32 * 1024 * 1024)
            throw new InvalidDataException("不是有效的流转迁移包，或索引过大。");
        using var input = metadata.Open();
        var manifest = JsonSerializer.Deserialize<ManifestRecord>(input, Json) ?? throw new InvalidDataException("迁移包索引为空。");
        if (manifest.Format != "LiuzhuanMigration" || manifest.Version != 1 || manifest.Settings == null || manifest.Items == null || manifest.Files == null)
            throw new InvalidDataException("迁移包版本不支持，请使用匹配版本的流转。");
        var cfg = manifest.Settings;
        try { if (cfg.Automation != null) cfg.Automation = AutomationStore.Normalize(cfg.Automation); }
        catch (ArgumentException ex) { throw new InvalidDataException("自动整理规则无效。", ex); }
        try {if(cfg.StorageRetention!=null)cfg.StorageRetention=StorageRetention.Validate(cfg.StorageRetention);}
        catch(ArgumentException ex){throw new InvalidDataException("历史保留策略无效。",ex);}
        try { cfg.SavedViews = SavedViewStore.NormalizeAll(cfg.SavedViews); }
        catch (ArgumentException ex) { throw new InvalidDataException("常用筛选无效。", ex); }
        try { if (cfg.ImagePresets != null) cfg.ImagePresets = ImageProcessingPresetStore.NormalizeDocument(cfg.ImagePresets); }
        catch (ArgumentException ex) { throw new InvalidDataException("图片处理预设无效。", ex); }
        try { cfg.ExcludedClipboardApps = ClipboardPrivacyPolicy.NormalizeApps(cfg.ExcludedClipboardApps); }
        catch (ArgumentException ex) { throw new InvalidDataException("剪贴板应用排除配置无效。", ex); }
        if (!Enum.IsDefined(typeof(HeicConversionMode), cfg.HeicConversion) || cfg.Port is < 1 or > 65533 || cfg.Password == null || cfg.Password.Length > 128)
            throw new InvalidDataException("迁移包配置无效。");
        if (manifest.Items.Count > 100000) throw new InvalidDataException("素材条目过多。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in manifest.Files)
        {
            if (file == null || !SafePayloadPath(file.Path) || !paths.Add(file.Path) || !entries.TryGetValue(file.Path, out var entry) || file.Size < 0 || entry.FullName != file.Path || entry.Length != file.Size || file.Size > MaxBytes - total || !Regex.IsMatch(file.Sha256 ?? "", "^[A-Fa-f0-9]{64}$"))
                throw new InvalidDataException("素材文件清单或长度无效。");
            total += file.Size;
        }
        if (entries.Count != paths.Count + 1) throw new InvalidDataException("迁移包包含清单之外的文件。");
        var ids = new HashSet<string>();
        foreach (var item in manifest.Items)
        {
            if (item == null || string.IsNullOrEmpty(item.Id) || !ids.Add(item.Id) || item.DisplayName == null || item.TextContent == null || item.ThumbnailPath == null || item.FilePath == null || !Enum.IsDefined(typeof(MaterialType), item.Type) || item.Size < 0 || !double.IsFinite(item.Duration) || item.Duration < 0)
                throw new InvalidDataException("素材索引无效。");
            // Optional organization fields keep version-1 packages compatible, including
            // old packages with missing/null values. Reject invalid limits before staging.
            try
            {
                item.Normalize(portable: true);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException("素材分组、标签或备注无效。", ex);
            }
            try
            {
                item.IntegrityBaseline = IntegrityService.NormalizeBaseline(item.IntegrityBaseline);
                if (item.IntegrityBaseline is { } baseline && (!item.HasFileSource || baseline.SourceType != item.Type || baseline.SourcePath != item.FilePath))
                    throw new ArgumentException("基线绑定的来源不匹配。");
            }
            catch (ArgumentException ex) { throw new InvalidDataException("素材完整性基线无效。", ex); }
            if ((item.HasFileSource && !paths.Contains(item.FilePath)) || (item.ThumbnailPath != "" && !paths.Contains(item.ThumbnailPath)))
                throw new InvalidDataException("素材引用了包外文件。");
        }
        return new Manifest { Format=manifest.Format, Version=manifest.Version, CreatedUtc=manifest.CreatedUtc,
            Settings=manifest.Settings, Files=manifest.Files, Items=manifest.Items.Select(x=>x.CreateMaterial()).ToList() };
    }

    public static async Task<string> PrepareImportAsync(string package, string appData, IProgress<string>? progress, CancellationToken ct, Manifest? expected = null)
    {
        appData = System.IO.Path.GetFullPath(appData);
        Directory.CreateDirectory(appData);
        var pending = System.IO.Path.Combine(appData, PendingName);
        if (File.Exists(pending)) throw new IOException("已有待应用的导入，请先退出并重新打开流转。");
        // 独占读取来源，验证与解包期间禁止外部写入/替换包。
        await using var packageLock = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read);
        var manifest = ReadManifest(package);
        if (expected != null && JsonSerializer.Serialize(expected, Json) != JsonSerializer.Serialize(manifest, Json)) throw new IOException("迁移包在预览后发生变化，请重新选择。");
        var total = manifest.Files.Sum(f => f.Size);
        var drive = new DriveInfo(System.IO.Path.GetPathRoot(appData)!);
        if (drive.IsReady && drive.AvailableFreeSpace < total + 64 * 1024 * 1024) throw new IOException("目标磁盘空间不足，无法完整导入。");
        var id = Guid.NewGuid().ToString("N");
        var root = System.IO.Path.Combine(appData, "imports", id);
        Directory.CreateDirectory(root);
        bool queued = false;
        try
        {
            using var zip = new ZipArchive(packageLock, ZipArchiveMode.Read, true);
            foreach (var file in manifest.Files)
            {
                ct.ThrowIfCancellationRequested();
                var path = ContainedPath(root, file.Path);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                await using var input = zip.GetEntry(file.Path)!.Open();
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                var result = await CopyChecked(input, output, file.Size, System.IO.Path.GetFileName(path), progress, ct);
                if (!result.Hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("文件校验失败：" + System.IO.Path.GetFileName(path));
            }
            foreach (var item in manifest.Items)
            {
                if (item.HasFileSource) item.FilePath = ContainedPath(root, item.FilePath);
                if (item.IntegrityBaseline is { } baseline) item.IntegrityBaseline = baseline with { SourcePath = item.FilePath };
                if (item.ThumbnailPath != "") item.ThumbnailPath = ContainedPath(root, item.ThumbnailPath);
            }
            await File.WriteAllTextAsync(System.IO.Path.Combine(root, "data.json"), JsonSerializer.Serialize(manifest.Items, Json), ct);
            await File.WriteAllTextAsync(System.IO.Path.Combine(root, "savedviews.json"), JsonSerializer.Serialize(manifest.Settings.SavedViews, Json), ct);
            if (manifest.Settings.ImagePresets != null)
                await File.WriteAllTextAsync(System.IO.Path.Combine(root, ImageProcessingPresetStore.FileName), JsonSerializer.Serialize(manifest.Settings.ImagePresets, Json), ct);
            if (manifest.Settings.Automation != null)
                await File.WriteAllTextAsync(System.IO.Path.Combine(root, "automation.json"), JsonSerializer.Serialize(manifest.Settings.Automation, AutomationStore.Json), ct);
            if(manifest.Settings.StorageRetention!=null)
                await File.WriteAllTextAsync(System.IO.Path.Combine(root,"storage-retention.json"),JsonSerializer.Serialize(manifest.Settings.StorageRetention,Json),ct);
            await File.WriteAllTextAsync(System.IO.Path.Combine(root, "appsettings.json"), JsonSerializer.Serialize(new { manifest.Settings.ClipboardMonitorEnabled, manifest.Settings.ExcludedClipboardApps, HeicConversion = manifest.Settings.HeicConversion.ToString() }, Json), ct);
            await File.WriteAllTextAsync(System.IO.Path.Combine(root, "lan.json"), JsonSerializer.Serialize(new { Enabled = manifest.Settings.LanEnabled, manifest.Settings.Port, manifest.Settings.Password }, Json), ct);
            await File.WriteAllTextAsync(System.IO.Path.Combine(root, "migration-settings.json"), JsonSerializer.Serialize(manifest.Settings, Json), ct);
            ct.ThrowIfCancellationRequested();
            // 运行中的素材库仍写旧目录；仅在下一次启动、创建 DataStore 之前切换。
            AtomicWrite(pending, JsonSerializer.Serialize(id), false);
            queued = true;
            return root;
        }
        finally
        {
            if (!queued && Directory.Exists(root))
            {
                var safeRoot = ContainedPath(appData, "imports/" + id);
                if (safeRoot != System.IO.Path.GetFullPath(root)) throw new IOException("临时目录校验失败。");
                Directory.Delete(safeRoot, true);
            }
        }
    }

    public static Settings? ApplyPending(string appData)
    {
        var pending = System.IO.Path.Combine(appData, PendingName);
        if (!File.Exists(pending)) return null;
        var id = JsonSerializer.Deserialize<string>(File.ReadAllText(pending));
        if (id == null || !Regex.IsMatch(id, "^[a-f0-9]{32}$")) throw new InvalidDataException("待导入目录无效。");
        var root = ContainedPath(appData, "imports/" + id);
        foreach (var name in new[] { "data.json", "lan.json", "appsettings.json", "migration-settings.json" })
            if (!File.Exists(System.IO.Path.Combine(root, name))) throw new InvalidDataException("待导入素材库不完整。");
        var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(System.IO.Path.Combine(root, "migration-settings.json")), Json)!;
        var config = System.IO.Path.Combine(appData, "config.json");
        // 保留原配置指针；原素材库始终不覆盖，便于回退。
        var backup = System.IO.Path.Combine(appData, "config.before-import-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + id + ".json");
        if (File.Exists(config)) File.Copy(config, backup, false);
        else File.WriteAllText(backup, JsonSerializer.Serialize(new { DataDir = System.IO.Path.GetFullPath(appData) }));
        AtomicWrite(config, JsonSerializer.Serialize(new { DataDir = root }), true);
        File.Delete(pending);
        return settings;
    }

    private static bool SafePayloadPath(string? path)
    {
        if (path == null || path.Contains('\\')) return false;
        var parts = path.Split('/');
        if (parts.Length != 3 || parts[0] != "files" || !Regex.IsMatch(parts[1], "^[a-f0-9]{32}$")) return false;
        var name = parts[2];
        return name.Length is > 0 and <= 240 && name != "." && name != ".." && !name.EndsWith('.') && !name.EndsWith(' ') && name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0 && !Regex.IsMatch(name, "^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])($|\\.)", RegexOptions.IgnoreCase);
    }
    private static string ContainedPath(string root, string relative)
    {
        root = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        var result = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative));
        if (!result.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("路径越界。");
        return result;
    }
    private static void AtomicWrite(string path, string content, bool overwrite)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, content); File.Move(temp, path, overwrite); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static async Task<(long Size, string Hash)> CopyChecked(Stream input, Stream output, long expected, string name, IProgress<string>? progress, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920]; long size = 0; long last = 0;
        int count;
        while ((count = await input.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            if (count > expected - size) throw new InvalidDataException("文件长度超出清单。");
            await output.WriteAsync(buffer.AsMemory(0, count), ct); hash.AppendData(buffer, 0, count); size += count;
            if (Environment.TickCount64 - last > 250) { progress?.Report($"{name} · {size / 1048576.0:F1} / {expected / 1048576.0:F1} MB"); last = Environment.TickCount64; }
        }
        if (size != expected) throw new InvalidDataException("文件不完整：" + name);
        ct.ThrowIfCancellationRequested();
        return (size, Convert.ToHexString(hash.GetHashAndReset()));
    }
}
