using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Liuzhuan;
using Liuzhuan.Models;
using Liuzhuan.Services;

internal static class Program
{
    private static int _checks;
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static void Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "liuzhuan-organization-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Liuzhuan.Utils.Logger.Init(root);

        void Scenario(string name, Action<DataStore, string> test)
        {
            var dataDir = Path.Combine(root, name);
            Directory.CreateDirectory(dataDir);
            // Linked TestApp is only a path contract; no real app startup or existing library is touched.
            typeof(App).GetProperty(nameof(App.DataDir))!.SetValue(null, dataDir);
            using var store = new DataStore();
            test(store, dataDir);
        }

        var legacy = JsonSerializer.Deserialize<MaterialItem>("{\"Id\":\"legacy\",\"Type\":\"Text\",\"TextContent\":\"hello\"}", Json)!;
        Check(legacy.GroupName == "" && legacy.Tags.Length == 0 && legacy.Note == "", "old JSON missing organization fields loads empty defaults");
        var nulls = JsonSerializer.Deserialize<MaterialItem>("{\"Id\":\"nulls\",\"Type\":\"Text\",\"GroupName\":null,\"Tags\":null,\"Note\":null}", Json)!;
        Check(nulls.GroupName == "" && nulls.Tags.Length == 0 && nulls.Note == "" && !nulls.HasMetadata, "explicit null organization fields load safely");
        var values = JsonSerializer.Deserialize<MaterialItem>("{\"GroupName\":\"  Design  \",\"Tags\":[\" A \",\"a\",null,\"\",\"B\"],\"Note\":\"line 1\\nline 2\"}", Json)!;
        Check(values.GroupName == "Design" && values.Tags.SequenceEqual(new[] { "A", "B" }), "JSON tags trim, ignore empty values and deduplicate without case sensitivity");
        Check(values.Note == "line 1\nline 2", "notes retain their line breaks");

        var notifications = new List<string?>();
        nulls.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        nulls.GroupName = "Group";
        Check(notifications.Contains(nameof(MaterialItem.GroupName)) && notifications.Contains(nameof(MaterialItem.HasMetadata)) && notifications.Contains(nameof(MaterialItem.MetadataLabel)) && notifications.Contains(nameof(MaterialItem.MetadataSummary)), "organization edits notify card metadata bindings");
        notifications.Clear();
        nulls.GroupName = " Group ";
        Check(notifications.Count == 0, "equivalent normalized organization assignment sends no redundant notification");
        nulls.Tags = new[] { "first" };
        var returnedTags = nulls.Tags;
        returnedTags[0] = "changed outside model";
        Check(nulls.Tags[0] == "first", "tag array cannot be changed through its getter");
        nulls.Note = "memo";
        Check(nulls.HasMetadata && nulls.MetadataLabel.Contains("Group") && nulls.MetadataLabel.Contains("#first") && nulls.MetadataSummary.Contains("memo"), "metadata label and tooltip describe group, tags and note");
        var encoded = JsonSerializer.Serialize(nulls, Json);
        Check(!encoded.Contains("MetadataLabel") && !encoded.Contains("MetadataSummary") && !encoded.Contains("HasMetadata"), "derived card metadata is excluded from persisted JSON");

        var full = MaterialOrganization.Normalize(null, null, null);
        Check(full.GroupName == "" && full.Tags!.Length == 0 && full.Note == "", "full normalization converts null metadata into empty values");
        var emptyPatch = MaterialOrganization.Normalize(new MaterialOrganizationPatch());
        Check(emptyPatch.GroupName == null && emptyPatch.Tags == null && emptyPatch.Note == null, "patch normalization preserves omitted fields");
        var normalized = MaterialOrganization.Normalize(new MaterialOrganizationPatch { GroupName = " Work ", Tags = new[] { "Red", " red ", "", "blue" }, Note = "  keep formatting  " });
        Check(normalized.GroupName == "Work" && normalized.Tags!.SequenceEqual(new[] { "Red", "blue" }) && normalized.Note == "  keep formatting  ", "normalization trims groups and tags while retaining note formatting");
        Rejects(() => MaterialOrganization.Normalize(new MaterialOrganizationPatch { GroupName = new string('g', 49) }), "group names exceeding 48 characters are rejected");
        foreach (var control in new[] { '\r', '\n', '\t', '\0' })
            Rejects(() => MaterialOrganization.Normalize(new MaterialOrganizationPatch { GroupName = "first" + control + "second" }), "group names reject internal control " + (int)control);
        foreach (var separator in new[] { ',', '，', ';', '；', '\r', '\n', '\t', '\0' })
            Rejects(() => MaterialOrganization.Normalize(new MaterialOrganizationPatch { Tags = new[] { "first" + separator + "second" } }), "single tags reject editor separators or controls " + (int)separator);
        Rejects(() => MaterialOrganization.Normalize(new MaterialOrganizationPatch { Tags = new[] { new string('t', 33) } }), "tags exceeding 32 characters are rejected");
        Rejects(() => MaterialOrganization.Normalize(new MaterialOrganizationPatch { Tags = Enumerable.Range(0, 21).Select(i => "tag" + i).ToArray() }), "more than 20 unique tags are rejected");
        Rejects(() => MaterialOrganization.Normalize(new MaterialOrganizationPatch { Note = new string('n', 4001) }), "notes exceeding 4000 characters are rejected");
        Rejects(() => MaterialOrganization.Normalize(new MaterialOrganizationPatch { TagsMode = (TagsMode)99 }), "unknown tag update modes are rejected");
        var boundary = MaterialOrganization.Normalize(new string('g', 48), Enumerable.Range(0, 20).Select(i => i.ToString().PadRight(32, 't')), new string('n', 4000));
        Check(boundary.GroupName!.Length == 48 && boundary.Tags!.Length == 20 && boundary.Note!.Length == 4000, "maximum metadata lengths are accepted without truncation");
        Check(MaterialOrganization.Normalize("", Enumerable.Repeat("tag", 30), "").Tags!.Length == 1, "tag capacity counts unique nonempty labels");

        Scenario("single-and-batch", (store, _) =>
        {
            var a = Text("a", "alpha");
            var b = Text("b", "beta");
            store.Add(a);
            store.Add(b);
            var order = store.Items.Select(x => x.Id).ToArray();
            var timestamps = store.Items.Select(x => x.AddedTime).ToArray();
            var sequence = store.CurrentSequence;
            var events = 0;
            store.ItemAdded += _ => events++;
            store.ItemRemoved += _ => events++;
            store.ItemCleared += () => events++;
            Check(store.UpdateOrganization(new[] { "a" }, new() { GroupName = "Work", Tags = new[] { "One", "Two" }, Note = "alpha memo" }) == 1, "single edit returns the actual updated item count");
            Check(a.GroupName == "Work" && a.Tags.SequenceEqual(new[] { "One", "Two" }) && a.Note == "alpha memo" && !b.HasMetadata, "single edit leaves unselected materials untouched");
            Check(store.UpdateOrganization(new[] { "a", "b", "b", "missing" }, new() { GroupName = "Shared" }) == 2, "batch edits ignore repeated and missing IDs");
            Check(a.Tags.SequenceEqual(new[] { "One", "Two" }) && a.Note == "alpha memo" && b.Tags.Length == 0, "omitted batch fields preserve each item's previous tags and notes");
            Check(store.UpdateOrganization(new[] { "a", "b" }, new() { GroupName = "Shared" }) == 0, "no-op edits return zero");
            Check(store.UpdateOrganization(new[] { "missing" }, new() { Note = "ignored" }) == 0, "missing selections create no new material");
            Check(store.UpdateOrganization(new[] { "a", "b" }, new()) == 0, "empty patch never clears organization fields");
            Check(events == 0 && store.CurrentSequence == sequence, "local organization edits emit no LAN events and consume no sequence");
            Check(store.Items.Select(x => x.Id).SequenceEqual(order) && store.Items.Select(x => x.AddedTime).SequenceEqual(timestamps), "organization edits preserve library order and timestamps");
            store.UpdateOrganization(new[] { "a" }, new() { Tags = new[] { "two", "Three" }, TagsMode = TagsMode.Add });
            Check(a.Tags.SequenceEqual(new[] { "One", "Two", "Three" }), "adding tags keeps previous labels and deduplicates case-insensitively");
            store.UpdateOrganization(new[] { "a" }, new() { Tags = new[] { "ONE", "missing" }, TagsMode = TagsMode.Remove });
            Check(a.Tags.SequenceEqual(new[] { "Two", "Three" }), "removing tags compares case-insensitively and preserves remaining order");
            store.UpdateOrganization(new[] { "a" }, new() { Tags = Array.Empty<string>(), TagsMode = TagsMode.Add });
            Check(a.Tags.SequenceEqual(new[] { "Two", "Three" }), "adding an empty tag list preserves existing tags");
            store.UpdateOrganization(new[] { "a" }, new() { Tags = Array.Empty<string>(), TagsMode = TagsMode.Remove });
            Check(a.Tags.SequenceEqual(new[] { "Two", "Three" }), "removing an empty tag list preserves existing tags");
            store.UpdateOrganization(new[] { "a" }, new() { GroupName = "", Tags = Array.Empty<string>(), Note = "" });
            Check(!a.HasMetadata && b.GroupName == "Shared", "explicit empty values clear only the selected item's metadata");
        });

        Scenario("transaction", (store, _) =>
        {
            var a = Text("a", "alpha");
            var b = Text("b", "beta");
            b.Tags = Enumerable.Range(0, 20).Select(i => "tag" + i).ToArray();
            store.Add(b);
            store.Add(a); // The item that could accept the patch is evaluated first.
            var sequence = store.CurrentSequence;
            Rejects(() => store.UpdateOrganization(new[] { "a", "b" }, new() { GroupName = "WouldChange", Tags = new[] { "new" }, TagsMode = TagsMode.Add }), "tag capacity rejection validates the entire batch first");
            Check(a.GroupName == "" && a.Tags.Length == 0 && b.GroupName == "" && b.Tags.Length == 20 && store.CurrentSequence == sequence, "an invalid merged tag batch changes no material or sequence");
            Rejects(() => store.UpdateOrganization(new[] { "a", "b" }, new() { GroupName = "Valid", Note = new string('n', 4001) }), "invalid notes reject all requested field changes");
            Check(a.GroupName == "" && b.GroupName == "" && a.Note == "" && b.Note == "", "invalid patch cannot partially update earlier fields");
            Check(store.UpdateOrganization(new[] { "b" }, new() { Tags = new[] { "TAG0", "tag1" }, TagsMode = TagsMode.Add }) == 0, "adding existing labels at maximum capacity remains a no-op");
            Check(store.UpdateOrganization(new[] { "b" }, new() { Tags = new[] { "TAG0" }, TagsMode = TagsMode.Remove }) == 1 && b.Tags.Length == 19, "removal from a full tag list remains valid");
        });

        Scenario("search", (store, _) =>
        {
            var a = Text("a", "bodytoken");
            a.DisplayName = "NameToken";
            var b = Text("b", "different body");
            b.IsFavorite = true;
            var c = Text("c", "unassigned");
            var file = new MaterialItem { Id = "file", Type = MaterialType.Other, DisplayName = "asset", FilePath = Path.Combine(root, "PathToken.bin") };
            store.Add(a); store.Add(b); store.Add(c); store.Add(file);
            store.UpdateOrganization(new[] { "a" }, new() { GroupName = "BrandAssets", Tags = new[] { "Campaign", "Red" }, Note = "ApprovalToken\nWaiting" });
            store.UpdateOrganization(new[] { "b" }, new() { GroupName = "brandassets", Tags = new[] { "Campaign" }, Note = "different" });
            Check(store.Search("brand", "All").Count() == 2, "search matches group names without case sensitivity");
            Check(store.Search("PAIGN", "All").Count() == 2, "search matches partial tag labels");
            Check(store.Search("approvaltoken", "All").Single().Id == "a", "search includes notes");
            Check(store.Search("bodytoken", "Text").Single().Id == "a" && store.Search("nametoken", "All").Single().Id == "a" && store.Search("pathtoken", "All").Single().Id == "file", "organization search preserves body, display-name and source-path matching");
            Check(store.GetFiltered("All", groupName: " BRANDASSETS ").Count() == 2, "group filtering matches normalized exact names without case sensitivity");
            Check(store.GetFiltered("All", groupName: "").Select(x => x.Id).ToHashSet().SetEquals(new[] { "c", "file" }), "empty group filter selects unassigned materials");
            Check(store.GetFiltered("All", tag: " campaign ").Count() == 2 && !store.GetFiltered("All", tag: "Camp").Any(), "tag filtering uses exact normalized labels");
            Check(store.GetFiltered("Favorites", "brandassets", "CAMPAIGN").Single().Id == "b", "group and tag filters compose with favorites");
            Check(store.Search("Campaign", "All", "brandassets", "Red").Single().Id == "a", "search composes with group and tag filters");
            Check(store.Search(" ", "All", "", null).Count() == 2, "empty search keeps organization filters active");
        });

        Scenario("retain-and-persist", (store, _) =>
        {
            var original = Text("original", "shared body");
            original.IsFavorite = true;
            store.Add(original);
            store.UpdateOrganization(new[] { original.Id }, new() { GroupName = "SavedGroup", Tags = new[] { "SavedTag" }, Note = "SavedNote" });
            var duplicated = store.Add(Text("duplicate", "shared body"));
            Check(ReferenceEquals(duplicated, original) && original.GroupName == "SavedGroup" && original.Tags.Single() == "SavedTag" && original.Note == "SavedNote", "duplicate text re-addition retains the existing item's organization metadata");
            var path = Path.Combine(root, "dedupe.bin");
            File.WriteAllText(path, "source bytes");
            var firstFile = new MaterialItem { Id = "original-file", Type = MaterialType.Other, FilePath = path, GroupName = "FileGroup", Tags = new[] { "FileTag" }, Note = "FileNote" };
            store.Add(firstFile);
            var readdedFile = store.Add(new MaterialItem { Type = MaterialType.Other, FilePath = path, GroupName = "NewGroup", Tags = new[] { "NewTag" }, Note = "NewNote" });
            Check(ReferenceEquals(firstFile, readdedFile) && firstFile.GroupName == "FileGroup" && firstFile.Tags.Single() == "FileTag" && firstFile.Note == "FileNote", "duplicate file re-addition does not overwrite previous organization information");
            store.RemoveMultiple(new() { original.Id, firstFile.Id });
            Check(store.Items.Count == 0 && File.ReadAllText(path) == "source bytes", "organization-aware deletion keeps source file bytes intact");
            store.Undo();
            Check(store.Items.Contains(original) && store.Items.Contains(firstFile) && original.IsFavorite && original.Note == "SavedNote" && firstFile.Tags.Single() == "FileTag", "undo restores the original objects with all organization metadata");
        });
        Scenario("retain-and-persist", (store, dataDir) =>
        {
            var text = store.Items.Single(x => x.Id == "original");
            var file = store.Items.Single(x => x.Id == "original-file");
            Check(text.GroupName == "SavedGroup" && text.Tags.Single() == "SavedTag" && text.Note == "SavedNote" && text.IsFavorite, "organization fields persist and reload alongside existing metadata");
            Check(file.GroupName == "FileGroup" && file.Note == "FileNote" && File.ReadAllText(file.FilePath) == "source bytes", "organization fields survive file-index persistence without altering originals");
            var persisted = File.ReadAllText(Path.Combine(dataDir, "data.json"));
            Check(persisted.Contains("GroupName") && persisted.Contains("Tags") && persisted.Contains("Note") && !persisted.Contains("MetadataSummary"), "data.json stores organization fields but excludes UI-derived summaries");
        });

        var oldDir = Path.Combine(root, "old-library");
        Directory.CreateDirectory(oldDir);
        File.WriteAllText(Path.Combine(oldDir, "data.json"), "[{\"Id\":\"old\",\"Type\":\"Text\",\"TextContent\":\"old\"},{\"Id\":\"null\",\"Type\":\"Text\",\"TextContent\":\"null\",\"GroupName\":null,\"Tags\":null,\"Note\":null}]");
        Scenario("old-library", (store, _) =>
        {
            Check(store.Items.Count == 2 && store.Items.All(x => !x.HasMetadata && x.GroupName == "" && x.Tags.Length == 0 && x.Note == ""), "DataStore loads both legacy and explicit-null organization records");
            Check(store.Search("old", "All").Single().Id == "old", "old libraries remain searchable after upgrade");
        });

        Console.WriteLine($"PASS {_checks} organization checks; isolated artifacts: {root}");
    }

    private static MaterialItem Text(string id, string content) => new() { Id = id, Type = MaterialType.Text, DisplayName = id, TextContent = content };

    private static void Rejects(Action action, string message)
    {
        var rejected = false;
        try { action(); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
        Console.WriteLine("PASS " + message);
    }
}
