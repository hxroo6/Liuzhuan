using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Liuzhuan.Models;
using Liuzhuan.Services;

internal static class Program
{
    private static int _checks;

    [STAThread]
    private static void Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "liuzhuan-deferred-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Liuzhuan.Utils.Logger.Init(root);
        var scheduler = new PumpScheduler();
        var uiThread = Environment.CurrentManagedThreadId;
        var item = new MaterialItem { Id = "same-source", Type = MaterialType.Video, FilePath = Path.Combine(root, "A.mp4") };
        var oldResult = Pending();
        var newResult = Pending();
        var oldCompleted = 0;
        var newCompleted = 0;
        var notificationThreads = new List<int>();
        item.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(MaterialItem.ThumbnailPath) or nameof(MaterialItem.Duration)) notificationThreads.Add(Environment.CurrentManagedThreadId);
        };
        var sourceSeen = new TaskCompletionSource<MaterialSourceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loaderThread = 0;
        var old = DeferredMaterialLoader.LoadAsync(item, snapshot =>
        {
            loaderThread = Environment.CurrentManagedThreadId;
            sourceSeen.SetResult(snapshot);
            return oldResult.Task;
        }, () => oldCompleted++, scheduler);
        var observed = sourceSeen.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        var latest = DeferredMaterialLoader.LoadAsync(item, _ => newResult.Task, () => newCompleted++, scheduler);
        newResult.SetResult(new("new-thumbnail", 22));
        Complete(latest, scheduler);
        Check(item.ThumbnailPath == "new-thumbnail" && item.Duration == 22 && newCompleted == 1, "the latest request applies its thumbnail, duration and completion callback");
        oldResult.SetResult(new("old-thumbnail", 11));
        Complete(old, scheduler);
        Check(item.ThumbnailPath == "new-thumbnail" && item.Duration == 22 && oldCompleted == 0, "an older request completing later cannot overwrite properties or refresh the UI");
        Check(loaderThread != uiThread && notificationThreads.All(x => x == uiThread), "the injected production loader runs in the background and property notifications use the caller scheduler");
        Check(observed.FilePath == item.FilePath && observed.Type == MaterialType.Video && observed.SourceRevision == item.SourceRevision, "the loader receives an immutable path, type and source-revision snapshot");

        var pathItem = new MaterialItem { Type = MaterialType.Image, FilePath = Path.Combine(root, "old.png") };
        var pathResult = Pending();
        var pathCompleted = 0;
        var pathTask = DeferredMaterialLoader.LoadAsync(pathItem, _ => pathResult.Task, () => pathCompleted++, scheduler);
        pathItem.FilePath = Path.Combine(root, "replacement.png");
        pathResult.SetResult(new("stale-path-thumbnail", 3));
        Complete(pathTask, scheduler);
        Check(pathItem.ThumbnailPath == "" && pathItem.Duration == 0 && pathCompleted == 0, "a source-path change cancels writeback even without starting another load");

        var roundTrip = new MaterialItem { Type = MaterialType.Image, FilePath = Path.Combine(root, "original.png") };
        var initialRevision = roundTrip.SourceRevision;
        var originalPath = roundTrip.FilePath;
        var roundResult = Pending();
        var roundCompleted = 0;
        var roundTask = DeferredMaterialLoader.LoadAsync(roundTrip, _ => roundResult.Task, () => roundCompleted++, scheduler);
        roundTrip.FilePath = Path.Combine(root, "temporary.png");
        roundTrip.FilePath = originalPath;
        roundResult.SetResult(new("roundtrip-old-thumbnail", 9));
        Complete(roundTask, scheduler);
        Check(roundTrip.SourceRevision > initialRevision && roundTrip.ThumbnailPath == "" && roundTrip.Duration == 0 && roundCompleted == 0, "A-to-B-to-A source changes cannot revive the original request");

        var typed = new MaterialItem { Type = MaterialType.Video, FilePath = Path.Combine(root, "typed.mp4") };
        var typeResult = Pending();
        var typeCompleted = 0;
        var typeTask = DeferredMaterialLoader.LoadAsync(typed, _ => typeResult.Task, () => typeCompleted++, scheduler);
        var typeChanges = new List<string?>();
        typed.PropertyChanged += (_, args) => typeChanges.Add(args.PropertyName);
        var beforeTypeRevision = typed.SourceRevision;
        typed.Type = MaterialType.Audio;
        typeResult.SetResult(new("video-thumbnail", 100));
        Complete(typeTask, scheduler);
        Check(typed.SourceRevision > beforeTypeRevision && typed.ThumbnailPath == "" && typed.Duration == 0 && typeCompleted == 0, "changing material type invalidates an outstanding source load");
        Check(new[] { nameof(MaterialItem.Type), nameof(MaterialItem.KindLabel), nameof(MaterialItem.DetailLabel), nameof(MaterialItem.IsFile), nameof(MaterialItem.HasFileSource) }.All(typeChanges.Contains), "type changes notify all dependent card and source bindings");

        var pathFirst = new MaterialItem { FilePath = "", Type = MaterialType.Text };
        Check(!pathFirst.IsSourceMissing && !pathFirst.HasFileSource, "initializing an empty path before text type leaves inline text usable");
        pathFirst.Type = MaterialType.Other;
        Check(pathFirst.IsSourceMissing && pathFirst.HasFileSource, "changing inline text to a file type refreshes missing-source state");
        pathFirst.Type = MaterialType.Text;
        Check(!pathFirst.IsSourceMissing && !pathFirst.HasFileSource, "changing an empty file item back to inline text clears stale missing state");
        var fileText = new MaterialItem { FilePath = Path.Combine(root, "missing.txt"), Type = MaterialType.Text };
        Check(fileText.IsSourceMissing && fileText.HasFileSource, "file-backed text retains source checking even when its type changes after path initialization");
        var unchangedRevision = fileText.SourceRevision;
        fileText.Type = fileText.Type;
        fileText.FilePath = fileText.FilePath;
        Check(fileText.SourceRevision == unchangedRevision, "unchanged type and path assignments do not invalidate a current load");
        Check(!JsonSerializer.Serialize(fileText).Contains("SourceRevision"), "source generations are excluded from saved indices");

        var unrelated = new MaterialItem { Type = MaterialType.Image, FilePath = Path.Combine(root, "unrelated.png") };
        var unrelatedResult = Pending();
        var unrelatedCount = 0;
        var independent = DeferredMaterialLoader.LoadAsync(unrelated, _ => unrelatedResult.Task, () => unrelatedCount++, scheduler);
        var currentCount = 0;
        var currentTask = DeferredMaterialLoader.LoadAsync(roundTrip, _ => Task.FromResult(new DeferredMaterialProperties(null, 0)), () => currentCount++, scheduler);
        Complete(currentTask, scheduler);
        unrelatedResult.SetResult(new("independent-thumbnail", 0));
        Complete(independent, scheduler);
        Check(unrelated.ThumbnailPath == "independent-thumbnail" && unrelatedCount == 1 && currentCount == 1, "requests for other material objects remain independent");

        var failureResult = Pending();
        var failureCount = 0;
        var failure = DeferredMaterialLoader.LoadAsync(unrelated, _ => failureResult.Task, () => failureCount++, scheduler);
        failureResult.SetException(new IOException("isolated loader failure"));
        Complete(failure, scheduler);
        Check(failureCount == 0 && unrelated.ThumbnailPath == "independent-thumbnail", "a failing loader cannot change cached properties or invoke completion");

        var skipCount = 0;
        var skipped = DeferredMaterialLoader.LoadAsync(pathFirst, _ => { skipCount++; return Task.FromResult(new DeferredMaterialProperties("wrong", 1)); }, () => skipCount++, scheduler);
        Check(skipped.IsCompleted && skipCount == 0, "inline text skips source loading without running the loader or callback");
        Console.WriteLine($"PASS {_checks} deferred-source checks; isolated artifacts: {root}");
    }

    private static TaskCompletionSource<DeferredMaterialProperties> Pending() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Complete(Task task, PumpScheduler scheduler)
    {
        var clock = Stopwatch.StartNew();
        while (!task.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(5))
        {
            scheduler.Pump();
            Thread.Sleep(1);
        }
        if (!task.IsCompleted) throw new TimeoutException("Controlled deferred task did not complete.");
        task.GetAwaiter().GetResult();
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
        Console.WriteLine("PASS " + message);
    }
    private sealed class PumpScheduler : TaskScheduler
    {
        private readonly ConcurrentQueue<Task> _tasks = new();
        protected override void QueueTask(Task task) => _tasks.Enqueue(task);
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
        protected override IEnumerable<Task> GetScheduledTasks() => _tasks.ToArray();
        public void Pump() { while (_tasks.TryDequeue(out var task)) TryExecuteTask(task); }
    }
}
