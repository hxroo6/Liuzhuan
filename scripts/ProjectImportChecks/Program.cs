using System.Collections.Specialized;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Liuzhuan;
using Liuzhuan.Models;
using Liuzhuan.Services;

// Production source links, isolated synthetic files and a directory-only App contract.
// Never start the real App, dispatcher, server, clipboard or material library.
internal static class Program
{
    private static int checks;
    private static string root = "";
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private sealed record Change(string Id, long Sequence, int VisibleCount);

    private static void Main()
    {
        root = Path.Combine(Path.GetTempPath(), "liuzhuan-project-import-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Liuzhuan.Utils.Logger.Init(root);

        Scenario("metadata-and-events", (store, directory) =>
        {
            var old = Seed(store, "old-first", "old-second");
            var oldJson = Snapshot(store);
            store.Automation.Save(new AutomationDocument { Rules = new() { new CollectionRule { Name = "must not run on import", Group = "automatic", Tags = new[] { "automatic-tag" } } } });
            var package = Guid.NewGuid();
            var text = ImportedText(package, Guid.NewGuid(), "project text");
            text.DisplayName = "项目说明"; text.IsFavorite = true; text.GroupName = "导入分组";
            text.Tags = new[] { "封面", "待确认" }; text.Note = "第一行\n第二行";
            var file = ImportedFile(directory, package, Guid.NewGuid(), "same-name.bin", "original source bytes");
            file.IsFavorite = true; file.GroupName = "图片参考"; file.Tags = new[] { "文件" }; file.Note = "file note";
            var baseline = new MaterialIntegrityBaseline(new string('A', 64), file.Size, DateTimeOffset.Parse("2026-10-01T12:00:00Z"), file.FilePath, file.Type);
            file.IntegrityBaseline = baseline;
            var incoming = new[] { text, file };
            var changes = new List<Change>();
            var resets = new List<NotifyCollectionChangedAction>();
            store.ItemAdded += item => changes.Add(new(item.Id, store.CurrentSequence, store.Items.Count));
            store.Items.CollectionChanged += (_, args) => resets.Add(args.Action);
            var sequence = store.CurrentSequence;
            var preview = store.PreviewProjectBatch(incoming, sequence, directory);
            Check(preview.Added == 2 && preview.AlreadyImported == 0 && preview.DuplicateText == 0, "preview counts new text and file materials");
            Check(Snapshot(store) == oldJson && store.CurrentSequence == sequence && changes.Count == 0 && resets.Count == 0, "preview leaves library, metadata, collection and LAN sequence unchanged");
            var result = store.ImportProjectBatch(incoming, sequence, directory);
            Check(result.Added == 2 && result.AlreadyImported == 0 && result.DuplicateText == 0, "commit agrees with the validated preview");
            Check(store.Items.Where(x => old.Contains(x)).SequenceEqual(old), "project merge retains existing object identity and relative order");
            Check(old.All(x => x.GroupName == "existing-group" && x.Tags.SequenceEqual(new[] { "existing-tag" }) && x.Note == "existing-note"), "project merge preserves original-library organization");
            Check(text.IsFavorite && text.GroupName == "导入分组" && text.Tags.SequenceEqual(new[] { "封面", "待确认" }) && text.Note == "第一行\n第二行", "import preserves favorite, group, tags and multiline notes without applying collection rules");
            Check(file.IsFavorite && file.GroupName == "图片参考" && file.Tags.SequenceEqual(new[] { "文件" }) && file.Note == "file note" && file.IntegrityBaseline == null, "file organization survives merge while foreign derived integrity state is reset");
            Check(resets.SequenceEqual(new[] { NotifyCollectionChangedAction.Reset }), "batch insertion emits one collection reset instead of one reset per material");
            Check(changes.Count == 2 && changes.Select(x => x.Sequence).SequenceEqual(new[] { sequence + 1, sequence + 2 }) && changes.All(x => x.VisibleCount == 4), "each new material emits a continuous LAN sequence after the complete collection is visible");
            Check(incoming.All(x => x.AddedTime == FixtureTime) && old.All(x => x.AddedTime == FixtureTime.AddDays(1)), "merge preserves source timestamps and existing timestamps");
            Flush(store);
            using var reloaded = new DataStore();
            Check(reloaded.Items.Count == 4 && reloaded.Items.Single(x => x.Id == text.Id).Note == text.Note && reloaded.Items.Single(x => x.Id == file.Id).IntegrityBaseline == null, "durable flush and production reload retain imported organization without foreign derived integrity state");
            Check(File.ReadAllText(reloaded.Items.Single(x => x.Id == file.Id).FilePath) == "original source bytes", "durable index points to the staged original file bytes");
        });

        Scenario("idempotent-package", (store, directory) =>
        {
            var package = Guid.NewGuid(); var entry = Guid.NewGuid();
            var first = ImportedText(package, entry, "first imported text");
            store.ImportProjectBatch(new[] { first }, store.CurrentSequence, directory);
            Flush(store);
            var sequence = store.CurrentSequence; var saved = Snapshot(store);
            var retry = ImportedText(package, entry, "ignored changed retry content");
            retry.RegistrationKeys = new[] { $"project:{package:D}:{entry:D}" };
            var preview = store.PreviewProjectBatch(new[] { retry }, sequence, directory);
            var result = store.ImportProjectBatch(new[] { retry }, sequence, directory);
            Check(preview.AlreadyImported == 1 && result.AlreadyImported == 1 && result.Added == 0 && result.DuplicateText == 0, "same package entry is recognized across N and D UUID spellings");
            Check(Snapshot(store) == saved && store.CurrentSequence == sequence && store.Items.Single().RegistrationKeys.Single() == Key(package, entry), "reimport changes neither original content nor metadata nor sequence");
            Flush(store);
            using var reloaded = new DataStore();
            var again = reloaded.ImportProjectBatch(new[] { ImportedText(package, entry, "another retry") }, reloaded.CurrentSequence, directory);
            Check(again.AlreadyImported == 1 && reloaded.Items.Count == 1, "package-entry idempotency survives process-equivalent reload");
        });

        Scenario("duplicate-inline-text", (store, directory) =>
        {
            var existing = Seed(store, "keep-first", "same-text", "keep-last");
            var target = existing[1]; target.IsFavorite = true;
            var order = store.Items.Select(x => x.Id).ToArray(); var timestamp = target.AddedTime;
            var package = Guid.NewGuid(); var entry = Guid.NewGuid();
            var incoming = ImportedText(package, entry, target.TextContent);
            incoming.IsFavorite = false; incoming.GroupName = "must not replace"; incoming.Tags = new[] { "foreign" }; incoming.Note = "foreign note";
            var preview = store.PreviewProjectBatch(new[] { incoming }, store.CurrentSequence, directory);
            Check(preview.DuplicateText == 1 && target.RegistrationKeys.Length == 0, "duplicate-text preview reports reuse without appending an internal key");
            var result = store.ImportProjectBatch(new[] { incoming }, store.CurrentSequence, directory);
            Check(result.DuplicateText == 1 && result.Added == 0 && result.AlreadyImported == 0 && store.Items.Count == 3, "matching inline text reuses the existing material");
            Check(store.Items.Select(x => x.Id).SequenceEqual(order) && ReferenceEquals(store.Items[1], target) && target.AddedTime == timestamp, "duplicate text does not move, replace or retimestamp the existing material");
            Check(target.IsFavorite && target.GroupName == "existing-group" && target.Tags.SequenceEqual(new[] { "existing-tag" }) && target.Note == "existing-note", "duplicate text preserves local favorite and organization");
            Check(target.RegistrationKeys.SequenceEqual(new[] { Key(package, entry) }), "duplicate text appends only the canonical project retry key");
            var sameBatch = new[] { ImportedText(Guid.NewGuid(), Guid.NewGuid(), "new same text"), ImportedText(Guid.NewGuid(), Guid.NewGuid(), "new same text") };
            var intra = store.ImportProjectBatch(sameBatch, store.CurrentSequence, directory);
            Check(intra.Added == 1 && intra.DuplicateText == 1 && store.Items.Count == 4 && store.Items.Single(x => x.TextContent == "new same text").RegistrationKeys.Length == 2, "same-text entries within one package batch share one material and retain both retry identities");
            Flush(store);
            using var reloaded = new DataStore();
            var retry = reloaded.ImportProjectBatch(new[] { ImportedText(package, entry, target.TextContent) }, reloaded.CurrentSequence, directory);
            Check(retry.AlreadyImported == 1 && reloaded.Items.Single(x => x.Id == target.Id).Note == "existing-note", "duplicate-text retry mapping is durable without replacing local metadata");
        });

        Scenario("file-policies", (store, directory) =>
        {
            var file1 = ImportedFile(directory, Guid.NewGuid(), Guid.NewGuid(), "identical.bin", "same bytes");
            var file2 = ImportedFile(directory, Guid.NewGuid(), Guid.NewGuid(), "identical.bin", "same bytes");
            var result = store.ImportProjectBatch(new[] { file1, file2 }, store.CurrentSequence, directory);
            Check(result.Added == 2 && store.Items.Count == 2 && file1.FilePath != file2.FilePath, "same-name equal-byte files from separate packages are not automatically merged by hash");
            var fileBackedText = ImportedFile(directory, Guid.NewGuid(), Guid.NewGuid(), "full-text.txt", new string('文', 5000));
            fileBackedText.Type = MaterialType.Text; fileBackedText.TextContent = "short preview";
            var inline = ImportedText(Guid.NewGuid(), Guid.NewGuid(), "short preview");
            var mixed = store.ImportProjectBatch(new[] { fileBackedText, inline }, store.CurrentSequence, directory);
            Check(mixed.Added == 2 && mixed.DuplicateText == 0 && File.ReadAllText(fileBackedText.FilePath).Length == 5000, "file-backed text retains its complete source and does not deduplicate against inline preview text");
        });

        Scenario("atomic-validation", (store, directory) =>
        {
            Seed(store, "untouched");
            void Reject(string name, Action<MaterialItem[]> invalidate, long? sequence = null, string? expectedRoot = null)
            {
                var batch = new[] { ImportedText(Guid.NewGuid(), Guid.NewGuid(), "valid prefix " + name), ImportedText(Guid.NewGuid(), Guid.NewGuid(), "invalid suffix " + name) };
                invalidate(batch);
                var before = Snapshot(store); var originalSequence = store.CurrentSequence;
                var events = 0; var resets = 0;
                Action<MaterialItem> handler = _ => events++;
                NotifyCollectionChangedEventHandler collection = (_, _) => resets++;
                store.ItemAdded += handler; store.Items.CollectionChanged += collection;
                try
                {
                    var previewRejected = ThrowsExpected(() => store.PreviewProjectBatch(batch, sequence ?? originalSequence, expectedRoot ?? directory));
                    Check(previewRejected && Snapshot(store) == before && store.CurrentSequence == originalSequence && events == 0 && resets == 0, name + ": preview rejects without modification");
                    var rejected = ThrowsExpected(() => store.ImportProjectBatch(batch, sequence ?? originalSequence, expectedRoot ?? directory));
                    Check(rejected && Snapshot(store) == before && store.CurrentSequence == originalSequence && events == 0 && resets == 0, name + ": full validation rejects before modifying the valid prefix");
                }
                finally { store.ItemAdded -= handler; store.Items.CollectionChanged -= collection; }
            }
            Reject("overlong group", b => b[1].GroupName = new string('组', 49));
            Reject("overlong note", b => b[1].Note = new string('注', 4001));
            Reject("overlong tag", b => b[1].Tags = new[] { new string('签', 33) });
            Reject("unknown type", b => b[1].Type = (MaterialType)999);
            Reject("empty material ID", b => b[1].Id = "");
            Reject("overlong display name", b => b[1].DisplayName = new string('名', 257));
            Reject("overlong inline text", b => b[1].TextContent = new string('文', 262145));
            Reject("negative size", b => b[1].Size = -1);
            Reject("missing key", b => b[1].RegistrationKeys = Array.Empty<string>());
            Reject("malformed key", b => b[1].RegistrationKeys = new[] { "project:not-a-guid:still-not-a-guid" });
            Reject("extra receipt key", b => b[1].RegistrationKeys = b[1].RegistrationKeys.Concat(new[] { "unrelated-receipt" }).ToArray());
            Reject("duplicate canonical key", b => b[1].RegistrationKeys = b[0].RegistrationKeys.ToArray());
            Reject("duplicate material ID", b => b[1].Id = b[0].Id);
            Reject("existing material instance", b => b[1] = store.Items[0]);
            Reject("existing ID collision", b => b[1].Id = store.Items[0].Id);
            Reject("outside file root", b => { b[1].Type = MaterialType.Other; b[1].FilePath = Path.Combine(directory, "not-project-managed.bin"); File.WriteAllText(b[1].FilePath, "fixture"); b[1].Size = 7; });
            Reject("sibling prefix file root", b => { b[1].Type = MaterialType.Other; b[1].FilePath = Path.Combine(directory, "project-imports-escape", "file.bin"); Directory.CreateDirectory(Path.GetDirectoryName(b[1].FilePath)!); File.WriteAllText(b[1].FilePath, "fixture"); b[1].Size = 7; });
            Reject("stale sequence", _ => { }, store.CurrentSequence - 1);
            Reject("wrong library root", _ => { }, expectedRoot: Path.Combine(directory, "other-library"));
        });

        Scenario("failed-flush-retry", (store, directory) =>
        {
            Seed(store, "old-preserved"); Flush(store);
            var index = Path.Combine(directory, "data.json"); var previous = File.ReadAllBytes(index);
            var package = Guid.NewGuid(); var entry = Guid.NewGuid();
            var incoming = ImportedFile(directory, package, entry, "retry.bin", "keep on failed commit");
            // A held read handle prevents atomic replacement on Windows. No sleeps or IO retries.
            using (var blocked = new FileStream(index, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var result = store.ImportProjectBatch(new[] { incoming }, store.CurrentSequence, directory);
                Check(result.Added == 1 && store.Items.Count == 2, "in-memory merge is separate from its durable success barrier");
                var refused = ThrowsExpected(() => Flush(store));
                Check(refused && File.ReadAllBytes(index).SequenceEqual(previous), "Flush failure surfaces and retains the previous durable index");
                Check(File.Exists(incoming.FilePath) && store.Items.Any(x => x.Id == incoming.Id), "failed persistence retains staged payloads referenced by the in-memory library");
                var retry = store.ImportProjectBatch(new[] { ImportedText(package, entry, "ignored retry") }, store.CurrentSequence, directory);
                Check(retry.AlreadyImported == 1 && store.Items.Count == 2, "retry after uncertain save recognizes the existing in-memory project entry");
            }
            var later = new MaterialItem { Id = Guid.NewGuid().ToString("N"), Type = MaterialType.Text, DisplayName = "later", TextContent = "new unrelated material", AddedTime = FixtureTime.AddDays(2) };
            store.Add(later); Flush(store);
            using var reloaded = new DataStore();
            Check(reloaded.Items.Count == 3 && reloaded.Items.Any(x => x.Id == incoming.Id) && reloaded.Items.Any(x => x.Id == later.Id), "retry Flush persists both the project batch and unrelated later collection");
            var resultAfterRestart = reloaded.ImportProjectBatch(new[] { ImportedText(package, entry, "ignored retry") }, reloaded.CurrentSequence, directory);
            Check(resultAfterRestart.AlreadyImported == 1 && reloaded.Items.Count == 3 && File.ReadAllText(incoming.FilePath) == "keep on failed commit", "successful retry survives reload without duplicate items or loss of original payload");
        });

        var damaged = Path.Combine(root, "damaged-index"); Directory.CreateDirectory(damaged);
        var damagedIndex = Path.Combine(damaged, "data.json"); File.WriteAllText(damagedIndex, "[broken retained index");
        SetRoot(damaged);
        using (var store = new DataStore())
        {
            var incoming = new[] { ImportedText(Guid.NewGuid(), Guid.NewGuid(), "must not overwrite a damaged index") };
            Check(store.LoadFailed && ThrowsExpected(() => store.ImportProjectBatch(incoming, store.CurrentSequence, damaged)) && store.Items.Count == 0 && File.ReadAllText(damagedIndex) == "[broken retained index", "unreadable production index refuses merge and preserves the original damaged file");
        }
        Console.WriteLine($"PASS {checks} project-import checks; isolated artifacts: {root}");
    }

    private static readonly DateTime FixtureTime = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Local);
    private static string Key(Guid package, Guid entry) => $"project:{package:N}:{entry:N}";
    private static MaterialItem ImportedText(Guid package, Guid entry, string text) => new() { Id = Guid.NewGuid().ToString("N"), Type = MaterialType.Text, DisplayName = "fixture text", TextContent = text, AddedTime = FixtureTime, RegistrationKeys = new[] { Key(package, entry) } };
    private static MaterialItem ImportedFile(string directory, Guid package, Guid entry, string name, string bytes)
    {
        var path = Path.Combine(directory, "project-imports", package.ToString("N"), "files", Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, bytes);
        return new() { Id = Guid.NewGuid().ToString("N"), Type = MaterialType.Other, FilePath = path, DisplayName = name, Size = new FileInfo(path).Length, AddedTime = FixtureTime, RegistrationKeys = new[] { Key(package, entry) } };
    }
    private static MaterialItem[] Seed(DataStore store, params string[] names)
    {
        var items = names.Select(name => new MaterialItem { Id = Guid.NewGuid().ToString("N"), Type = MaterialType.Text, DisplayName = name, TextContent = name, AddedTime = FixtureTime.AddDays(1), GroupName = "existing-group", Tags = new[] { "existing-tag" }, Note = "existing-note" }).ToArray();
        foreach (var item in items.Reverse()) store.Add(item);
        return items;
    }
    private static void Scenario(string name, Action<DataStore, string> test)
    {
        var directory = Path.Combine(root, name); Directory.CreateDirectory(directory); SetRoot(directory);
        using var store = new DataStore();
        try { test(store, directory); }
        finally
        {
            // Drain scheduled saves before moving the App directory contract to the next fixture.
            var timer = (Timer?)typeof(DataStore).GetField("_saveTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store);
            if (timer != null) timer.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Flush(store);
        }
    }
    private static void SetRoot(string directory) => typeof(App).GetProperty(nameof(App.DataDir))!.SetValue(null, directory);
    private static void Flush(DataStore store) => store.FlushAsync().GetAwaiter().GetResult();
    private static string Snapshot(DataStore store) => JsonSerializer.Serialize(store.Items, Json);
    private static bool ThrowsExpected(Action action)
    {
        try { action(); return false; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException) { return true; }
    }
    private static void Check(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
        checks++; Console.WriteLine("PASS " + message);
    }
}
