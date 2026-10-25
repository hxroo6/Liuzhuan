using System.Runtime.CompilerServices;
using Liuzhuan.Models;
using Liuzhuan.Utils;

namespace Liuzhuan.Services;

public sealed record MaterialSourceSnapshot(string FilePath, MaterialType Type, long SourceRevision);
public sealed record DeferredMaterialProperties(string? ThumbnailPath, double Duration);

/// <summary>后台只访问来源快照；仅当前来源的最新请求可在调用方调度器上写回。</summary>
public static class DeferredMaterialLoader
{
    private sealed class RequestState { public long Generation; }
    private static readonly ConditionalWeakTable<MaterialItem, RequestState> Requests = new();
    private static readonly SemaphoreSlim WorkSlots = new(2, 2);

    public static Task LoadAsync(MaterialItem item,
        Func<MaterialSourceSnapshot, Task<DeferredMaterialProperties>> loader,
        Action? onCompleted = null, TaskScheduler? completionScheduler = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(loader);
        var request = Requests.GetValue(item, _ => new RequestState());
        var generation = Interlocked.Increment(ref request.Generation);
        var source = new MaterialSourceSnapshot(item.FilePath, item.Type, item.SourceRevision);
        if (!item.IsFile) return Task.CompletedTask;
        var scheduler = completionScheduler ?? TaskScheduler.FromCurrentSynchronizationContext();
        return RunLoader().ContinueWith(task =>
        {
            // Observe loader failures even for stale requests, but never let them write back or refresh UI.
            if (task.IsFaulted)
            {
                Logger.Error("Deferred material load failed: {0}", task.Exception?.GetBaseException().Message ?? "unknown");
                return;
            }
            if (!task.IsCompletedSuccessfully || generation != Interlocked.Read(ref request.Generation) ||
                item.SourceRevision != source.SourceRevision || item.Type != source.Type || item.FilePath != source.FilePath) return;
            var result = task.Result;
            if (!string.IsNullOrEmpty(result.ThumbnailPath))
            {
                item.ThumbnailPath = result.ThumbnailPath;
            }
            if (result.Duration > 0) item.Duration = result.Duration;
            onCompleted?.Invoke();
        }, CancellationToken.None, TaskContinuationOptions.None, scheduler);

        async Task<DeferredMaterialProperties> RunLoader()
        {
            await WorkSlots.WaitAsync().ConfigureAwait(false);
            try
            {
                // A superseded queued request must not start expensive Shell/media work.
                if (generation != Interlocked.Read(ref request.Generation) || item.SourceRevision != source.SourceRevision)
                    return new(null, 0);
                return await Task.Run(() => loader(source)).ConfigureAwait(false);
            }
            finally { WorkSlots.Release(); }
        }
    }
}
