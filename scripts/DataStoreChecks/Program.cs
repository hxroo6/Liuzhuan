using System.IO;
using Liuzhuan;
using Liuzhuan.Models;
using Liuzhuan.Services;

internal static class Program
{
    private static int _checks;

    private static void Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "liuzhuan-datastore-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Liuzhuan.Utils.Logger.Init(root);

        void Scenario(string name, Action<DataStore> test)
        {
            var dataDir = Path.Combine(root, name);
            Directory.CreateDirectory(dataDir);
            // Never construct App or invoke OnStartup: every store writes only to this isolated directory.
            typeof(App).GetProperty(nameof(App.DataDir))!.SetValue(null, dataDir);
            using var store = new DataStore();
            test(store);
        }

        Scenario("undo-single", store =>
        {
            var item = Text("one");
            store.Add(item);
            var events = Observe(store);
            store.Remove(item.Id);
            events.Clear();
            var sequence = store.CurrentSequence;
            Check(store.Undo(), "undo consumes the latest deletion");
            Check(events.SequenceEqual(new[] { new Change("add", item.Id, sequence + 1) }), "undo emits one new add event with a fresh sequence");
            Check(ReferenceEquals(store.Items.Single(), item), "undo retains the original material and timestamp");
            Check(!store.CanUndo && !store.Undo(), "empty undo history makes no changes");
        });

        Scenario("undo-batch", store =>
        {
            var originals = Seed(store, "a", "b", "c", "d", "e");
            var events = Observe(store);
            var sequence = store.CurrentSequence;
            // Caller order and duplicate IDs must not change library order or duplicate events.
            store.RemoveMultiple(new() { "d", "b", "b", "unknown" });
            Check(events.SequenceEqual(new[] { new Change("remove", "b", sequence + 1), new Change("remove", "d", sequence + 2) }), "batch deletion emits distinct IDs with continuous sequences");
            Check(Ids(store).SequenceEqual(new[] { "a", "c", "e" }), "batch deletion leaves unrelated materials in order");
            store.Add(Text("new"));
            events.Clear();
            sequence = store.CurrentSequence;
            store.Undo();
            Check(Ids(store).SequenceEqual(new[] { "new", "a", "b", "c", "d", "e" }), "undo restores original positions while retaining newly added materials first");
            Check(events.SequenceEqual(new[] { new Change("add", "d", sequence + 1), new Change("add", "b", sequence + 2) }), "batch undo broadcasts older restorations first with distinct sequences");
            Check(store.Items.Where(x => originals.Contains(x)).All(x => x.AddedTime == originals.First(o => o.Id == x.Id).AddedTime), "batch undo preserves material timestamps");
        });

        Scenario("undo-duplicate", store =>
        {
            var originals = Seed(store, "a", "b", "c");
            store.RemoveMultiple(new() { "b", "c" });
            var replacement = Text("b", "replacement");
            store.Add(replacement);
            var events = Observe(store);
            var sequence = store.CurrentSequence;
            store.Undo();
            Check(store.Items.Count(x => x.Id == "b") == 1 && ReferenceEquals(store.Items.First(x => x.Id == "b"), replacement), "undo never replaces or duplicates an ID re-added since deletion");
            Check(events.SequenceEqual(new[] { new Change("add", "c", sequence + 1) }), "skipped duplicate IDs emit no event and consume no sequence");
            Check(ReferenceEquals(store.Items.First(x => x.Id == "c"), originals[2]), "non-conflicting materials still restore");
        });

        Scenario("clear-type", store =>
        {
            store.Add(Text("other"));
            store.Add(new MaterialItem { Id = "image-favorite", Type = MaterialType.Image, FilePath = "favorite.png", IsFavorite = true });
            store.Add(new MaterialItem { Id = "image-remove", Type = MaterialType.Image, FilePath = "remove.png" });
            var events = Observe(store);
            var sequence = store.CurrentSequence;
            store.ClearByType(MaterialType.Image);
            Check(Ids(store).SequenceEqual(new[] { "image-favorite", "other" }), "category clear preserves favorites and other types");
            Check(events.SequenceEqual(new[] { new Change("remove", "image-remove", sequence + 1) }), "partial category clear broadcasts deletion instead of a global clear");
            events.Clear();
            store.Undo();
            Check(Ids(store).SequenceEqual(new[] { "image-remove", "image-favorite", "other" }) && events.Count == 1 && events[0].Kind == "add", "category clear can be undone and mirrored incrementally");
        });

        Scenario("clear-favorites", store =>
        {
            Seed(store, "a", "b", "c", "d");
            store.SetFavorite("b", true);
            store.SetFavorite("d", true);
            var events = Observe(store);
            var sequence = store.CurrentSequence;
            store.ClearAll();
            Check(Ids(store).SequenceEqual(new[] { "b", "d" }), "clear all preserves favorites on the computer");
            Check(events.SequenceEqual(new[] { new Change("remove", "a", sequence + 1), new Change("remove", "c", sequence + 2) }), "clear all with favorites sends only removed IDs");
            store.Undo();
            Check(Ids(store).SequenceEqual(new[] { "a", "b", "c", "d" }), "undo clear restores favorite and ordinary material order");
        });

        Scenario("clear-empty", store =>
        {
            Seed(store, "a", "b", "c");
            var events = Observe(store);
            var sequence = store.CurrentSequence;
            store.ClearAll();
            Check(store.Items.Count == 0 && events.SequenceEqual(new[] { new Change("clear", "", sequence + 1) }), "true global clear emits one clear event and one sequence");
            store.Add(Text("new"));
            store.Undo();
            Check(Ids(store).SequenceEqual(new[] { "new", "a", "b", "c" }), "undo an emptied library appends older restored materials behind new arrivals");
        });

        Scenario("no-op", store =>
        {
            var favorite = Text("favorite");
            favorite.IsFavorite = true;
            store.Add(favorite);
            var events = Observe(store);
            var sequence = store.CurrentSequence;
            store.Remove("unknown");
            store.RemoveMultiple(new() { "unknown", "unknown" });
            store.ClearByType(MaterialType.Image);
            store.ClearAll();
            Check(events.Count == 0 && store.CurrentSequence == sequence && !store.CanUndo, "no-op deletions and clears preserve sequences and undo history");
        });

        Scenario("batch-favorites", store =>
        {
            var originals=Seed(store,"first","second","third");
            var changed=new List<string>();store.ItemUpdated+=item=>changed.Add(item.Id);
            var sequence=store.CurrentSequence;
            store.SetFavoriteMultiple(new(){"third","first","first","unknown"},true);
            Check(changed.SequenceEqual(new[]{"first","third"})&&store.CurrentSequence==sequence+2,"batch favorites update distinct matches in library order with one event per actual change");
            Check(Ids(store).SequenceEqual(new[]{"first","second","third"})&&originals[0].IsFavorite&&!originals[1].IsFavorite&&originals[2].IsFavorite,"batch favorites preserve order and unrelated items");
            changed.Clear();sequence=store.CurrentSequence;
            store.SetFavoriteMultiple(new(){"third","first","missing"},true);
            Check(changed.Count==0&&store.CurrentSequence==sequence,"unchanged favorite batch emits no metadata revisions or synchronization events");
            store.SetFavoriteMultiple(new(){"first","first"},false);
            Check(changed.SequenceEqual(new[]{"first"})&&!originals[0].IsFavorite&&originals[2].IsFavorite,"batch unfavorite respects duplicates and retains unselected favorites");
        });

        Scenario("bounded-history", store =>
        {
            Seed(store, Enumerable.Range(0, 55).Select(i => "item-" + i).ToArray());
            for (var i = 0; i < 55; i++) store.Remove("item-" + i);
            Check(store.UndoCount == 50, "deletion history remains bounded to 50 steps");
            var allRetainedStepsRestore = true;
            for (var i = 0; i < 50; i++) allRetainedStepsRestore &= store.Undo();
            Check(allRetainedStepsRestore, "all 50 retained deletion steps can be undone");
            Check(!store.Undo() && store.Items.Count == 50, "older discarded deletion steps cannot reappear");
        });

        Scenario("incremental-mirror", store =>
        {
            var mirror = new HashSet<string>(StringComparer.Ordinal);
            var lastSequence = 0L;
            var continuous = true;
            void Next() { continuous &= store.CurrentSequence == lastSequence + 1; lastSequence = store.CurrentSequence; }
            store.ItemAdded += item => { Next(); mirror.Add(item.Id); };
            store.ItemRemoved += id => { Next(); mirror.Remove(id); };
            store.ItemCleared += () => { Next(); mirror.Clear(); };
            store.ItemUpdated += _ => Next();
            var random = new Random(5319);
            var nextId = 0;
            var consistent = true;
            for (var i = 0; i < 150; i++)
            {
                var ids = Ids(store).ToArray();
                switch (random.Next(7))
                {
                    case 0: store.Add(Text("random-" + nextId++)); break;
                    case 1: store.Remove(ids.Length == 0 ? "missing" : ids[random.Next(ids.Length)]); break;
                    case 2: store.RemoveMultiple(ids.Where(_ => random.Next(2) == 0).ToList()); break;
                    case 3: store.ClearByType(MaterialType.Text); break;
                    case 4: store.ClearAll(); break;
                    case 5: store.Undo(); break;
                    case 6: if (ids.Length > 0) store.SetFavorite(ids[random.Next(ids.Length)], random.Next(2) == 0); break;
                }
                consistent &= mirror.SetEquals(Ids(store));
            }
            Check(consistent, "a delta-only remote mirror matches the store after 150 mixed operations");
            Check(continuous && lastSequence == store.CurrentSequence, "mixed operations emit exactly one continuous sequence per actual event");
        });

        Scenario("original-file", store =>
        {
            var path = Path.Combine(root, "original.bin");
            File.WriteAllBytes(path, new byte[] { 0, 1, 2, 127, 255 });
            var item = new MaterialItem { Id = "file", Type = MaterialType.Other, FilePath = path, DisplayName = "original", ThumbnailPath = "thumbnail", IsFavorite = true };
            store.Add(item);
            store.Remove("file");
            Check(File.ReadAllBytes(path).SequenceEqual(new byte[] { 0, 1, 2, 127, 255 }), "deletion preserves original file bytes");
            store.Undo();
            Check(ReferenceEquals(store.Items.Single(), item) && item.IsFavorite && item.ThumbnailPath == "thumbnail", "undo retains favorites, source paths and thumbnail metadata");
        });
        Scenario("original-file", store =>
        {
            Check(store.Items.Count == 1 && store.Items[0].Id == "file" && store.Items[0].IsFavorite && File.Exists(store.Items[0].FilePath), "restored materials persist and reload from the isolated library");
        });

        Scenario("missing-preservation", store =>
        {
            var missing = new MaterialItem
            {
                Id = "missing", Type = MaterialType.Other, FilePath = Path.Combine(root, "moved-original.bin"),
                DisplayName = "important", IsFavorite = true, GroupName = "project", Tags = new[] { "tag" }, Note = "keep memo"
            };
            store.Add(missing);
            store.Add(new MaterialItem { Id = "text-file", Type = MaterialType.Text, FilePath = Path.Combine(root, "moved.txt"), TextContent = "preview" });
            store.Add(Text("inline", "inline-only"));
            Check(missing.IsSourceMissing && missing.SourceStatusLabel.Length > 0, "missing source remains an indexed item with a visible source state");
            Check(store.Items.Single(x => x.Id == "text-file").IsSourceMissing && !store.Items.Single(x => x.Id == "inline").IsSourceMissing, "file-backed text reports missing sources while inline text remains usable");
        });
        Scenario("missing-preservation", store =>
        {
            var missing = store.Items.Single(x => x.Id == "missing");
            Check(store.Items.Count == 3 && missing.IsFavorite && missing.GroupName == "project" && missing.Tags.Single() == "tag" && missing.Note == "keep memo" && missing.IsSourceMissing, "load retains absent sources with favorites and all organization metadata");
            var json = File.ReadAllText(Path.Combine(ConfigService.GetEffectiveDataDir(), "data.json"));
            Check(!json.Contains("IsSourceMissing") && !json.Contains("SourceStatusLabel") && !json.Contains("HasFileSource"), "cached source availability is never persisted to data.json");
            var events = Observe(store);
            var sequence = store.CurrentSequence;
            Check(store.RefreshSourceStatuses() == 2 && store.CurrentSequence == sequence && events.Count == 0, "source-state checking does not alter LAN sequences or remove materials");
            File.WriteAllText(missing.FilePath, "restored bytes");
            var changes = new List<string?>();
            missing.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
            Check(store.RefreshSourceStatuses(new[] { missing.Id }) == 0 && !missing.IsSourceMissing && changes.Contains(nameof(MaterialItem.IsSourceMissing)) && changes.Contains(nameof(MaterialItem.SourceStatusLabel)), "explicit recheck restores cached availability and notifies status bindings");
            File.Delete(missing.FilePath);
            var snapshot = SourceRelinkService.Snapshot(store.Items);
            var results = SourceRelinkService.CheckSourcesAsync(snapshot).GetAwaiter().GetResult();
            Check(!missing.IsSourceMissing, "background checking does not change live models or raise UI notifications");
            Check(store.ApplySourceStatuses(results) == 2 && missing.IsSourceMissing, "UI-thread application accepts matching background path checks");
            missing.FilePath = Path.Combine(root, "new-path.bin");
            Check(store.ApplySourceStatuses(new[] { new SourceRelinkStatus(missing.Id, snapshot.Single(x => x.Id == missing.Id).FilePath, true) }) == 0 && missing.IsSourceMissing, "stale source checks cannot mark a different current path available");
            store.ApplySourceStatuses(new[] { new SourceRelinkStatus(missing.Id, missing.FilePath, true) });
            Check(!missing.IsSourceMissing && !File.Exists(missing.FilePath), "applying a background result uses its cached existence without probing disk again");
            store.Remove(missing.Id);
            Check(store.ApplySourceStatuses(new[] { new SourceRelinkStatus(missing.Id, missing.FilePath, true) }) == 0, "checks for removed materials cannot restore deleted indices");
            store.Undo();
            Check(ReferenceEquals(store.Items.Single(x => x.Id == missing.Id), missing) && missing.Note == "keep memo" && missing.IsFavorite, "missing-source deletion remains undoable without losing metadata");
        });

        Scenario("recollect-current-source", store =>
        {
            var path = Path.Combine(root, "recollect.txt"); File.WriteAllText(path, "updated");
            var original = new MaterialItem { Type=MaterialType.Text, FilePath=path, TextContent="old", IsFavorite=true,
                GroupName="kept", Tags=new[]{"tag"}, Note="memo", ThumbnailPath="old-thumb", Duration=12,
                RegistrationKeys=new[]{"first"} };
            store.Add(original); var revision=original.SourceRevision;
            var retained=store.Add(new MaterialItem {Type=MaterialType.Text,FilePath=path,TextContent="updated",Size=7,RegistrationKeys=new[]{"second"}});
            Check(ReferenceEquals(retained,original) && retained.TextContent=="updated", "ordinary same-path recollection updates the actual indexed text object");
            Check(retained.IsFavorite && retained.GroupName=="kept" && retained.Tags.SequenceEqual(new[]{"tag"}) && retained.Note=="memo", "source refresh preserves all manual organization");
            Check(retained.SourceRevision>revision && retained.ThumbnailPath=="" && retained.Duration==0 && retained.RegistrationKeys.Length==2, "recollection invalidates stale derivatives while retaining durable registration keys");
            Check(store.GetSummary(retained).SourceRevision==retained.SourceRevision, "LAN summaries carry the refreshed source generation");
        });
        Scenario("immutable-save", store =>
        {
            var item=Text("immutable", "captured"); item.RegistrationKeys=new[]{"captured-key"};
            item.IsFavorite=true;item.GroupName="group";item.Tags=new[]{"tag"};item.Note="note";
            store.Add(item); var flush=store.FlushAsync();
            item.TextContent="later";item.RegistrationKeys[0]="later-key";
            flush.GetAwaiter().GetResult();
            var saved=File.ReadAllText(Path.Combine(ConfigService.GetEffectiveDataDir(),"data.json"));
            Check(saved.Contains("captured") && !saved.Contains("later"), "background serialization uses immutable owner-thread fields including copied arrays");
            var tasks=new List<Task>();for(var i=0;i<30;i++){item.TextContent="generation-"+i;tasks.Add(store.FlushAsync());}
            Task.WhenAll(tasks).GetAwaiter().GetResult();
            using var loaded=new DataStore();var copy=loaded.Items.Single();
            Check(copy.TextContent=="generation-29", "all durable waiters complete only after a covering generation is persisted");
            Check(copy.IsFavorite && copy.GroupName=="group" && copy.Tags.SequenceEqual(new[]{"tag"}) && copy.Note=="note" && copy.RegistrationKeys.SequenceEqual(new[]{"later-key"}), "immutable persistence snapshots retain organization and registration schema");
        });
        Scenario("retry-failed-save", store =>
        {
            var target=Path.Combine(ConfigService.GetEffectiveDataDir(),"data.json");Directory.CreateDirectory(target);
            store.Add(Text("retry-save"));var refused=false;
            try{store.FlushAsync().GetAwaiter().GetResult();}catch(Exception ex) when (ex is IOException or UnauthorizedAccessException){refused=true;}
            Check(refused,"a failed durable write faults its waiter instead of acknowledging registration");
            Directory.Delete(target);store.FlushAsync().GetAwaiter().GetResult();
            Check(File.ReadAllText(target).Contains("retry-save"),"the save worker can retry successfully after a storage failure");
        });
        var nullRoot=Path.Combine(root,"null-index");Directory.CreateDirectory(nullRoot);
        var nullFile=Path.Combine(nullRoot,"data.json");File.WriteAllText(nullFile,"null");
        typeof(App).GetProperty(nameof(App.DataDir))!.SetValue(null,nullRoot);
        using(var nullStore=new DataStore()) {
            Check(nullStore.LoadFailed,"a null top-level index is treated as unreadable rather than an empty library");
            var refused=false;try{nullStore.FlushAsync().GetAwaiter().GetResult();}catch(IOException){refused=true;}
            Check(refused && File.ReadAllText(nullFile)=="null","null index is retained and cannot produce a durable acknowledgement");
        }

        var damaged=Path.Combine(root,"damaged-index");Directory.CreateDirectory(damaged);
        var damagedFile=Path.Combine(damaged,"data.json");File.WriteAllText(damagedFile,"[broken original index");
        typeof(App).GetProperty(nameof(App.DataDir))!.SetValue(null,damaged);
        using(var failedStore=new DataStore()) {
            Check(failedStore.LoadFailed,"damaged material index is surfaced as an unavailable store");
            var refused=false;try{failedStore.FlushAsync().GetAwaiter().GetResult();}catch(IOException){refused=true;}
            Check(refused && File.ReadAllText(damagedFile)=="[broken original index","failed index load cannot overwrite the original or acknowledge a durable save");
        }

        Scenario("query-revision", store => {
            var before=store.QueryRevision; var text=Text("query-text"); store.Add(text);
            Check(store.QueryRevision>before,"adding material advances the desktop query revision");
            before=store.QueryRevision; store.SetFavorite(text.Id,true);
            Check(store.QueryRevision>before,"favorite changes invalidate query snapshots");
            before=store.QueryRevision; store.UpdateOrganization(new[]{text.Id},MaterialOrganization.Normalize("project",new[]{"tag"},"note"));
            Check(store.QueryRevision>before,"group, tag and note changes invalidate query snapshots");
            before=store.QueryRevision; store.UpdateOrganization(new[]{text.Id},MaterialOrganization.Normalize("project",new[]{"tag"},"note"));
            Check(store.QueryRevision==before,"idempotent metadata does not invalidate query snapshots");
            var path=Path.Combine(root,"query-source.bin");File.WriteAllText(path,"source");
            var file=new MaterialItem{Id="query-file",Type=MaterialType.Other,FilePath=path,DisplayName="query-file"};store.Add(file);
            before=store.QueryRevision;var sequence=store.CurrentSequence;File.Delete(path);store.RefreshSourceStatuses(new[]{file.Id});
            Check(file.IsSourceMissing&&store.CurrentSequence==sequence+1&&store.QueryRevision==before,"source-status scans retain LAN sequence semantics without rebuilding desktop queries");
            sequence=store.CurrentSequence;store.ApplySourceStatuses(new[]{new SourceRelinkStatus(file.Id,file.FilePath,true)});
            Check(!file.IsSourceMissing&&store.CurrentSequence==sequence+1&&store.QueryRevision==before,"background source-status results do not invalidate search fields");
            file.ThumbnailPath="synthetic-thumbnail";file.Duration=42;file.IntegrityBaseline=new(new string('A',64),1,DateTimeOffset.UtcNow,file.FilePath,file.Type);store.ClearIntegrityBaselines(new[]{file.Id});
            Check(store.QueryRevision==before,"thumbnail, duration and integrity-only changes retain the query revision");
            var candidate=Path.Combine(root,"query-relinked.bin");File.WriteAllText(candidate,"candidate");store.RelinkSource(SourceRelinkService.PrepareSingle(file,candidate));
            Check(store.QueryRevision>before,"confirmed source path changes invalidate path search snapshots");
            before=store.QueryRevision;store.Remove(text.Id);Check(store.QueryRevision>before,"removing material advances query revision");
            before=store.QueryRevision;store.Undo();Check(store.QueryRevision>before,"undo collection restoration advances query revision");
        });
        Console.WriteLine($"PASS {_checks} data-store checks; isolated artifacts: {root}");
    }

    private static MaterialItem Text(string id, string? content = null) => new() { Id = id, Type = MaterialType.Text, DisplayName = id, TextContent = content ?? id };

    private static MaterialItem[] Seed(DataStore store, params string[] ids)
    {
        var items = ids.Select((id, i) => new MaterialItem { Id = id, Type = MaterialType.Text, DisplayName = id, TextContent = id, AddedTime = new DateTime(2026, 1, 1).AddMinutes(-i) }).ToArray();
        foreach (var item in items.Reverse()) store.Add(item);
        return items;
    }

    private static IEnumerable<string> Ids(DataStore store) => store.Items.Select(x => x.Id);

    private static List<Change> Observe(DataStore store)
    {
        var events = new List<Change>();
        store.ItemAdded += item => events.Add(new("add", item.Id, store.CurrentSequence));
        store.ItemRemoved += id => events.Add(new("remove", id, store.CurrentSequence));
        store.ItemCleared += () => events.Add(new("clear", "", store.CurrentSequence));
        return events;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
        Console.WriteLine("PASS " + message);
    }

    private sealed record Change(string Kind, string Id, long Sequence);
}
