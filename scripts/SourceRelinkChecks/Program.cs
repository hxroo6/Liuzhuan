using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Liuzhuan;
using Liuzhuan.Models;
using Liuzhuan.Services;

internal static class Program
{
    private static int _checks;

    private static void Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "liuzhuan-source-relink-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Liuzhuan.Utils.Logger.Init(root);

        void Scenario(string name, Action<DataStore, string> test)
        {
            var dataDir = Path.Combine(root, "index-" + name);
            Directory.CreateDirectory(dataDir);
            typeof(App).GetProperty(nameof(App.DataDir))!.SetValue(null, dataDir);
            using var store = new DataStore();
            test(store, dataDir);
        }
        string Fixture(string relative, string bytes)
        {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, bytes);
            return path;
        }
        var singleCandidate = Fixture("single/new-name.png", "image bytes");
        var originalHash = SHA256.HashData(File.ReadAllBytes(singleCandidate));
        Scenario("single", (store, _) =>
        {
            var before = Inline("before");
            var missing = new MaterialItem { Id = "missing", Type = MaterialType.Image, FilePath = Path.Combine(root, "old/original.png"), DisplayName = "original name", Size = 500, AddedTime = new DateTime(2026, 1, 3), IsFavorite = true, GroupName = "project", Tags = new[] { "important" }, Note = "memo", ThumbnailPath = "old-thumbnail", Duration = 90 };
            store.Add(missing); store.Add(before);
            var order = store.Items.Select(x => x.Id).ToArray();
            var time = missing.AddedTime;
            var sequence = store.CurrentSequence;
            var emitted = new List<(string Id, long Seq)>();
            store.ItemAdded += item => emitted.Add((item.Id, store.CurrentSequence));
            var plan = SourceRelinkService.PrepareSingle(missing, singleCandidate);
            Check(plan.CandidateSize == new FileInfo(singleCandidate).Length && missing.IsSourceMissing && missing.FilePath != plan.CandidatePath && emitted.Count == 0, "single preparation validates a candidate without changing the index");
            Check(plan.CandidatePath.EndsWith("new-name.png") && !plan.RequireOriginalSize, "explicit single-file confirmation supports a different name and size of the same material type");
            var fixedItem = store.RelinkSource(plan);
            Check(ReferenceEquals(fixedItem, missing) && SourceRelinkService.PathsEqual(fixedItem.FilePath, singleCandidate) && fixedItem.Size == plan.CandidateSize && !fixedItem.IsSourceMissing, "confirmed single relink changes only the existing item's source and size");
            Check(store.Items.Select(x => x.Id).SequenceEqual(order) && missing.AddedTime == time && missing.DisplayName == "original name", "relink retains original ID, list position, timestamp and display name");
            Check(missing.IsFavorite && missing.GroupName == "project" && missing.Tags.Single() == "important" && missing.Note == "memo", "relink preserves favorites, group, tags and note");
            Check(missing.ThumbnailPath == "" && missing.ThumbnailSource == null && missing.Duration == 0, "relink invalidates old thumbnail and duration caches");
            Check(emitted.SequenceEqual(new[] { (missing.Id, sequence + 1) }) && store.CurrentSequence == sequence + 1, "confirmed relink broadcasts one fresh ItemAdded sequence");
            Check(SHA256.HashData(File.ReadAllBytes(singleCandidate)).SequenceEqual(originalHash), "relink never edits the candidate bytes");
        });
        Scenario("single", (store, _) =>
        {
            var fixedItem = store.Items.Single(x => x.Id == "missing");
            Check(store.Items.Select(x => x.Id).SequenceEqual(new[] { "before", "missing" }) && SourceRelinkService.PathsEqual(fixedItem.FilePath, singleCandidate) && fixedItem.IsFavorite && fixedItem.Note == "memo", "confirmed source path and preserved metadata reload from isolated data.json");
        });

        Scenario("reject", (store, _) =>
        {
            var item = new MaterialItem { Id = "item", Type = MaterialType.Image, FilePath = Path.Combine(root, "old/item.png"), Size = 10, ThumbnailPath = "old" };
            store.Add(item);
            var sequence = store.CurrentSequence;
            Rejects<InvalidOperationException>(() => SourceRelinkService.PrepareSingle(item, Fixture("reject/audio.mp3", "audio")), "single repair rejects a different material type");
            Rejects<ArgumentException>(() => SourceRelinkService.PrepareSingle(Inline("text"), singleCandidate), "inline text has no file source to relink");
            Rejects<FileNotFoundException>(() => SourceRelinkService.PrepareSingle(item, Path.Combine(root, "absent.png")), "nonexistent candidates fail explicitly");
            Rejects<ArgumentException>(() => SourceRelinkService.PrepareSingle(item, "relative.png"), "relative candidate paths are rejected");
            var occupied = new MaterialItem { Id = "occupied", Type = MaterialType.Image, FilePath = singleCandidate };
            store.Add(occupied);
            var plan = SourceRelinkService.PrepareSingle(item, singleCandidate);
            var seq = store.CurrentSequence;
            Rejects<InvalidOperationException>(() => store.RelinkSource(plan), "a candidate already indexed by another material is rejected");
            Check(item.FilePath == Path.Combine(root, "old/item.png") && item.Size == 10 && item.ThumbnailPath == "old" && store.CurrentSequence == seq, "duplicate path rejection changes no source, cache or sequence");
            store.Remove(occupied.Id);
            item.Size = 11;
            Rejects<InvalidOperationException>(() => store.RelinkSource(plan), "stale original size invalidates a prepared plan");
            item.Size = 10;
            item.FilePath = Path.Combine(root, "old/different.png");
            Rejects<InvalidOperationException>(() => store.RelinkSource(plan), "stale original path invalidates a prepared plan");
            item.FilePath = plan.Source.FilePath;
            var volatilePath = Fixture("reject/volatile.png", "before");
            var volatilePlan = SourceRelinkService.PrepareSingle(item, volatilePath);
            File.AppendAllText(volatilePath, " appended");
            Rejects<InvalidOperationException>(() => store.RelinkSource(volatilePlan), "candidate size changes after confirmation preparation are rejected");
            File.Delete(volatilePath);
            Rejects<FileNotFoundException>(() => store.RelinkSource(volatilePlan), "candidate disappearance before apply is rejected");
            var lockedPath = Fixture("reject/locked.png", "locked");
            using (var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Rejects<IOException>(() => SourceRelinkService.PrepareSingle(item, lockedPath), "inaccessible candidate files fail before index mutation");
            store.Remove(item.Id);
            Rejects<InvalidOperationException>(() => store.RelinkSource(plan), "plans for removed IDs cannot resurrect a deleted item");
            Check(sequence > 0, "rejection scenario starts from an indexed source");
        });

        Scenario("batch", (store, _) =>
        {
            var a = new MaterialItem { Id = "a", Type = MaterialType.Other, FilePath = Path.Combine(root, "old/a.bin"), Size = 1, Note = "A" };
            var b = new MaterialItem { Id = "b", Type = MaterialType.Other, FilePath = Path.Combine(root, "old/b.bin"), Size = 2, Note = "B" };
            store.Add(a); store.Add(b);
            var aPath = Fixture("batch/a.bin", "aaaa");
            var bPath = Fixture("batch/b.bin", "bbbb");
            var aPlan = SourceRelinkService.PrepareSingle(a, aPath);
            var bPlan = SourceRelinkService.PrepareSingle(b, bPath);
            var sequence = store.CurrentSequence;
            var emitted = new List<long>();
            store.ItemAdded += _ => emitted.Add(store.CurrentSequence);
            Rejects<InvalidOperationException>(() => store.RelinkSources(new[] { aPlan, bPlan with { CandidateSize = 999 } }), "one invalid batch candidate rejects the full batch");
            Check(a.FilePath == aPlan.Source.FilePath && b.FilePath == bPlan.Source.FilePath && emitted.Count == 0 && store.CurrentSequence == sequence, "failed batch validation leaves earlier candidates unchanged");
            Rejects<InvalidOperationException>(() => store.RelinkSources(new[] { aPlan, bPlan with { CandidatePath = aPath, CandidateSize = aPlan.CandidateSize } }), "one candidate path cannot be assigned to two materials in a batch");
            Rejects<ArgumentException>(() => store.RelinkSources(new[] { aPlan, aPlan }), "duplicate material IDs in a confirmed batch are rejected");
            Check(store.RelinkSources(Array.Empty<SourceRelinkPlan>()) == 0 && store.CurrentSequence == sequence, "an empty confirmation creates no increment or mutation");
            Check(store.RelinkSources(new[] { aPlan, bPlan }) == 2 && emitted.SequenceEqual(new[] { sequence + 1, sequence + 2 }), "confirmed batch produces one continuous fresh sequence per item");
            Check(a.Note == "A" && b.Note == "B" && store.Items.Select(x => x.Id).SequenceEqual(new[] { "b", "a" }), "successful batch retains metadata and collection order");
        });

        var scanRoot = Path.Combine(root, "scan");
        Fixture("scan/a/unique.bin", "1234");
        Fixture("scan/a/ambiguous.bin", "same");
        Fixture("scan/b/ambiguous.bin", "same");
        Fixture("scan/a/wrong-size.bin", "wrong-length");
        Fixture("scan/a/text.txt", "body preview");
        Fixture("scan/a/zero.bin", "nonzero");
        var scanItems = new[]
        {
            Missing("unique", "unique.bin", 4), Missing("ambiguous", "ambiguous.bin", 4), Missing("wrong", "wrong-size.bin", 3), Missing("absent", "absent.bin", 1),
            new MaterialItem { Id = "file-text", Type = MaterialType.Text, FilePath = Path.Combine(root, "old/text.txt"), Size = 12, TextContent = "old preview" },
            Missing("zero", "zero.bin", 0), Inline("inline")
        };
        var snapshots = SourceRelinkService.Snapshot(scanItems);
        var callerThread = Environment.CurrentManagedThreadId;
        var progressOnWorker = true;
        var progressCount = 0;
        var result = SourceRelinkService.FindCandidatesAsync(scanRoot, snapshots, new InlineProgress(p => { progressCount++; progressOnWorker &= Environment.CurrentManagedThreadId != callerThread; })).GetAwaiter().GetResult();
        Check(result.Matches.Single(x => x.Source.Id == "unique").Status == SourceRelinkMatchStatus.Unique, "folder search finds a unique same-name, same-size candidate recursively");
        Check(result.Matches.Single(x => x.Source.Id == "ambiguous").Status == SourceRelinkMatchStatus.Ambiguous && result.Matches.Single(x => x.Source.Id == "ambiguous").Candidates.Count == 2, "folder search reports multiple matching paths as ambiguous");
        Check(result.Matches.Single(x => x.Source.Id == "wrong").Status == SourceRelinkMatchStatus.Missing && result.Matches.Single(x => x.Source.Id == "absent").Status == SourceRelinkMatchStatus.Missing, "folder search does not accept a positive-size mismatch or absent name");
        Check(result.Matches.Single(x => x.Source.Id == "file-text").Status == SourceRelinkMatchStatus.Unique, "folder search includes file-backed text sources");
        Check(result.Matches.Single(x => x.Source.Id == "zero").Status == SourceRelinkMatchStatus.Unique, "zero old size allows a name-matching candidate with an unknown previous byte count");
        Check(result.Matches.Single(x => x.Source.Id == "inline").Status == SourceRelinkMatchStatus.InvalidSource, "folder search clearly reports inline text without a file name");
        Check(scanItems.Zip(snapshots).All(pair => pair.First.FilePath == pair.Second.FilePath), "folder search does not apply a candidate or change live source paths");
        Check(progressCount > 0 && progressOnWorker && result.ScannedFiles == 6, "folder enumeration and progress run on a background thread");
        Check(result.Matches.Single(x => x.Source.Id == "unique").Message.Contains("不能证明内容相同"), "candidate descriptions warn that name and size cannot prove identical content");
        var folderPlan = result.Matches.Single(x => x.Source.Id == "unique").Candidates.Single();
        Rejects<InvalidOperationException>(() => SourceRelinkService.ValidatePlan(folderPlan with { CandidatePath = Fixture("scan/a/different.bin", "1234") }), "folder plans revalidate the original file name at apply time");
        Rejects<ArgumentException>(() => SourceRelinkService.FindCandidatesAsync(Path.GetPathRoot(root)!, snapshots).GetAwaiter().GetResult(), "folder search refuses a whole drive root");
        Rejects<DirectoryNotFoundException>(() => SourceRelinkService.FindCandidatesAsync(Path.Combine(root, "does-not-exist"), snapshots).GetAwaiter().GetResult(), "missing scan directories fail clearly");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            Rejects<OperationCanceledException>(() => SourceRelinkService.FindCandidatesAsync(scanRoot, snapshots, cancellationToken: cancelled.Token).GetAwaiter().GetResult(), "cancelled folder searches produce no candidate-apply operation");
            Rejects<OperationCanceledException>(() => SourceRelinkService.CheckSourcesAsync(snapshots, cancelled.Token).GetAwaiter().GetResult(), "cancelled source checking produces no model mutation");
        }
        using (var cancelled = new CancellationTokenSource())
        {
            Rejects<OperationCanceledException>(() => SourceRelinkService.FindCandidatesAsync(scanRoot, snapshots, new InlineProgress(_ => cancelled.Cancel()), cancelled.Token).GetAwaiter().GetResult(), "cancellation during enumeration stops the scan");
            Check(scanItems.All(x => !x.FilePath.StartsWith(scanRoot)), "mid-scan cancellation leaves all live sources unchanged");
        }
        var sharedSnapshots = SourceRelinkService.Snapshot(new[] { Missing("shared-a", "unique.bin", 4), Missing("shared-b", "unique.bin", 4) });
        var sharedResult = SourceRelinkService.FindCandidatesAsync(scanRoot, sharedSnapshots).GetAwaiter().GetResult();
        Check(sharedResult.Matches.All(x => x.Status == SourceRelinkMatchStatus.Ambiguous && x.Candidates.Count == 1), "one physical candidate shared by multiple source records requires individual confirmation");

        // Junction creation is an isolated fixture, not a link to any user data.
        var outside = Path.Combine(root, "outside");
        Fixture("outside/only-outside.bin", "safe");
        var linked = Path.Combine(scanRoot, "junction");
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", linked, outside }) start.ArgumentList.Add(argument);
        using (var process = Process.Start(start)!)
        {
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException("Cannot create isolated junction fixture: " + output + error);
        }
        var linkResult = SourceRelinkService.FindCandidatesAsync(scanRoot, SourceRelinkService.Snapshot(new[] { Missing("outside", "only-outside.bin", 4) })).GetAwaiter().GetResult();
        Check(linkResult.Matches.Single().Status == SourceRelinkMatchStatus.Missing && linkResult.Issues.Any(x => x.Path == linked && x.Message.Contains("跳过")), "folder scanning never traverses junctions and explicitly reports skipped links");
        Rejects<ArgumentException>(() => SourceRelinkService.FindCandidatesAsync(linked, snapshots).GetAwaiter().GetResult(), "a linked scan root is rejected");
        var nestedOutside = Path.Combine(outside, "nested");
        Directory.CreateDirectory(nestedOutside);
        Rejects<ArgumentException>(() => SourceRelinkService.FindCandidatesAsync(Path.Combine(linked, "nested"), snapshots).GetAwaiter().GetResult(), "a scan root beneath a linked ancestor is rejected");
        Check(File.ReadAllText(Path.Combine(outside, "only-outside.bin")) == "safe" && SHA256.HashData(File.ReadAllBytes(singleCandidate)).SequenceEqual(originalHash), "searching and rejection never alter isolated original files");

        Console.WriteLine($"PASS {_checks} source-relink checks; isolated artifacts: {root}");

        MaterialItem Missing(string id, string file, long size) => new() { Id = id, Type = MaterialType.Other, FilePath = Path.Combine(root, "old", file), Size = size };
    }

    private static MaterialItem Inline(string id) => new() { Id = id, Type = MaterialType.Text, TextContent = id };
    private sealed class InlineProgress : IProgress<SourceRelinkProgress>
    {
        private readonly Action<SourceRelinkProgress> _action;
        public InlineProgress(Action<SourceRelinkProgress> action) => _action = action;
        public void Report(SourceRelinkProgress value) => _action(value);
    }
    private static void Rejects<T>(Action action, string message) where T : Exception
    {
        var rejected = false;
        try { action(); }
        catch (T) { rejected = true; }
        Check(rejected, message);
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
        Console.WriteLine("PASS " + message);
    }
}
