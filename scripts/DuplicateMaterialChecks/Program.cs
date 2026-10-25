using System.IO;
using System.Security.Cryptography;
using Liuzhuan.Models;
using Liuzhuan.Services;

internal static class Program
{
    private static int _checks;
    private sealed class ImmediateProgress<T> : IProgress<T>
    { private readonly Action<T> _callback; public ImmediateProgress(Action<T> callback) => _callback = callback; public void Report(T value) => _callback(value); }
    private static void Check(bool value, string name)
    { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); _checks++; }
    private static IntegritySource Source(string path, string? id = null) => new(id ?? Guid.NewGuid().ToString("N"), Path.GetFileName(path), path, MaterialType.Other, 0, 0, null);
    private static async Task Main(string[] args)
    {
        var output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "liuzhuan-duplicate-checks");
        var root = Path.Combine(output, "fixture-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string FileAt(string name, string body) { var path = Path.Combine(root, name); File.WriteAllText(path, body); return path; }
        var first = FileAt("first.bin", "abc"); var renamed = FileAt("renamed.txt", "abc");
        var different = FileAt("same-size-different.bin", "abd"); var unique = FileAt("unique-size.bin", "unique length");
        var before = Directory.GetFiles(root).ToDictionary(x => x, File.ReadAllBytes);
        var results = await DuplicateMaterialService.ScanAsync(new[] { Source(first), Source(renamed), Source(different), Source(unique) });
        Check(results.Groups.Count == 1 && results.Groups[0].Files.Count == 2, "renamed and differently typed identical files form one exact-content group");
        Check(results.CandidatePaths == 3 && results.HashedPaths == 3 && results.ReadBytes == 9, "size prefilter avoids reading the unique-size file");
        Check(results.Groups[0].Sha256 == "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", "production SHA256 matches known abc digest");
        Check(results.LogicalDuplicateBytes == 3 && results.Groups[0].FileBytes == 3, "logical duplicate bytes exclude one retained copy");
        Check(results.Issues.Count == 0 && before.All(x => File.ReadAllBytes(x.Key).SequenceEqual(x.Value)), "scan leaves all source bytes unchanged");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        var aliases = new[] { Source(first), Source(Path.Combine(root, "sub", "..", "first.bin")), Source(first.ToUpperInvariant()), Source(renamed) };
        var aliasResult = await DuplicateMaterialService.ScanAsync(aliases);
        Check(aliasResult.UniquePaths == 2 && aliasResult.HashedPaths == 2 && aliasResult.ReadBytes == 6, "normalized case-insensitive aliases hash only once per path");
        Check(aliasResult.Groups[0].Files.Single(x => x.Path.Equals(first, StringComparison.OrdinalIgnoreCase)).Sources.Count == 3 && aliasResult.LogicalDuplicateBytes == 3, "alias index count never inflates logical duplicate bytes");
        var onlyAliases = await DuplicateMaterialService.ScanAsync(aliases.Take(3).ToArray());
        Check(onlyAliases.Groups.Count == 0 && onlyAliases.HashedPaths == 0, "several indexes of one path are not distinct duplicate files");
        var fake = new MaterialIntegrityBaseline(new string('A', 64), 3, DateTimeOffset.UtcNow, first, MaterialType.Other);
        var baselineResult = await DuplicateMaterialService.ScanAsync(new[] { Source(first) with { Baseline = fake }, Source(renamed), Source(different) with { Baseline = fake with { SourcePath = different } } });
        Check(baselineResult.Groups.Count == 1 && baselineResult.Groups[0].Files.All(x => x.Path != different), "old matching or mismatching baselines are never current duplicate truth");
        var empty1 = FileAt("empty1.bin", ""); var empty2 = FileAt("empty2.bin", "");
        var empty = await DuplicateMaterialService.ScanAsync(new[] { Source(empty1), Source(empty2) });
        Check(empty.Groups.Count == 1 && empty.HashedPaths == 2 && empty.ReadBytes == 0 && empty.LogicalDuplicateBytes == 0, "zero-byte files form a valid duplicate group without invented savings");
        var textFile = new MaterialItem { Type = MaterialType.Text, FilePath = renamed }; var inline = new MaterialItem { Type = MaterialType.Text, TextContent = "abc" };
        Check(IntegrityService.Snapshot(new[] { textFile, inline }).Count == 1, "existing integrity snapshot includes file-backed text but excludes inline text");
        var missingPath = Path.Combine(root, "not-created.bin");
        var missing = await DuplicateMaterialService.ScanAsync(new[] { Source(missingPath), Source(first), Source(renamed) });
        Check(missing.Issues.Single().Kind == DuplicateIssueKind.Missing && missing.Groups.Count == 1 && !File.Exists(missingPath), "missing sources are visible and unrelated comparisons continue");
        using (var held = new FileStream(first, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var locked = await DuplicateMaterialService.ScanAsync(new[] { Source(first), Source(renamed) });
            Check(locked.Issues.Any(x => x.Path == first && x.Kind == DuplicateIssueKind.ReadFailed) && locked.Groups.Count == 0, "locked candidate is reported and never declared equal");
        }
        var changedBeforeHash = FileAt("change-before-hash.bin", "abc");
        bool changed = false;
        var beforeHash = await DuplicateMaterialService.ScanAsync(new[] { Source(changedBeforeHash), Source(renamed) }, new ImmediateProgress<DuplicateProgress>(p =>
        { if (!changed && p.Stage == "正在比较内容") { changed = true; File.WriteAllText(changedBeforeHash, "abd"); File.SetLastWriteTimeUtc(changedBeforeHash, DateTime.UtcNow.AddMinutes(2)); } }));
        Check(changed && beforeHash.Groups.Count == 0 && beforeHash.Issues.Any(x => x.Kind == DuplicateIssueKind.SourceChanged), "same-size source change after size capture is excluded");
        var changedAfterHash = FileAt("change-after-hash.bin", "abc"); bool changedAgain = false;
        var afterHash = await DuplicateMaterialService.ScanAsync(new[] { Source(changedAfterHash), Source(renamed) }, new ImmediateProgress<DuplicateProgress>(p =>
        { if (!changedAgain && p.Stage == "正在复核来源") { changedAgain = true; File.WriteAllText(changedAfterHash, "abd"); File.SetLastWriteTimeUtc(changedAfterHash, DateTime.UtcNow.AddMinutes(2)); } }));
        Check(changedAgain && afterHash.Groups.Count == 0 && afterHash.Issues.Any(x => x.Kind == DuplicateIssueKind.SourceChanged), "earlier file changed while scan finishes cannot remain in duplicate results");
        var item = new MaterialItem { Type = MaterialType.Other, FilePath = first }; var peer = new MaterialItem { Type = MaterialType.Other, FilePath = renamed };
        var snapshot = IntegrityService.Snapshot(new[] { item, peer }); var stale = await DuplicateMaterialService.ScanAsync(snapshot);
        item.FilePath = different; item.FilePath = first;
        var reconciled = DuplicateMaterialService.Reconcile(stale, IntegrityService.Snapshot(new[] { item, peer }));
        Check(reconciled.Groups.Count == 0 && reconciled.Issues.Any(x => x.Kind == DuplicateIssueKind.SourceChanged), "owner-thread reconcile rejects source A to B to A callbacks");
        var removed = DuplicateMaterialService.Reconcile(stale, IntegrityService.Snapshot(new[] { peer }));
        Check(removed.Groups.Count == 0 && removed.CheckedFiles.Count == 1, "removed indexes do not remain as duplicate group members");
        var sameModel = IntegrityService.Snapshot(new[] { peer, item }); var sameScan = await DuplicateMaterialService.ScanAsync(sameModel);
        peer.IntegrityBaseline = fake with { SourcePath = peer.FilePath };
        var baselineOnly = DuplicateMaterialService.Reconcile(sameScan, IntegrityService.Snapshot(new[] { peer, item }));
        Check(baselineOnly.Groups.Count == 1 && baselineOnly.CheckedFiles.Count == sameScan.CheckedFiles.Count, "changing saved baseline alone does not invalidate current byte comparison");
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel(); bool canceled = false;
            try { await DuplicateMaterialService.ScanAsync(snapshot, cancellationToken: cancel.Token); } catch (OperationCanceledException) { canceled = true; }
            Check(canceled, "already canceled scans publish no partial report");
        }
        using (var cancel = new CancellationTokenSource())
        {
            bool canceled = false;
            try { await DuplicateMaterialService.ScanAsync(snapshot, new ImmediateProgress<DuplicateProgress>(p => { if (p.Stage == "正在比较内容") cancel.Cancel(); }), cancel.Token); }
            catch (OperationCanceledException) { canceled = true; }
            Check(canceled && File.ReadAllText(first) == "abc", "canceling active scan leaves originals untouched and returns no partial report");
        }
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        using (var cancel = new CancellationTokenSource())
        {
            var running = DuplicateMaterialService.ScanAsync(snapshot, new ImmediateProgress<DuplicateProgress>(p =>
            { if (p.Stage == "正在比较内容") { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("fixture gate timed out"); } }));
            bool queuedCanceled = false; int queuedProgress = 0;
            try
            {
                Check(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))), "first scan reaches synthetic gate");
                var queued = DuplicateMaterialService.ScanAsync(snapshot, new ImmediateProgress<DuplicateProgress>(_ => queuedProgress++), cancel.Token);
                cancel.CancelAfter(30);
                try { await queued; } catch (OperationCanceledException) { queuedCanceled = true; }
            }
            finally { release.Set(); await running; }
            Check(queuedCanceled && queuedProgress == 0, "process-wide scan slot bounds IO and queued cancellation performs no file work");
        }
        bool rejected = false;
        try { _ = DuplicateMaterialService.ScanAsync(Enumerable.Repeat(Source(first), DuplicateMaterialService.MaxSources + 1).ToArray()); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "over-budget snapshots rejected before file work");
        Check(DuplicateMaterialService.AddBytes(long.MaxValue - 1, 2) == long.MaxValue, "logical aggregate saturates safely rather than wrapping negative");
        File.WriteAllText(renamed, "abd"); var fresh = await DuplicateMaterialService.ScanAsync(new[] { Source(first), Source(renamed) });
        Check(fresh.Groups.Count == 0, "subsequent scan reads current bytes instead of reusing a prior digest");
        Check(((IList<DuplicateGroup>)results.Groups).IsReadOnly && ((IList<DuplicateFile>)results.CheckedFiles).IsReadOnly, "published report collections cannot be mutated by consumers");
        Console.WriteLine($"DuplicateMaterialChecks passed {_checks} checks. Isolated output: {root}");
    }
}
