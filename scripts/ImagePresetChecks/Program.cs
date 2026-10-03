using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Liuzhuan.Models;
using Liuzhuan.Services;

internal static class Program
{
    private static int _checks;
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private static void Check(bool value, string description)
    {
        if (!value) throw new Exception(description);
        _checks++; Console.WriteLine("PASS " + description);
    }
    private static bool Reject(Action action)
    {
        try { action(); return false; }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or JsonException or IOException or UnauthorizedAccessException or InvalidOperationException) { return true; }
    }
    static void Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "liuzhuan-image-preset-check-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var directory = Path.Combine(root, "library"); var path = Path.Combine(directory, ImageProcessingPresetStore.FileName);
            var store = new ImageProcessingPresetStore(directory);
            Check(store.Presets.Count == 0 && store.LoadError == null && !Directory.Exists(directory), "missing preset file loads empty without creating files or directories");
            store.Add(ImageProcessingPresetStore.Create("  交付照片  ", new(ImageOutputFormat.Jpeg, 1920, 82)));
            store.Add(ImageProcessingPresetStore.Create("透明原图", new(ImageOutputFormat.Png, null, 95)));
            var restored = new ImageProcessingPresetStore(directory);
            Check(restored.Presets.Count == 2 && restored.Presets[0].Name == "交付照片" && restored.Presets[0].Options == new ImageProcessingOptions(ImageOutputFormat.Jpeg, 1920, 82)
                && restored.Presets[1].MaxEdge == null, "real disk round trip preserves named format, nullable original size and JPEG quality");
            Check(!File.ReadAllText(path).Contains("FilePath") && !File.ReadAllText(path).Contains("Destination") && !File.ReadAllText(path).Contains("Source"), "persistent presets contain parameters rather than source files or output locations");
            restored.Update("交付照片", new(ImageOutputFormat.Png, 1280, 88));
            Check(new ImageProcessingPresetStore(directory).Presets[0].Options == new ImageProcessingOptions(ImageOutputFormat.Png, 1280, 88), "explicit update overwrites the selected parameters and persists before reporting success");
            var previousOptions = restored.Presets[0].Options;
            restored.Rename("交付照片", "PNG 交付");
            Check(restored.Presets[0].Name == "PNG 交付" && restored.Presets[0].Options == previousOptions
                && new ImageProcessingPresetStore(directory).Presets[0].Name == "PNG 交付", "rename preserves processing parameters and survives reopening");
            var originalFile = File.ReadAllBytes(path); var originalPresets = restored.Presets.ToArray();
            Check(Reject(() => restored.Add(new() { Name = " PNG 交付 " })) && File.ReadAllBytes(path).SequenceEqual(originalFile)
                && restored.Presets.SequenceEqual(originalPresets), "duplicate normalized names cannot overwrite disk or memory");
            Check(Reject(() => restored.Rename("PNG 交付", "透明原图")) && File.ReadAllBytes(path).SequenceEqual(originalFile), "duplicate rename leaves all existing presets intact");
            Check(new[] { "", "   ", "a\nb", " \na ", new string('名', 41) }.All(name => Reject(() => ImageProcessingPresetStore.Normalize(new() { Name = name }))), "empty, control and oversized preset names are rejected without truncation");
            Check(Reject(() => ImageProcessingPresetStore.Normalize(new() { Name = "bad", Format = (ImageOutputFormat)44 }))
                && new[] { -1, 0, 32769 }.All(edge => Reject(() => ImageProcessingPresetStore.Normalize(new() { Name = "bad", MaxEdge = edge })))
                && new[] { 0, 101 }.All(quality => Reject(() => ImageProcessingPresetStore.Normalize(new() { Name = "bad", JpegQuality = quality }))), "invalid format, longest edge and quality are rejected by shared pure validation");
            Check(ImageProcessingPresetStore.Normalize(new() { Name = "minimum", MaxEdge = 1, JpegQuality = 1 }).MaxEdge == 1
                && ImageProcessingPresetStore.Normalize(new() { Name = "maximum", MaxEdge = 32768, JpegQuality = 100 }).JpegQuality == 100,
                "valid parameter endpoints remain usable");
            restored.Remove("透明原图");
            Check(restored.Presets.Count == 1 && new ImageProcessingPresetStore(directory).Presets.Count == 1, "deleting a preset persists the remaining parameter sets");
            Check(Reject(() => restored.Update("missing", new())), "missing update targets are not silently recreated");
            var exported = restored.ExportDocument(); var serialized = JsonSerializer.Serialize(exported, Json);
            var portable = ImageProcessingPresetStore.NormalizeDocument(JsonSerializer.Deserialize<ImageProcessingPresetDocument>(serialized, Json));
            var second = new ImageProcessingPresetStore(Path.Combine(root, "new-pc")); second.ImportDocument(portable);
            Check(second.Presets.SequenceEqual(restored.Presets) && new ImageProcessingPresetStore(Path.Combine(root, "new-pc")).Presets.SequenceEqual(restored.Presets), "migration DTO serialization and real import preserve all named processing parameters");
            exported.Presets.Clear();
            Check(restored.Presets.Count == 1, "exported DTO list cannot mutate the source store");
            var importedBytes = File.ReadAllBytes(Path.Combine(root, "new-pc", ImageProcessingPresetStore.FileName));
            Check(Reject(() => second.ImportDocument(new() { Version = 99 })) && second.Presets.Count == 1
                && File.ReadAllBytes(Path.Combine(root, "new-pc", ImageProcessingPresetStore.FileName)).SequenceEqual(importedBytes), "unsupported migration versions fail without changing an existing store");
            Check(Reject(() => ImageProcessingPresetStore.NormalizeDocument(null)) && Reject(() => ImageProcessingPresetStore.NormalizeDocument(new() { Presets = null! }))
                && Reject(() => ImageProcessingPresetStore.NormalizeDocument(new() { Presets = new() { new() { Name = "Same" }, new() { Name = "same" } } })), "null payloads, missing collections and duplicate migration names are rejected");
            Check(Reject(() => JsonSerializer.Deserialize<ImageProcessingPresetDocument>("{}", Json))
                && Reject(() => JsonSerializer.Deserialize<ImageProcessingPresetDocument>("{\"Version\":1,\"Presets\":[{\"Name\":\"incomplete\",\"Format\":\"Jpeg\",\"JpegQuality\":85}]}", Json)),
                "JSON DTO missing mandatory version or parameter fields cannot silently use defaults");
            var limited = new ImageProcessingPresetStore(Path.Combine(root, "limited"));
            for (int index = 0; index < ImageProcessingPresetStore.MaxPresets; index++) limited.Add(new() { Name = "preset-" + index });
            var limitedPath = Path.Combine(root, "limited", ImageProcessingPresetStore.FileName); var limitedBytes = File.ReadAllBytes(limitedPath);
            Check(Reject(() => limited.Add(new() { Name = "too-many" })) && limited.Presets.Count == 20 && File.ReadAllBytes(limitedPath).SequenceEqual(limitedBytes), "preset count limit is enforced before altering existing data");
            limited.Update("preset-0", new(ImageOutputFormat.Jpeg, 1280, 80)); limited.Remove("preset-1"); limited.Add(new() { Name = "replacement" });
            Check(limited.Presets.Count == 20 && limited.Presets[0].JpegQuality == 80, "a full store still permits updates and reuse of a deleted slot");
            Check(Reject(() => ImageProcessingPresetStore.NormalizeDocument(new() { Presets = Enumerable.Range(0, 21).Select(index => new ImageProcessingPreset { Name = "migrate-" + index }).ToList() })), "oversized migration collections obey the same count limit");
            var beforeLock = restored.Presets.ToArray(); var lockedBytes = File.ReadAllBytes(path);
            using (var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                Check(Reject(() => restored.Update("PNG 交付", new(ImageOutputFormat.Jpeg, 640, 60))) && restored.Presets.SequenceEqual(beforeLock)
                    && File.ReadAllBytes(path).SequenceEqual(lockedBytes), "failed atomic replacement never changes in-memory values or the old disk file");
            Check(!Directory.GetFiles(directory, "*.tmp").Any(), "failed saves remove only their own temporary files");
            var brokenDirectory = Path.Combine(root, "broken"); Directory.CreateDirectory(brokenDirectory);
            var brokenPath = Path.Combine(brokenDirectory, ImageProcessingPresetStore.FileName); var brokenText = "{ this is deliberately corrupt";
            File.WriteAllText(brokenPath, brokenText); var broken = new ImageProcessingPresetStore(brokenDirectory);
            Check(broken.LoadError != null && Reject(() => broken.Add(new() { Name = "must not overwrite" }))
                && Reject(() => broken.ExportDocument()) && File.ReadAllText(brokenPath) == brokenText,
                "damaged presets are visible and block silent overwrite or empty migration export");
            var hash = SHA256.HashData(File.ReadAllBytes(brokenPath)); var backup = broken.RebuildAfterLoadError();
            Check(File.Exists(backup) && SHA256.HashData(File.ReadAllBytes(backup)).SequenceEqual(hash) && broken.LoadError == null
                && broken.Presets.Count == 0 && new ImageProcessingPresetStore(brokenDirectory).LoadError == null,
                "explicit rebuild first hash-verifies a preserved copy before creating a fresh empty file");
            var unsupported = Path.Combine(root, "unsupported"); Directory.CreateDirectory(unsupported);
            File.WriteAllText(Path.Combine(unsupported, ImageProcessingPresetStore.FileName), JsonSerializer.Serialize(new ImageProcessingPresetDocument { Version = 2 }, Json));
            Check(new ImageProcessingPresetStore(unsupported).LoadError != null, "unknown disk versions do not load as empty success");
            var oversized = Path.Combine(root, "oversized"); Directory.CreateDirectory(oversized);
            File.WriteAllText(Path.Combine(oversized, ImageProcessingPresetStore.FileName), new string('x', 128 * 1024 + 1));
            Check(new ImageProcessingPresetStore(oversized).LoadError != null, "preset file reads have a bounded size");
            var notDirectory = Path.Combine(root, "not-directory"); File.WriteAllText(notDirectory, "retain this file");
            var blocked = new ImageProcessingPresetStore(notDirectory);
            Check(Reject(() => blocked.Add(new() { Name = "blocked" })) && blocked.Presets.Count == 0 && File.ReadAllText(notDirectory) == "retain this file",
                "unwritable storage does not turn failed saves into in-memory success");
            ConfigService.DirectoryPath = Path.Combine(root, "default-fixture"); var defaults = new ImageProcessingPresetStore(); defaults.Add(new() { Name = "defaults" });
            Check(File.Exists(Path.Combine(ConfigService.DirectoryPath, ImageProcessingPresetStore.FileName)), "default constructor uses the current configured library directory");
            Console.WriteLine($"{_checks} pure preset checks passed; no App, image codec or real user data was loaded.");
        }
        finally
        {
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(root).StartsWith("liuzhuan-image-preset-check-", StringComparison.Ordinal)) Directory.Delete(root, true);
        }
    }
}
