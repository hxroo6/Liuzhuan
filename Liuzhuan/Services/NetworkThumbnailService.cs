using System.IO;
using System.Windows.Media.Imaging;

namespace Liuzhuan.Services;

public static class NetworkThumbnailService
{
    private static readonly SemaphoreSlim Gate = new(2);
    private sealed record CacheEntry(string Path,long Length,long Modified,byte[] Bytes);
    private static readonly Dictionary<string,CacheEntry> Cache=new(StringComparer.OrdinalIgnoreCase);
    private static long _cacheBytes;
    private const long CacheBudget=8*1024*1024;
    public static async Task<byte[]> CreateAsync(string path,long sourceRevision=0)
    {
        path=Path.GetFullPath(path);
        var info=new FileInfo(path);var length=info.Length;var modified=info.LastWriteTimeUtc.Ticks;
        var key=path+"|"+sourceRevision;
        lock(Cache)if(Cache.TryGetValue(key,out var hit)&&hit.Length==length&&hit.Modified==modified)return hit.Bytes;
        await Gate.WaitAsync();
        try
        {
            lock(Cache)if(Cache.TryGetValue(key,out var hit)&&hit.Length==length&&hit.Modified==modified)return hit.Bytes;
            byte[]? result = null;
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var image = ThumbnailBudget.Decode(path,256);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    using var output = new MemoryStream(); encoder.Save(output);
                    if(output.Length>1024*1024)throw new IOException("缩略图超过输出预算。");
                    result = output.ToArray();
                }
                catch (Exception ex) { failure = ex; }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            await Task.Run(thread.Join);
            if (failure != null) throw failure;
            info.Refresh();
            if(info.Exists&&info.Length==length&&info.LastWriteTimeUtc.Ticks==modified)
                lock(Cache){
                    if(Cache.Remove(key,out var old))_cacheBytes-=old.Bytes.Length;
                    while(Cache.Count>=256||_cacheBytes+result!.Length>CacheBudget){var first=Cache.FirstOrDefault();if(first.Value==null)break;Cache.Remove(first.Key);_cacheBytes-=first.Value.Bytes.Length;}
                    Cache[key]=new(path,length,modified,result!);_cacheBytes+=result!.Length;
                }
            return result!;
        }
        finally { Gate.Release(); }
    }
}
