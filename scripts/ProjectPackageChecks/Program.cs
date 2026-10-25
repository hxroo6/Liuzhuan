using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Liuzhuan.Models;
using Liuzhuan.Services;

internal static class Program
{
    private static int _checks;
    private sealed class Immediate<T> : IProgress<T>
    { private readonly Action<T> _action; public Immediate(Action<T> action) => _action = action; public void Report(T value) => _action(value); }
    private static void Check(bool value, string description)
    { if (!value) throw new Exception(description); Console.WriteLine("PASS " + description); _checks++; }
    private static async Task Reject(Func<Task> work, string description)
    {
        try { await work(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or JsonException or UnauthorizedAccessException)
        { Check(true, description); return; }
        throw new Exception("not rejected: " + description);
    }
    private static Dictionary<string, byte[]> Members(string path)
    {
        using var archive = ZipFile.OpenRead(path); var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries) { using var input = entry.Open(); using var output = new MemoryStream(); input.CopyTo(output); result.Add(entry.FullName, output.ToArray()); }
        return result;
    }
    private static void WriteArchive(string path, IEnumerable<KeyValuePair<string, byte[]>> members)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var pair in members) { var entry = archive.CreateEntry(pair.Key); using var output = entry.Open(); output.Write(pair.Value); }
    }
    private static void ClaimUncompressedLength(string package, string memberName, uint claimed)
    {
        var bytes = File.ReadAllBytes(package); bool found = false;
        for (int i = 0; i + 46 < bytes.Length; i++)
        {
            if (bytes[i] != 0x50 || bytes[i + 1] != 0x4b || bytes[i + 2] != 0x01 || bytes[i + 3] != 0x02) continue;
            var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i + 28, 2));
            if (i + 46 + length > bytes.Length || Encoding.UTF8.GetString(bytes, i + 46, length) != memberName) continue;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i + 24, 4), claimed); found = true;
        }
        if (!found) throw new Exception("synthetic ZIP central member unavailable");
        File.WriteAllBytes(package, bytes);
    }
    private static async Task Main(string[] args)
    {
        var output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "liuzhuan-project-package-checks");
        var root = Path.Combine(output, "fixture-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string FileAt(string name, string value) { var path = Path.Combine(root, name); File.WriteAllText(path, value); return path; }
        string NewLibrary(string name) { var path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
        var binaryPath = FileAt("same-file.bin", "full binary original"); var originalTextPath = FileAt("file-note.txt", "the full file-backed text 🐈\nsecond line");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        var at = new DateTime(2026, 10, 1, 12, 30, 0, DateTimeKind.Utc);
        var plain = new MaterialItem { Type = MaterialType.Text, DisplayName = "项目文字", TextContent = "完整正文 🍎\n第二行", IsFavorite = true,
            GroupName = "项目甲", Tags = new[] { "设计", "待用" }, Note = "保留备注", AddedTime = at,
            RegistrationKeys = new[] { "SENSITIVE-REGISTRATION-KEY" }, CollectionSource = "SENSITIVE-ORIGIN", CollectionDevice = "SENSITIVE-DEVICE" };
        var original = new MaterialItem { Type = MaterialType.Other, FilePath = binaryPath, DisplayName = "改名原文件", IsFavorite = true, GroupName = "文件组", Tags = new[] { "原件" }, Note = "文件备注", AddedTime = at,
            IntegrityBaseline = new(new string('A', 64), 1, DateTimeOffset.UtcNow, binaryPath, MaterialType.Other) };
        var fileText = new MaterialItem { Type = MaterialType.Text, FilePath = originalTextPath, DisplayName = "文件型文字", TextContent = "TRUNCATED-PREVIEW", AddedTime = at };
        var alias = new MaterialItem { Type = MaterialType.Image, FilePath = Path.Combine(root, "sub", "..", "same-file.bin"), DisplayName = "相同来源另一个索引", AddedTime = at };
        var snapshot = ProjectPackageService.Snapshot(new[] { plain, original, fileText, alias, plain });
        Check(snapshot.Entries.Count == 4 && Guid.TryParse(snapshot.PackageId, out _), "selection snapshot deduplicates IDs and creates stable package identity");
        var package = Path.Combine(root, "project.zip"); var preview = await ProjectPackageService.ExportAsync(package, snapshot);
        Check(preview.ItemCount == 4 && preview.FileCount == 2 && preview.PayloadBytes == new FileInfo(binaryPath).Length + new FileInfo(originalTextPath).Length, "mixed package stores each normalized source path once");
        var members = Members(package); var manifestText = Encoding.UTF8.GetString(members["manifest.json"]);
        Check(!manifestText.Contains(root, StringComparison.OrdinalIgnoreCase) && !manifestText.Contains("SENSITIVE-") && !manifestText.Contains("integrityBaseline") && !manifestText.Contains("registrationKeys"), "public whitelist excludes source paths credentials registration keys baselines and devices");
        Check(members.Count == 3 && members.Keys.Where(x => x != "manifest.json").All(x => x.StartsWith("files/")), "archive has only manifest and declared payloads");
        var library = NewLibrary("library"); var existingIndex = Path.Combine(library, "data.json"); File.WriteAllText(existingIndex, "synthetic preexisting index");
        var prepared = await ProjectPackageService.ReadPreviewAsync(package, library);
        Check(File.ReadAllText(existingIndex) == "synthetic preexisting index", "preview and staging never change target material index");
        Check(prepared.DataRoot == Path.GetFullPath(library) && prepared.Preview.Fingerprint == preview.Fingerprint && prepared.Rows.Count == 4, "fully verified preview binds target root and original manifest fingerprint");
        var materials = prepared.CreateMaterials(); var restoredPlain = materials.Single(x => x.DisplayName == plain.DisplayName);
        Check(restoredPlain.TextContent == plain.TextContent && restoredPlain.GroupName == plain.GroupName && restoredPlain.Tags.SequenceEqual(plain.Tags) && restoredPlain.Note == plain.Note && restoredPlain.IsFavorite, "plain text and organization roundtrip without truncation");
        Check(new DateTimeOffset(restoredPlain.AddedTime).ToUniversalTime() == new DateTimeOffset(at), "material timestamp roundtrips across local display representation");
        Check(materials.All(x => x.IntegrityBaseline == null && x.MetadataRevision == 0 && x.RegistrationKeys.Length == 1 && x.RegistrationKeys[0].StartsWith("project:")), "new material objects carry only local project retry keys and no foreign state");
        Check(materials.Single(x => x.DisplayName == fileText.DisplayName).TextContent == fileText.TextContent && File.ReadAllText(materials.Single(x => x.DisplayName == fileText.DisplayName).FilePath) == File.ReadAllText(originalTextPath), "file-backed text keeps a bounded existing preview and also imports the full original file");
        Check(prepared.Rows.Single(x => x.Name == alias.DisplayName).FilePath == prepared.Rows.Single(x => x.Name == original.DisplayName).FilePath, "source aliases share one owned prepared file");
        Check(prepared.CreateMaterials()[0].Id != materials[0].Id && !ReferenceEquals(prepared.CreateMaterials()[0], materials[0]), "retry creates fresh material objects while registration keys stay stable");
        await ProjectPackageService.ValidatePreparedAsync(prepared); Check(true, "prepared payloads and completed binding revalidate before commit");
        var beforeTimes = prepared.Rows.Where(x => x.FilePath.Length > 0).Select(x => x.FilePath).Distinct().ToDictionary(x => x, File.GetLastWriteTimeUtc);
        var retried = await ProjectPackageService.PrepareImportAsync(package, library, expectedPreview: prepared.Preview);
        Check(retried.DirectoryPath == prepared.DirectoryPath && retried.Rows.Select(x => x.RegistrationKey).SequenceEqual(prepared.Rows.Select(x => x.RegistrationKey)) && beforeTimes.All(x => File.GetLastWriteTimeUtc(x.Key) == x.Value), "same package retry verifies and reuses original stage without rewriting files");
        Check(((IList<ProjectPackageImportRow>)prepared.Rows).IsReadOnly && ((IList<ProjectPackageEntryPreview>)prepared.Preview.Entries).IsReadOnly && ((IList<string>)prepared.Rows[0].Tags).IsReadOnly, "prepared rows preview and metadata collections are immutable to callers");
        var originalMembers = Members(package);
        string Variant(string name, Action<JsonObject, Dictionary<string, byte[]>> edit)
        {
            var dict = originalMembers.ToDictionary(x => x.Key, x => x.Value.ToArray(), StringComparer.Ordinal);
            var json = JsonNode.Parse(dict["manifest.json"])!.AsObject(); var before = JsonSerializer.Serialize(json); edit(json, dict);
            if (JsonSerializer.Serialize(json) != before) dict["manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(json);
            var path = Path.Combine(root, name + ".zip"); WriteArchive(path, dict); return path;
        }
        var changedPackage = Variant("changed-metadata", (json, _) => json["entries"]![0]!["note"] = "different");
        await Reject(() => ProjectPackageService.PrepareImportAsync(changedPackage, library, expectedPreview: preview), "changed archive after preview is rejected before any replacement");
        await Reject(() => ProjectPackageService.PrepareImportAsync(changedPackage, library), "same package UUID with different manifest cannot overwrite owned project stage");
        var corruptPayload = Variant("corrupt-payload", (json, dict) => { var path = json["files"]![0]!["path"]!.GetValue<string>(); dict[path][0] ^= 1; });
        await Reject(() => ProjectPackageService.PrepareImportAsync(corruptPayload, library), "even existing good staging cannot hide corruption in a retried archive");
        var badFreshLibrary = NewLibrary("bad-fresh");
        await Reject(() => ProjectPackageService.PrepareImportAsync(corruptPayload, badFreshLibrary), "bad payload hash never returns a committable prepared object");
        var badBinding = JsonNode.Parse(File.ReadAllText(Path.Combine(badFreshLibrary, "project-imports", preview.PackageId, "binding.json")))!;
        Check(!badBinding["complete"]!.GetValue<bool>() && !File.Exists(Path.Combine(badFreshLibrary, "data.json")), "failed half-package is marked incomplete and does not create any index");
        Check(!Directory.GetFiles(badFreshLibrary, "*.partial", SearchOption.AllDirectories).Any(), "owned incomplete output files are individually cleaned without recursive directory deletion");
        var stagedPath = prepared.Rows.First(x => x.FilePath.Length > 0).FilePath; var stagedBytes = File.ReadAllBytes(stagedPath);
        var altered = stagedBytes.ToArray(); altered[0] ^= 1; File.WriteAllBytes(stagedPath, altered);
        await Reject(() => ProjectPackageService.ValidatePreparedAsync(prepared), "same-size stage tampering is caught by current hash rather than mtime");
        await Reject(() => ProjectPackageService.PrepareImportAsync(package, library), "retry refuses to overwrite corrupted preexisting stage");
        Check(File.ReadAllBytes(stagedPath).SequenceEqual(altered), "failed retry preserves external modified stage as recovery evidence");
        File.WriteAllBytes(stagedPath, stagedBytes);
        await ProjectPackageService.ValidatePreparedAsync(prepared);
        var missingMember = Variant("missing-member", (json, dict) => dict.Remove(json["files"]![0]!["path"]!.GetValue<string>()));
        await Reject(() => ProjectPackageService.PrepareImportAsync(missingMember, NewLibrary("missing-member")), "declared but absent payload rejected before staging");
        var extraMember = Variant("extra-member", (_, dict) => dict.Add("files/" + Guid.NewGuid().ToString("N") + "/extra.bin", new byte[] { 1 }));
        await Reject(() => ProjectPackageService.PrepareImportAsync(extraMember, NewLibrary("extra-member")), "undeclared archive payload rejected");
        var duplicateEntry = Variant("duplicate-entry", (json, _) => json["entries"]![1]!["entryId"] = json["entries"]![0]!["entryId"]!.GetValue<string>());
        await Reject(() => ProjectPackageService.PrepareImportAsync(duplicateEntry, NewLibrary("duplicate-entry")), "duplicate entry UUIDs rejected");
        var unsupported = Variant("unsupported", (json, _) => json["version"] = 2);
        await Reject(() => ProjectPackageService.PrepareImportAsync(unsupported, NewLibrary("unsupported")), "unknown package version cannot be silently imported");
        var invalidOrganization = Variant("invalid-group", (json, _) => json["entries"]![0]!["groupName"] = new string('x', 49));
        await Reject(() => ProjectPackageService.PrepareImportAsync(invalidOrganization, NewLibrary("invalid-group")), "invalid organization is rejected instead of truncated");
        var invalidName = Variant("invalid-name", (json, _) => json["entries"]![0]!["name"] = new string('x', 257));
        await Reject(() => ProjectPackageService.PrepareImportAsync(invalidName, NewLibrary("invalid-name")), "import name length matches DataStore's 256-character budget");
        var longInline = Variant("long-inline", (json, _) => json["entries"]![0]!["textContent"] = new string('x', ProjectPackageService.MaxInlineTextChars + 1));
        await Reject(() => ProjectPackageService.PrepareImportAsync(longInline, NewLibrary("long-inline")), "over-limit inline body is rejected instead of truncated");
        var defaultAdded = Variant("default-added", (json, _) => json["entries"]![0]!["addedTime"] = "0001-01-01T00:00:00+00:00");
        await Reject(() => ProjectPackageService.PrepareImportAsync(defaultAdded, NewLibrary("default-added")), "default source timestamp is rejected before any prepared rows exist");
        await Reject(() => Task.FromResult(ProjectPackageService.Snapshot(new[] { new MaterialItem { Type = MaterialType.Text, DisplayName = new string('x', 257), TextContent = "body" } })), "export snapshot also rejects names outside the shared budget");
        await Reject(() => Task.FromResult(ProjectPackageService.Snapshot(new[] { new MaterialItem { Type = MaterialType.Text, DisplayName = "too long", TextContent = new string('x', ProjectPackageService.MaxInlineTextChars + 1) } })), "export snapshot rejects oversized complete inline text without truncation");
        await Reject(() => Task.FromResult(ProjectPackageService.Snapshot(new[] { new MaterialItem { Type = MaterialType.Text, DisplayName = "no timestamp", TextContent = "body", AddedTime = default } })), "export snapshot rejects an invalid default material timestamp");
        var oversizedPayload = Variant("oversized-payload", (json, _) => json["files"]![0]!["size"] = ProjectPackageService.MaxPayloadBytes + 1);
        await Reject(() => ProjectPackageService.PrepareImportAsync(oversizedPayload, NewLibrary("oversized-payload")), "64GiB payload budget enforced before extraction");
        var wrongSize = Variant("wrong-size", (json, _) => json["files"]![0]!["size"] = 0);
        await Reject(() => ProjectPackageService.PrepareImportAsync(wrongSize, NewLibrary("wrong-size")), "archive uncompressed length must match manifest");
        string falsifiedMember = "";
        var actualOverLength = Variant("actual-over-length", (json, _) =>
        { falsifiedMember = json["files"]![0]!["path"]!.GetValue<string>(); json["files"]![0]!["size"] = 0; json["files"]![0]!["sha256"] = Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())); });
        ClaimUncompressedLength(actualOverLength, falsifiedMember, 0);
        await Reject(() => ProjectPackageService.PrepareImportAsync(actualOverLength, NewLibrary("actual-over-length")), "actual inflated payload bytes are bounded even when central directory falsely claims zero bytes");
        var unused = Variant("unused-payload", (json, _) => { foreach (var entry in json["entries"]!.AsArray().Where(x => x!["payload"]!.GetValue<string>() == json["files"]![0]!["path"]!.GetValue<string>()).ToArray()) { entry!["payload"] = ""; entry["type"] = "Text"; entry["textContent"] = "replacement"; } });
        await Reject(() => ProjectPackageService.PrepareImportAsync(unused, NewLibrary("unused-payload")), "unused declared payloads cannot smuggle unreferenced files");
        foreach (var badPath in new[] { "../outside.bin", "F:/outside.bin", "files\\outside.bin", "files/" + Guid.NewGuid().ToString("N") + "/CON.txt", "files/" + Guid.NewGuid().ToString("N") + "/trailing. " })
        {
            var n = _checks; var variant = Variant("unsafe-" + n, (json, dict) =>
            {
                var old = json["files"]![0]!["path"]!.GetValue<string>(); var bytes = dict[old]; dict.Remove(old); dict.Add(badPath, bytes); json["files"]![0]!["path"] = badPath;
                foreach (var entry in json["entries"]!.AsArray().Where(x => x!["payload"]!.GetValue<string>() == old)) entry!["payload"] = badPath;
            });
            await Reject(() => ProjectPackageService.PrepareImportAsync(variant, NewLibrary("unsafe-" + n)), "unsafe ZIP payload path rejected: " + badPath);
        }
        var caseMembers = originalMembers.ToList(); var payloadName = caseMembers.First(x => x.Key != "manifest.json").Key;
        caseMembers.Add(new(payloadName.ToUpperInvariant(), new byte[] { 1 })); var duplicateCase = Path.Combine(root, "duplicate-case.zip"); WriteArchive(duplicateCase, caseMembers);
        await Reject(() => ProjectPackageService.PrepareImportAsync(duplicateCase, NewLibrary("duplicate-case")), "case-insensitive duplicate ZIP names rejected");
        var metadataTooLarge = Path.Combine(root, "metadata-too-large.zip");
        WriteArchive(metadataTooLarge, new[] { new KeyValuePair<string, byte[]>("manifest.json", Encoding.UTF8.GetBytes(new string(' ', ProjectPackageService.MaxManifestBytes + 1))) });
        await Reject(() => ProjectPackageService.PrepareImportAsync(metadataTooLarge, NewLibrary("metadata-too-large")), "16MiB manifest budget enforced for compressed metadata");
        var falseMetadataLength = Path.Combine(root, "metadata-false-length.zip"); File.Copy(metadataTooLarge, falseMetadataLength);
        ClaimUncompressedLength(falseMetadataLength, "manifest.json", 1);
        await Reject(() => ProjectPackageService.PrepareImportAsync(falseMetadataLength, NewLibrary("metadata-false-length")), "actual decompressed manifest length is bounded independently of ZIP claimed length");
        var overCount = Enumerable.Range(0, ProjectPackageService.MaxItems + 1).Select(i => new MaterialItem { Id = "fixture-" + i, Type = MaterialType.Text, DisplayName = "test", TextContent = "test" });
        await Reject(() => Task.FromResult(ProjectPackageService.Snapshot(overCount)), "selection over 1000 entries rejected without opening sources");
        var repeatedBody = new string('x', ProjectPackageService.MaxInlineTextChars);
        var overManifest = ProjectPackageService.Snapshot(Enumerable.Range(0, 70).Select(i => new MaterialItem { Id = "large-" + i, Type = MaterialType.Text, DisplayName = "bounded text", TextContent = repeatedBody }));
        var overManifestTarget = Path.Combine(root, "over-manifest.zip");
        await Reject(() => ProjectPackageService.ExportAsync(overManifestTarget, overManifest), "aggregate export manifest exceeds 16MiB through a bounded writer before final output creation");
        Check(!File.Exists(overManifestTarget), "over-budget export does not publish a completed archive");
        var missingOriginal = new MaterialItem { Type = MaterialType.Other, FilePath = Path.Combine(root, "missing-original.bin"), DisplayName = "missing" };
        var missingTarget = Path.Combine(root, "should-not-exist.zip");
        await Reject(() => ProjectPackageService.ExportAsync(missingTarget, ProjectPackageService.Snapshot(new[] { missingOriginal })), "missing source aborts export without creating a completed package");
        Check(!File.Exists(missingTarget), "failed export publishes no final ZIP");
        var sentinel = FileAt("keep-existing.zip", "synthetic existing destination");
        using (var cancel = new CancellationTokenSource())
        {
            bool canceled = false;
            try { await ProjectPackageService.ExportAsync(sentinel, snapshot, new Immediate<ProjectPackageProgress>(p => { if (p.Stage == "正在打包原文件") cancel.Cancel(); }), cancel.Token); }
            catch (OperationCanceledException) { canceled = true; }
            Check(canceled && File.ReadAllText(sentinel) == "synthetic existing destination", "canceled export leaves existing destination intact");
        }
        var cancelLibrary = NewLibrary("cancel-library");
        using (var cancel = new CancellationTokenSource())
        {
            bool canceled = false;
            try { await ProjectPackageService.PrepareImportAsync(package, cancelLibrary, new Immediate<ProjectPackageProgress>(p => { if (p.Stage == "正在验证并准备原文件") cancel.Cancel(); }), cancel.Token); }
            catch (OperationCanceledException) { canceled = true; }
            Check(canceled && !File.Exists(Path.Combine(cancelLibrary, "data.json")), "canceling preparation never starts an index commit");
        }
        var resumed = await ProjectPackageService.PrepareImportAsync(package, cancelLibrary); Check(resumed.Rows.Count == 4, "canceled preparation retries with the same binding and completes verified staging");
        var plainPackage = Path.Combine(root, "plain-only.zip"); var plainPreview = await ProjectPackageService.ExportAsync(plainPackage, ProjectPackageService.Snapshot(new[] { plain }));
        var plainPrepared = await ProjectPackageService.PrepareImportAsync(plainPackage, NewLibrary("plain-library"));
        Check(plainPreview.FileCount == 0 && plainPrepared.Rows.Count == 1 && plainPrepared.Rows[0].FilePath == "", "pure-text-only project is complete without any file payload");
        var emptyPath = FileAt("empty.bin", ""); var emptyPackage = Path.Combine(root, "empty-file.zip");
        await ProjectPackageService.ExportAsync(emptyPackage, ProjectPackageService.Snapshot(new[] { new MaterialItem { Type = MaterialType.Other, FilePath = emptyPath, DisplayName = "empty" } }));
        var emptyPrepared = await ProjectPackageService.PrepareImportAsync(emptyPackage, NewLibrary("empty-library"));
        Check(emptyPrepared.Rows[0].Size == 0 && File.Exists(emptyPrepared.Rows[0].FilePath), "zero-byte original is verified and preserved");
        var boundedPreviewSource = new MaterialItem { Type = MaterialType.Text, FilePath = originalTextPath, DisplayName = "bounded preview", TextContent = new string('x', 6000), AddedTime = at };
        var previewPackage = Path.Combine(root, "bounded-preview.zip");
        await ProjectPackageService.ExportAsync(previewPackage, ProjectPackageService.Snapshot(new[] { boundedPreviewSource }));
        var boundedPreview = await ProjectPackageService.PrepareImportAsync(previewPackage, NewLibrary("bounded-preview-library"));
        Check(boundedPreview.Rows[0].TextContent.Length <= ProjectPackageService.MaxFileTextPreviewChars && boundedPreview.Rows[0].TextContent.EndsWith("（文件预览）") && File.ReadAllText(boundedPreview.Rows[0].FilePath) == File.ReadAllText(originalTextPath), "bounded existing preview is explicitly marked and never substitutes for the full original");
        var unknown = Path.Combine(prepared.DirectoryPath, "external-user-note.txt"); File.WriteAllText(unknown, "keep this unrelated file");
        await Reject(() => ProjectPackageService.PrepareImportAsync(changedPackage, library), "conflicting retry remains rejected after an unknown sibling appears");
        Check(File.ReadAllText(unknown) == "keep this unrelated file", "failure cleanup preserves unrecorded sibling files");
        var linkLibrary = NewLibrary("link-library"); var linkTarget = NewLibrary("link-target"); var link = Path.Combine(linkLibrary, "project-imports");
        var prefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(link).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !Path.GetFullPath(linkTarget).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new Exception("junction fixture escaped root");
        if (link.Contains('%') || linkTarget.Contains('%') || link.Contains('!') || linkTarget.Contains('!')) throw new Exception("junction fixture shell path requires literal non-expanding names");
        var start = new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{linkTarget}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using (var process = Process.Start(start)!) { var stdout = process.StandardOutput.ReadToEnd(); var stderr = process.StandardError.ReadToEnd(); process.WaitForExit(); if (process.ExitCode != 0) throw new Exception("junction fixture creation failed: " + stdout + stderr); }
        try { await Reject(() => ProjectPackageService.PrepareImportAsync(package, linkLibrary), "real directory junction in target path is rejected before extraction"); }
        finally { if ((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0) Directory.Delete(link, false); }
        Check(!Directory.EnumerateFileSystemEntries(linkTarget).Any(), "rejected junction never writes into its target");
        Check(File.ReadAllText(binaryPath) == "full binary original" && File.ReadAllText(existingIndex) == "synthetic preexisting index", "all service operations leave source files and preexisting library index untouched");
        Console.WriteLine($"ProjectPackageChecks passed {_checks} checks. Isolated output: {root}");
    }
}
