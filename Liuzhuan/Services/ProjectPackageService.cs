using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Liuzhuan.Models;

namespace Liuzhuan.Services;

/// <summary>Portable selected materials, distinct from whole-library migration. Never changes a DataStore.</summary>
public static class ProjectPackageService
{
    public const int MaxItems = 1000;
    public const int MaxManifestBytes = 16 * 1024 * 1024;
    public const long MaxPayloadBytes = 64L * 1024 * 1024 * 1024;
    public const int MaxFileTextPreviewChars = 5000;
    public const int MaxInlineTextChars = 262144;
    private const int BufferSize = 81920;
    private const string Format = "LiuzhuanProjectPackage";
    private static readonly SemaphoreSlim ImportGate = new(1, 1);
    private static readonly uint[] CrcTable = BuildCrcTable();
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, MaxDepth = 32,
        Converters = { new JsonStringEnumConverter() }
    };
    private sealed class Manifest
    {
        [JsonRequired] public string Format { get; set; } = ProjectPackageService.Format;
        [JsonRequired] public int Version { get; set; } = 1;
        [JsonRequired] public string PackageId { get; set; } = "";
        [JsonRequired] public DateTimeOffset CreatedUtc { get; set; }
        [JsonRequired] public List<Entry> Entries { get; set; } = new();
        [JsonRequired] public List<Payload> Files { get; set; } = new();
    }
    private sealed class Entry
    {
        [JsonRequired] public string EntryId { get; set; } = "";
        [JsonRequired] public string Name { get; set; } = "";
        [JsonRequired] public MaterialType Type { get; set; }
        [JsonRequired] public string TextContent { get; set; } = "";
        [JsonRequired] public string Payload { get; set; } = "";
        [JsonRequired] public bool IsFavorite { get; set; }
        [JsonRequired] public string GroupName { get; set; } = "";
        [JsonRequired] public string[] Tags { get; set; } = Array.Empty<string>();
        [JsonRequired] public string Note { get; set; } = "";
        [JsonRequired] public DateTimeOffset AddedTime { get; set; }
    }
    private sealed class Payload
    {
        [JsonRequired] public string Path { get; set; } = "";
        [JsonRequired] public long Size { get; set; }
        [JsonRequired] public string Sha256 { get; set; } = "";
    }
    private sealed record Binding(string Format, string PackageId, string Fingerprint, bool Complete);
    private sealed record Parsed(Manifest Manifest, ProjectPackagePreview Preview);

    /// <summary>Capture on the owner thread; pure text is complete, file-backed text is carried by its original file.</summary>
    public static ProjectPackageSnapshot Snapshot(IEnumerable<MaterialItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<ProjectPackageSource>();
        foreach (var item in items)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Id)) throw new ArgumentException("素材选择无效。");
            if (!seen.Add(item.Id)) continue;
            if (entries.Count == MaxItems) throw new ArgumentException($"项目包每次最多包含 {MaxItems} 条素材。");
            if (!Enum.IsDefined(typeof(MaterialType), item.Type)) throw new ArgumentException("素材类型无效。");
            var organization = MaterialOrganization.Normalize(item.GroupName, item.Tags, item.Note);
            var hasFile = item.HasFileSource;
            if (!hasFile && (item.TextContent?.Length ?? 0) > MaxInlineTextChars) throw new ArgumentException("单条项目文字超过 262144 字符，请减少内容；正文不会被截断。");
            var added = new DateTimeOffset(item.AddedTime).ToUniversalTime();
            if (!ValidTime(added)) throw new ArgumentException("素材时间无效或无法转换为本机时间。");
            entries.Add(new(Guid.NewGuid().ToString("D"), ValidateName(item.DisplayName), item.Type,
                hasFile ? item.FilePath : "", hasFile ? item.Type == MaterialType.Text ? FileTextPreview(item.TextContent ?? "") : "" : item.TextContent ?? "", item.IsFavorite,
                organization.GroupName!, Array.AsReadOnly(organization.Tags!), organization.Note!, added));
        }
        if (entries.Count == 0) throw new ArgumentException("请先选择需要打包的素材。");
        return new(Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow, entries);
    }

    public static Task<ProjectPackagePreview> ExportAsync(string destination, ProjectPackageSnapshot snapshot,
        IProgress<ProjectPackageProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Task.Run(() => ExportCoreAsync(destination, snapshot, progress, cancellationToken), cancellationToken);
    }
    private static async Task<ProjectPackagePreview> ExportCoreAsync(string destination, ProjectPackageSnapshot snapshot,
        IProgress<ProjectPackageProgress>? progress, CancellationToken ct)
    {
        var target = Path.GetFullPath(destination);
        var parent = Path.GetDirectoryName(target) ?? throw new ArgumentException("导出位置无效。");
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("请选择已存在的导出文件夹。");
        EnsureNoLinks(parent); EnsureNoLinks(target);
        var manifest = new Manifest { PackageId = snapshot.PackageId, CreatedUtc = snapshot.CreatedUtc };
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var source in snapshot.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var entry = new Entry { EntryId = source.EntryId, Name = source.Name, Type = source.Type, TextContent = source.TextContent,
                IsFavorite = source.IsFavorite, GroupName = source.GroupName, Tags = source.Tags.ToArray(), Note = source.Note, AddedTime = source.AddedTime };
            if (!string.IsNullOrWhiteSpace(source.FilePath))
            {
                var path = Path.GetFullPath(source.FilePath);
                if (string.Equals(path, target, StringComparison.OrdinalIgnoreCase)) throw new IOException("项目包不能覆盖正在打包的原文件。");
                if (!paths.TryGetValue(path, out var key))
                {
                    var info = new FileInfo(path); var length = info.Length;
                    if (length > MaxPayloadBytes - total) throw new IOException("项目包原文件总量超过 64 GiB，请减少选择。");
                    total += length;
                    key = "files/" + Guid.NewGuid().ToString("N") + "/" + SafeExportName(Path.GetFileName(path));
                    paths[path] = key; sources[key] = path;
                    manifest.Files.Add(new() { Path = key, Size = length, Sha256 = new string('0', 64) });
                }
                entry.Payload = key;
            }
            manifest.Entries.Add(entry);
        }
        Validate(manifest, null);
        _ = SerializeManifest(manifest);
        var partial = target + "." + Guid.NewGuid().ToString("N") + ".partial";
        bool partialCreated = false;
        var notifier = new Notifier(progress); long copied = 0; int completed = 0;
        try
        {
            EnsureNoLinks(parent); EnsureNoLinks(partial);
            var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, true);
            partialCreated = true;
            await using (output)
            {
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    foreach (var file in manifest.Files)
                    {
                        ct.ThrowIfCancellationRequested();
                        var name = Path.GetFileName(sources[file.Path]);
                        notifier.Send("正在打包原文件", completed, manifest.Files.Count, copied, total, name);
                        await using var input = new FileStream(sources[file.Path], FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        if (input.Length != file.Size) throw new IOException("原文件在打包前发生变化，请重新选择：" + name);
                        var member = zip.CreateEntry(file.Path, CompressionLevel.Fastest);
                        await using var payloadOutput = member.Open();
                        file.Sha256 = await CopyHashAsync(input, payloadOutput, file.Size, n => notifier.Send("正在打包原文件", completed, manifest.Files.Count, copied + n, total, name), ct);
                        copied += file.Size; completed++;
                    }
                    var bytes = SerializeManifest(manifest);
                    var metadata = zip.CreateEntry("manifest.json", CompressionLevel.Fastest);
                    await using var stream = metadata.Open(); await stream.WriteAsync(bytes, ct);
                }
                await output.FlushAsync(ct); output.Flush(true);
            }
            ct.ThrowIfCancellationRequested();
            ProjectPackagePreview preview;
            await using (var input = new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var archive = new ZipArchive(input, ZipArchiveMode.Read, true))
                preview = ReadParsed(archive).Preview;
            EnsureNoLinks(parent); EnsureNoLinks(target); ct.ThrowIfCancellationRequested();
            File.Move(partial, target, true);
            notifier.Send("打包完成", completed, manifest.Files.Count, copied, total, Path.GetFileName(target), true);
            return preview;
        }
        finally { if (partialCreated) DeleteOwnedPartial(partial); }
    }

    /// <summary>Full verification + deterministic staging. Opening a UI alone must not call this.</summary>
    public static Task<ProjectPackagePrepared> ReadPreviewAsync(string package, string dataRoot,
        IProgress<ProjectPackageProgress>? progress = null, CancellationToken cancellationToken = default) =>
        PrepareImportAsync(package, dataRoot, progress, cancellationToken);

    public static Task<ProjectPackagePrepared> PrepareImportAsync(string package, string dataRoot,
        IProgress<ProjectPackageProgress>? progress = null, CancellationToken cancellationToken = default,
        ProjectPackagePreview? expectedPreview = null) => Task.Run(async () =>
    {
        await ImportGate.WaitAsync(cancellationToken);
        try
        {
            var ct = cancellationToken; ct.ThrowIfCancellationRequested();
            var root = SafeRoot(dataRoot); var notifier = new Notifier(progress);
            notifier.Send("正在读取项目包清单", 0, 1, 0, 0, Path.GetFileName(package), true);
            await using var packageLock = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, true);
            using var archive = new ZipArchive(packageLock, ZipArchiveMode.Read, true);
            var parsed = ReadParsed(archive);
            if (expectedPreview != null && (expectedPreview.PackageId != parsed.Preview.PackageId || expectedPreview.Fingerprint != parsed.Preview.Fingerprint))
                throw new IOException("项目包在预览后发生变化，请重新选择并核对。");
            ct.ThrowIfCancellationRequested();
            var imports = Contained(root, "project-imports"); EnsureDirectory(imports);
            var lockPath = Contained(imports, ".prepare.lock"); EnsureNoLinks(lockPath);
            await using var libraryLease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var directory = Contained(imports, parsed.Preview.PackageId); EnsureNoLinks(directory);
            var bindingPath = Contained(directory, "binding.json");
            if (Directory.Exists(directory))
            {
                if (!File.Exists(bindingPath) && Directory.EnumerateFileSystemEntries(directory).Any()) throw new IOException("已有项目目录缺少绑定记录，原文件已保留，请先检查。");
            }
            EnsureDirectory(directory);
            var binding = ReadBinding(bindingPath);
            if (binding != null && (binding.Format != Format || binding.PackageId != parsed.Preview.PackageId || binding.Fingerprint != parsed.Preview.Fingerprint))
                throw new IOException("相同项目包编号对应了不同内容，已停止合入并保留原文件。");
            if (binding == null)
            {
                binding = new(Format, parsed.Preview.PackageId, parsed.Preview.Fingerprint, false);
                WriteBinding(bindingPath, binding);
            }
            long copied = 0; int completed = 0;
            foreach (var file in parsed.Manifest.Files)
            {
                ct.ThrowIfCancellationRequested();
                var destination = Contained(directory, file.Path); EnsureNoLinks(destination);
                EnsureDirectory(Path.GetDirectoryName(destination)!);
                notifier.Send("正在验证并准备原文件", completed, parsed.Manifest.Files.Count, copied, parsed.Preview.PayloadBytes, Path.GetFileName(file.Path));
                if (File.Exists(destination))
                {
                    // Verify BOTH the supplied archive and any already staged bytes; neither mtime nor a past receipt is truth.
                    var member = archive.GetEntry(file.Path)!;
                    await using (var source = member.Open())
                        await VerifyHashAsync(source, file.Size, file.Sha256, n => notifier.Send("正在验证并准备原文件", completed, parsed.Manifest.Files.Count, copied + n, parsed.Preview.PayloadBytes, Path.GetFileName(file.Path)), ct, member.Crc32);
                    await VerifyFileAsync(destination, file.Size, file.Sha256, ct);
                }
                else
                {
                    var partial = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
                    bool partialCreated = false;
                    try
                    {
                        EnsureNoLinks(partial);
                        var member = archive.GetEntry(file.Path)!;
                        await using (var source = member.Open())
                        {
                            var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, true);
                            partialCreated = true;
                            await using (output)
                            {
                                var digest = await CopyHashAsync(source, output, file.Size, n => notifier.Send("正在验证并准备原文件", completed, parsed.Manifest.Files.Count, copied + n, parsed.Preview.PayloadBytes, Path.GetFileName(file.Path)), ct, member.Crc32);
                                if (!digest.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("项目包文件 SHA-256 不匹配：" + Path.GetFileName(file.Path));
                                await output.FlushAsync(ct); output.Flush(true);
                            }
                        }
                        EnsureNoLinks(destination); EnsureNoLinks(partial); ct.ThrowIfCancellationRequested();
                        File.Move(partial, destination, false);
                    }
                    finally { if (partialCreated) DeleteOwnedPartial(partial); }
                }
                copied += file.Size; completed++;
            }
            ct.ThrowIfCancellationRequested();
            WriteBinding(bindingPath, binding with { Complete = true });
            var payloads = parsed.Manifest.Files.ToDictionary(x => x.Path, StringComparer.Ordinal);
            var rows = parsed.Manifest.Entries.Select(entry => new ProjectPackageImportRow(entry.EntryId,
                "project:" + parsed.Manifest.PackageId + ":" + entry.EntryId, entry.Name, entry.Type, entry.TextContent,
                entry.Payload.Length == 0 ? "" : Contained(directory, entry.Payload),
                entry.Payload.Length == 0 ? entry.TextContent.Length : payloads[entry.Payload].Size,
                entry.IsFavorite, entry.GroupName, Array.AsReadOnly(entry.Tags.ToArray()), entry.Note, entry.AddedTime.LocalDateTime)).ToArray();
            notifier.Send("准备完成，等待确认合入", completed, parsed.Manifest.Files.Count, copied, parsed.Preview.PayloadBytes, "", true);
            return new ProjectPackagePrepared(root, directory, parsed.Preview, rows,
                parsed.Manifest.Files.Select(x => new ProjectPackagePayload(x.Path, x.Size, x.Sha256)));
        }
        finally { ImportGate.Release(); }
    }, cancellationToken);

    /// <summary>Recheck owned staging before the caller begins an uncancellable owner-thread commit.</summary>
    public static Task ValidatePreparedAsync(ProjectPackagePrepared prepared,
        IProgress<ProjectPackageProgress>? progress = null, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        ArgumentNullException.ThrowIfNull(prepared);
        var root = SafeRoot(prepared.DataRoot);
        var expected = Contained(Contained(root, "project-imports"), prepared.Preview.PackageId);
        if (!string.Equals(expected, prepared.DirectoryPath, StringComparison.OrdinalIgnoreCase)) throw new IOException("项目准备目录无效。");
        EnsureNoLinks(expected);
        var binding = ReadBinding(Contained(expected, "binding.json"));
        if (binding == null || !binding.Complete || binding.Format != Format || binding.PackageId != prepared.Preview.PackageId || binding.Fingerprint != prepared.Preview.Fingerprint)
            throw new IOException("项目准备状态变化，请重新预览。");
        var notifier = new Notifier(progress); int completed = 0; long bytes = 0;
        foreach (var file in prepared.Payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            notifier.Send("正在复核已准备原文件", completed, prepared.Payloads.Count, bytes, prepared.Preview.PayloadBytes, Path.GetFileName(file.Path));
            await VerifyFileAsync(Contained(expected, file.Path), file.Size, file.Sha256, cancellationToken);
            completed++; bytes += file.Size;
        }
        cancellationToken.ThrowIfCancellationRequested();
    }, cancellationToken);

    private static Parsed ReadParsed(ZipArchive archive)
    {
        if (archive.Entries.Count is <1 or > MaxItems + 1) throw new InvalidDataException("项目包成员数量超过预算。");
        var members = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in archive.Entries)
        {
            if (!members.TryAdd(member.FullName, member)) throw new InvalidDataException("项目包包含重复或大小写冲突的路径。");
            if (member.FullName != "manifest.json" && !SafePayloadPath(member.FullName)) throw new InvalidDataException("项目包包含不安全路径。");
        }
        if (!members.TryGetValue("manifest.json", out var metadata) || metadata.FullName != "manifest.json" || metadata.Length > MaxManifestBytes)
            throw new InvalidDataException("项目包清单缺失或超过 16 MiB。");
        byte[] bytes;
        using (var stream = metadata.Open()) bytes = ReadBounded(stream, MaxManifestBytes, metadata.Crc32);
        var manifest = JsonSerializer.Deserialize<Manifest>(bytes, Json) ?? throw new InvalidDataException("项目包清单为空。");
        Validate(manifest, members);
        var fingerprint = Convert.ToHexString(SHA256.HashData(bytes));
        var entries = manifest.Entries.Select(x => new ProjectPackageEntryPreview(x.EntryId, x.Name, x.Type, x.TextContent,
            x.Payload.Length > 0, x.IsFavorite, x.GroupName, Array.AsReadOnly(x.Tags.ToArray()), x.Note, x.AddedTime)).ToArray();
        var preview = new ProjectPackagePreview(manifest.PackageId, fingerprint, manifest.CreatedUtc,
            entries.Length, manifest.Files.Count, manifest.Files.Sum(x => x.Size), Array.AsReadOnly(entries));
        return new(manifest, preview);
    }
    private static void Validate(Manifest manifest, IReadOnlyDictionary<string, ZipArchiveEntry>? members)
    {
        if (manifest.Format != Format || manifest.Version != 1 || !IsGuid(manifest.PackageId) || !ValidTime(manifest.CreatedUtc) ||
            manifest.Entries == null || manifest.Entries.Count is <1 or > MaxItems || manifest.Files == null || manifest.Files.Count > MaxItems)
            throw new InvalidDataException("不是有效的项目素材包，或版本/预算不支持。");
        long total = 0; var files = new Dictionary<string, Payload>(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            if (file == null || !SafePayloadPath(file.Path) || !files.TryAdd(file.Path, file) || file.Size < 0 || file.Size > MaxPayloadBytes - total || !IsHash(file.Sha256))
                throw new InvalidDataException("项目包文件清单或容量无效。");
            if (members != null && (!members.TryGetValue(file.Path, out var member) || member.FullName != file.Path || member.Length != file.Size))
                throw new InvalidDataException("项目包文件缺失或长度不符。");
            total += file.Size;
        }
        var ids = new HashSet<string>(StringComparer.Ordinal); var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in manifest.Entries)
        {
            if (entry == null || !IsGuid(entry.EntryId) || !ids.Add(entry.EntryId) || entry.TextContent == null || entry.Payload == null ||
                !ValidTime(entry.AddedTime) || entry.Payload.Length == 0 && entry.TextContent.Length > MaxInlineTextChars ||
                !Enum.IsDefined(typeof(MaterialType), entry.Type) || entry.Type != MaterialType.Text && entry.Payload.Length == 0 ||
                entry.Payload.Length > 0 && (!files.ContainsKey(entry.Payload) ||
                    (entry.Type == MaterialType.Text ? entry.TextContent.Length > MaxFileTextPreviewChars : entry.TextContent.Length != 0)))
                throw new InvalidDataException("项目包素材条目无效，或引用了清单外文件。");
            ValidateName(entry.Name);
            try
            {
                var organization = MaterialOrganization.Normalize(entry.GroupName, entry.Tags, entry.Note);
                entry.GroupName = organization.GroupName!; entry.Tags = organization.Tags!; entry.Note = organization.Note!;
            }
            catch (ArgumentException ex) { throw new InvalidDataException("项目包整理信息无效。", ex); }
            if (entry.Payload.Length > 0) used.Add(entry.Payload);
        }
        if (used.Count != files.Count || members != null && members.Count != files.Count + 1) throw new InvalidDataException("项目包包含未被素材引用的文件或额外成员。");
    }
    private static bool IsGuid(string? value) => value != null && Guid.TryParseExact(value, "D", out var id) && id.ToString("D") == value;
    private static bool ValidTime(DateTimeOffset value)
    {
        if (value == default) return false;
        var localTicks = value.UtcDateTime.Ticks + TimeZoneInfo.Local.GetUtcOffset(value.UtcDateTime).Ticks;
        return localTicks >= DateTime.MinValue.Ticks && localTicks <= DateTime.MaxValue.Ticks;
    }
    private static bool IsHash(string? value) => value != null && Regex.IsMatch(value, "^[A-Fa-f0-9]{64}$");
    private static string ValidateName(string? name)
    { if (name == null || name.Length is <1 or >256) throw new InvalidDataException("素材显示名称为空或超过 256 字符。"); return name; }
    private static string FileTextPreview(string text)
    {
        if (text.Length <= MaxFileTextPreviewChars) return text;
        const string ending = "\n…（文件预览）";
        var length = MaxFileTextPreviewChars - ending.Length;
        if (char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length] + ending;
    }
    private static bool SafePayloadPath(string? path)
    {
        if (path == null || path.Contains('\\')) return false;
        var parts = path.Split('/');
        if (parts.Length != 3 || parts[0] != "files" || !Guid.TryParseExact(parts[1], "N", out var id) || id.ToString("N") != parts[1]) return false;
        var name = parts[2];
        return name.Length is >0 and <=160 && name is not "." and not ".." && !name.EndsWith('.') && !name.EndsWith(' ') &&
            !name.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c)) && !Reserved(name);
    }
    private static bool Reserved(string name) => Regex.IsMatch(name.Split('.')[0].TrimEnd('.', ' '), "^(CON|PRN|AUX|NUL|CONIN\\$|CONOUT\\$|COM[0-9¹²³]|LPT[0-9¹²³])$", RegexOptions.IgnoreCase);
    private static byte[] SerializeManifest(Manifest manifest)
    {
        using var output = new MemoryStream();
        using var limited = new ManifestBuffer(output);
        JsonSerializer.Serialize(limited, manifest, Json);
        return output.ToArray();
    }
    private sealed class ManifestBuffer : Stream
    {
        private readonly Stream _output;
        public ManifestBuffer(Stream output) => _output = output;
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => _output.Length;
        public override long Position { get => _output.Position; set => throw new NotSupportedException(); }
        public override void Flush() => _output.Flush();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > MaxManifestBytes - _output.Length) throw new InvalidDataException("项目包清单超过 16 MiB，请减少选择或文字内容。");
            _output.Write(buffer);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
    private static string SafeExportName(string name)
    {
        var clean = new string(name.Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.', ' ');
        if (clean.Length == 0 || clean is "." or "..") clean = "material";
        if (Reserved(clean)) clean = "_" + clean;
        if (clean.Length > 160) { var length = 160; if (char.IsHighSurrogate(clean[length - 1])) length--; clean = clean[..length].TrimEnd('.', ' '); }
        return clean;
    }
    private static byte[] ReadBounded(Stream stream, int limit, uint? expectedCrc = null)
    {
        using var output = new MemoryStream(); var buffer = new byte[81920]; int count; uint crc = uint.MaxValue;
        while ((count = stream.Read(buffer, 0, Math.Min(buffer.Length, limit + 1 - (int)output.Length))) > 0)
        { output.Write(buffer, 0, count); if (expectedCrc != null) crc = UpdateCrc(crc, buffer, count); if (output.Length > limit) throw new InvalidDataException("清单实际解压内容超过读取预算。"); }
        if (expectedCrc != null && ~crc != expectedCrc.Value) throw new InvalidDataException("ZIP 清单长度或 CRC 校验不匹配。");
        return output.ToArray();
    }
    private static async Task<string> CopyHashAsync(Stream input, Stream? output, long expected, Action<long>? report, CancellationToken ct, uint? expectedCrc = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[BufferSize]; long total = 0; int count; uint crc = uint.MaxValue;
        while ((count = await input.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (count > expected - total) throw new InvalidDataException("文件实际解压内容超出清单长度。");
            hash.AppendData(buffer, 0, count);
            if (expectedCrc != null) crc = UpdateCrc(crc, buffer, count);
            if (output != null) await output.WriteAsync(buffer.AsMemory(0, count), ct);
            total += count; report?.Invoke(total);
        }
        if (total != expected) throw new InvalidDataException("文件未完整读取。");
        if (expectedCrc != null && ~crc != expectedCrc.Value) throw new InvalidDataException("ZIP 原件长度或 CRC 校验不匹配。");
        ct.ThrowIfCancellationRequested(); return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static async Task VerifyHashAsync(Stream input, long size, string expectedHash, Action<long>? progress, CancellationToken ct, uint? expectedCrc = null)
    { if (!(await CopyHashAsync(input, null, size, progress, ct, expectedCrc)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("项目包文件 SHA-256 不匹配。"); }
    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++) { var value = i; for (int bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xEDB88320U ^ (value >> 1) : value >> 1; table[i] = value; }
        return table;
    }
    private static uint UpdateCrc(uint crc, byte[] bytes, int length)
    { for (int i = 0; i < length; i++) crc = CrcTable[(crc ^ bytes[i]) & 255] ^ (crc >> 8); return crc; }
    private static async Task VerifyFileAsync(string path, long size, string hash, CancellationToken ct)
    {
        EnsureNoLinks(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, true);
        if (input.Length != size) throw new IOException("已准备原文件长度发生变化，原文件已保留，请重新核对。");
        await VerifyHashAsync(input, size, hash, null, ct);
    }
    private static Binding? ReadBinding(string path)
    {
        EnsureNoLinks(path); if (!File.Exists(path)) return null;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var binding = JsonSerializer.Deserialize<Binding>(ReadBounded(input, 65536), Json);
        if (binding == null || binding.Format != Format || !IsGuid(binding.PackageId) || !IsHash(binding.Fingerprint)) throw new IOException("项目绑定记录不可读取，已保留原文件。");
        return binding;
    }
    private static void WriteBinding(string path, Binding binding)
    { EnsureNoLinks(path); DurableJson.WriteText(path, JsonSerializer.Serialize(binding, Json)); }
    private static string SafeRoot(string root)
    {
        var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Path.IsPathFullyQualified(full) || full.StartsWith("\\\\", StringComparison.Ordinal) ||
            string.Equals(full, Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new IOException("项目合入仅支持本地非根目录的素材库。");
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("目标素材库目录不存在。");
        EnsureNoLinks(full); return full;
    }
    private static string Contained(string root, string relative)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(prefix, relative));
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("项目文件路径越界。");
        return path;
    }
    private static void EnsureDirectory(string path)
    { EnsureNoLinks(path); Directory.CreateDirectory(path); EnsureNoLinks(path); }
    private static void EnsureNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("项目路径包含目录链接，请使用普通本地目录。"); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    private static void DeleteOwnedPartial(string path)
    {
        try { EnsureNoLinks(path); if (File.Exists(path)) File.Delete(path); }
        catch { /* Never broaden cleanup or remove unknown files when a partial is locked/replaced. */ }
    }
    private sealed class Notifier
    {
        private readonly IProgress<ProjectPackageProgress>? _progress; private long _last; private string _stage = "";
        public Notifier(IProgress<ProjectPackageProgress>? progress) => _progress = progress;
        public void Send(string stage, int done, int count, long bytes, long total, string name, bool force = false)
        {
            if (_progress == null || !force && stage == _stage && Environment.TickCount64 - _last < 125) return;
            _last = Environment.TickCount64; _stage = stage; _progress.Report(new(stage, done, count, bytes, total, name));
        }
    }
}
