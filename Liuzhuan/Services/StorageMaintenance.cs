using System.IO;
using Liuzhuan.Models;

namespace Liuzhuan.Services;

public sealed record StorageReference(string Id, string Name, string Path, string Thumbnail, bool Favorite, DateTime Added, long Revision)
{
    public string Detail=>$"{Added.ToLocalTime():yyyy-MM-dd HH:mm} · {(Path.Length>0?"文件索引":"文字索引")}";
    public string SourceLabel=>Path.Length>0?Path:"仅移除索引，正文仍可从素材栏撤回";
}
public sealed record StorageFile(string Path, long Bytes, long Modified, string Kind)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Detail => $"{Kind} · {Utils.FileClassifier.FormatSize(Bytes)} · {new DateTime(Modified, DateTimeKind.Utc).ToLocalTime():yyyy-MM-dd}";
}
public sealed record StoragePreview(string Root, long Sequence, int Days, int KeepNewest, StorageReference[] History,
    StorageFile[] Files, long IndexBytes, long ManagedBytes, long ExternalBytes, long ThumbnailBytes, long RecoveryBytes,
    long TrashBytes, int Favorites, int Missing, bool Complete, string[] Warnings, string[] TrashDetails, string TrashVersion);
public static class StorageTrashVersion
{
    public static string Of(StorageTrashBatch[] batches)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(batches)));
}
public sealed record StorageTrashEntry(string Source, string Destination, long Bytes, long Modified);
public sealed record StorageTrashBatch(string Id, DateTime Created, StorageTrashEntry[] Entries);

/// <summary>Explicit preview only. External originals, referenced/undo files, HEIC originals and transfer state are protected.</summary>
public static class StorageMaintenance
{
    private static readonly string[] Owned = { "uploads", "temp", "thumbnails" };
    private const int Limit = 100_000;
    public static StorageReference[] Snapshot(IEnumerable<MaterialItem> items) => items.Select(x =>
        new StorageReference(x.Id, x.DisplayName, x.FilePath, x.ThumbnailPath, x.IsFavorite, x.AddedTime, x.MetadataRevision)).ToArray();
    private static string Full(string path) => System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar);
    public static bool Under(string root, string path) => Full(path).StartsWith(Full(root) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public static bool NoLinks(string path)
    {
        try {
            var full=System.IO.Path.GetFullPath(path);
            for(var p=full; !string.IsNullOrEmpty(p); p=System.IO.Path.GetDirectoryName(p))
                if((File.Exists(p)||Directory.Exists(p)) && (File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)return false;
            return true;
        } catch {return false;}
    }
    private static HashSet<string> Protected(StorageReference[] references) => references.SelectMany(x=>new[]{x.Path,x.Thumbnail})
        .Where(x=>!string.IsNullOrWhiteSpace(x)).Select(Full).ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static void TransferReferences(string root, HashSet<string> paths)
    {
        var directory=System.IO.Path.Combine(root,"transfers-v1");
        if(!Directory.Exists(directory))return;
        if(!NoLinks(directory))throw new IOException("续传目录含链接，停止文件清理。");
        var records=Directory.EnumerateFiles(directory,"*.json").Take(1002).ToArray();
        if(records.Length>1001)throw new IOException("续传描述超出扫描预算。");
        foreach(var file in records) {
            if(!NoLinks(file))throw new IOException("续传描述含链接。");
            if(System.IO.Path.GetFileName(file)=="server-id.json")continue;
            if(new FileInfo(file).Length>65536)throw new IOException("续传描述超过预算。");
            var m=System.Text.Json.JsonSerializer.Deserialize<Lan.ResumableTransferServer.Manifest>(File.ReadAllText(file),new System.Text.Json.JsonSerializerOptions{PropertyNameCaseInsensitive=true});
            if(m==null || string.IsNullOrEmpty(m.Path))throw new IOException("续传描述不可读取。");
            paths.Add(Full(m.Path));
        }
    }
    public static Task<StoragePreview> PreviewAsync(string root,long sequence,StorageReference[] live,StorageReference[] retained,
        int days,int keepNewest,CancellationToken ct=default) => Task.Run(()=> {
        if(days is <1 or >3650 || keepNewest is <0 or >100000)throw new ArgumentException("保留天数需1–3650，最近条数需0–100000。");
        root=Full(root);if(root.StartsWith("\\\\",StringComparison.Ordinal)||Full(System.IO.Path.GetPathRoot(root)!)==root || !NoLinks(root))throw new IOException("数据目录不可安全清理；仅支持本地非根目录。");
        var warnings=new List<string>();var trashDetails=new List<string>();var trashVersion="";bool complete=true;long managed=0,thumbs=0,external=0,recovery=0,trash=0,index=0;int missing=0;
        var cutoff=DateTime.UtcNow.AddDays(-days);var protectedPaths=Protected(live.Concat(retained).ToArray());
        try {TransferReferences(root,protectedPaths);}catch{complete=false;warnings.Add("续传描述不可完整读取，已禁止文件回收。");}
        var latest=live.OrderByDescending(x=>x.Added.ToUniversalTime()).Take(keepNewest).Select(x=>x.Id).ToHashSet();
        var history=live.Where(x=>!x.Favorite && x.Added.ToUniversalTime()<cutoff && !latest.Contains(x.Id)).OrderBy(x=>x.Added).ToArray();
        var externalSeen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var row in live) {
            ct.ThrowIfCancellationRequested();if(string.IsNullOrEmpty(row.Path))continue;
            try {var f=new FileInfo(row.Path);if(!f.Exists){missing++;continue;}if(!Owned.Any(x=>Under(System.IO.Path.Combine(root,x),row.Path))&&externalSeen.Add(Full(row.Path)))external+=f.Length;}
            catch{missing++;warnings.Add("有来源不可访问，外部占用仅为可读取部分。");}
        }
        var candidates=new List<StorageFile>();int count=0;
        foreach(var name in Owned.Concat(new[]{"transfers-v1"})) {
            var dir=System.IO.Path.Combine(root,name);if(!Directory.Exists(dir))continue;
            if(!NoLinks(dir)){complete=false;warnings.Add($"{name} 含目录链接，已跳过。");continue;}
            try {
                foreach(var path in Directory.EnumerateFiles(dir)) {
                    ct.ThrowIfCancellationRequested();if(++count>Limit){complete=false;warnings.Add("扫描达到10万文件上限。");break;}
                    if(!NoLinks(path)){complete=false;continue;}
                    var f=new FileInfo(path);var size=f.Length;
                    if(name=="thumbnails")thumbs+=size;else if(name=="transfers-v1")recovery+=size;else managed+=size;
                    if(name=="transfers-v1" || protectedPaths.Contains(Full(path)) || f.LastWriteTimeUtc>=cutoff || f.CreationTimeUtc>=DateTime.UtcNow.AddDays(-1))continue;
                    // Received HEIC originals remain protected even when only their converted derivative is indexed.
                    if(name!="thumbnails" && HeicImageConverter.IsHeic(path))continue;
                    candidates.Add(new(path,size,f.LastWriteTimeUtc.Ticks,name=="thumbnails"?"未引用缩略图":"未引用托管副本"));
                }
            }catch(OperationCanceledException){throw;}catch{complete=false;warnings.Add($"{name} 无法完整读取。");}
        }
        try{var f=new FileInfo(System.IO.Path.Combine(root,"data.json"));if(f.Exists)index=f.Length;}
        catch{complete=false;warnings.Add("索引占用不可读取。");}
        try {var batches=Batches(root);trashVersion=StorageTrashVersion.Of(batches);foreach(var batch in batches)foreach(var row in batch.Entries){ct.ThrowIfCancellationRequested();var path=TrashPath(root,batch.Id,row.Destination);if(NoLinks(path)&&File.Exists(path)){trash+=new FileInfo(path).Length;trashDetails.Add($"{batch.Created.ToLocalTime():yyyy-MM-dd HH:mm} · {row.Source} · {Utils.FileClassifier.FormatSize(row.Bytes)}");}} }
        catch{complete=false;warnings.Add("回收区记录不可读取，请保留原文件并检查。");}
        return new StoragePreview(root,sequence,days,keepNewest,history,candidates.ToArray(),index,managed,external,thumbs,recovery,trash,
            live.Count(x=>x.Favorite),missing,complete,warnings.Distinct().Take(8).ToArray(),trashDetails.ToArray(),trashVersion);
    },ct);
    public static int RemoveHistory(DataStore store,StoragePreview preview,IEnumerable<string> ids)
    {
        ValidateStore(store,preview);
        var selected=ids.ToHashSet();var plans=preview.History.Where(x=>selected.Contains(x.Id)).ToArray();
        if(plans.Length!=selected.Count)throw new InvalidOperationException("所选条目不在预览中。");
        var current=Snapshot(store.Items).ToDictionary(x=>x.Id);
        if(plans.Any(x=>!current.TryGetValue(x.Id,out var row)||row!=x||row.Favorite))throw new InvalidOperationException("素材已变化，请重新预览。");
        store.RemoveMultiple(plans.Select(x=>x.Id).ToList());return plans.Length;
    }
    private static void ValidateStore(DataStore store,StoragePreview preview)
    {
        if(store.LoadFailed || Full(ConfigService.GetEffectiveDataDir())!=preview.Root || store.CurrentSequence!=preview.Sequence)
            throw new InvalidOperationException("素材库已变化或不可读取，请重新预览。");
    }
    /// <summary>Runs on the store owner thread after confirmation; never recursively moves a directory.</summary>
    public static int Recycle(DataStore store,StoragePreview preview,IEnumerable<string> paths)
    {
        ValidateStore(store,preview);if(!preview.Complete)throw new InvalidOperationException("扫描不完整，不能回收文件。");
        if(TransferJournal.HasPending)throw new InvalidOperationException("仍有活动或待恢复收发任务，请先处理任务再回收。");
        var selected=paths.Select(Full).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(selected.Count is <1 or >200)throw new ArgumentException("每批请选择1–200个文件。");
        var rows=preview.Files.Where(x=>selected.Contains(Full(x.Path))).ToArray();
        if(rows.Length!=selected.Count)throw new InvalidOperationException("文件不在本次预览中。");
        var protectedPaths=Protected(Snapshot(store.Items.Concat(store.RetainedForUndo)));
        TransferReferences(preview.Root,protectedPaths);
        foreach(var row in rows) {
            var f=new FileInfo(row.Path);
            if(!Owned.Any(x=>string.Equals(Full(System.IO.Path.GetDirectoryName(row.Path)!),Full(System.IO.Path.Combine(preview.Root,x)),StringComparison.OrdinalIgnoreCase)) ||
                !NoLinks(row.Path) || protectedPaths.Contains(Full(row.Path)) || !f.Exists || f.Length!=row.Bytes || f.LastWriteTimeUtc.Ticks!=row.Modified ||
                row.Kind!="未引用缩略图" && HeicImageConverter.IsHeic(row.Path))throw new InvalidOperationException("文件已变化或受保护，请重新预览。");
        }
        var id=Guid.NewGuid().ToString("N");var directory=System.IO.Path.Combine(preview.Root,"cleanup-trash",id);
        if(!NoLinks(directory))throw new IOException("回收区包含链接。");
        Directory.CreateDirectory(directory);
        var batch=new StorageTrashBatch(id,DateTime.UtcNow,rows.Select(x=>new StorageTrashEntry(System.IO.Path.GetRelativePath(preview.Root,x.Path),Guid.NewGuid().ToString("N"),x.Bytes,x.Modified)).ToArray());
        // Write intent before moves; interrupted batches can still be restored by inspecting destination existence.
        DurableJson.Write(System.IO.Path.Combine(directory,"manifest.json"),batch);
        int moved=0;
        foreach(var row in batch.Entries) {
            var source=SourcePath(preview.Root,row.Source);var target=TrashPath(preview.Root,id,row.Destination);
            if(!NoLinks(source)||!NoLinks(target))throw new IOException("文件路径已变化，已回收部分可恢复。");
            File.Move(source,target);moved++;
        }
        return moved;
    }
    private static string SourcePath(string root,string relative)
    {var path=Full(System.IO.Path.Combine(root,relative));if(!Owned.Any(x=>string.Equals(Full(System.IO.Path.GetDirectoryName(path)!),Full(System.IO.Path.Combine(root,x)),StringComparison.OrdinalIgnoreCase)))throw new IOException("回收记录来源无效。");return path;}
    private static string TrashPath(string root,string batch,string name)
    {if(!Guid.TryParseExact(batch,"N",out _)||!Guid.TryParseExact(name,"N",out _))throw new IOException("回收记录路径无效。");return System.IO.Path.Combine(root,"cleanup-trash",batch,name);}
    public static StorageTrashBatch[] Batches(string root)
    {
        root=Full(root);if(root.StartsWith("\\\\",StringComparison.Ordinal)||Full(System.IO.Path.GetPathRoot(root)!)==root||!NoLinks(root))throw new IOException("回收区根目录无效。");
        var dir=System.IO.Path.Combine(root,"cleanup-trash");if(!Directory.Exists(dir))return Array.Empty<StorageTrashBatch>();
        if(!NoLinks(dir))throw new IOException("回收区包含链接。");
        var paths=Directory.EnumerateDirectories(dir).Take(1001).ToArray();if(paths.Length>1000)throw new IOException("回收批次超过预算。");
        var batches=new List<StorageTrashBatch>();
        foreach(var path in paths){if(!NoLinks(path))throw new IOException("回收批次包含链接。");if(!Directory.EnumerateFileSystemEntries(path).Any())continue;
            var manifest=System.IO.Path.Combine(path,"manifest.json");if(!NoLinks(manifest)||new FileInfo(manifest).Length>262144)throw new IOException("回收清单无效。");var b=DurableJson.Read<StorageTrashBatch>(manifest);
            if(b==null||b.Id!=System.IO.Path.GetFileName(path)||b.Entries.Length>200)throw new IOException("回收记录损坏。");
            foreach(var row in b.Entries){SourcePath(root,row.Source);TrashPath(root,b.Id,row.Destination);}batches.Add(b);if(batches.Sum(x=>x.Entries.Length)>Limit)throw new IOException("回收条目超过扫描预算。");}
        return batches.OrderByDescending(x=>x.Created).ThenBy(x=>x.Id).ToArray();
    }
    public static int RestoreLatest(string root)
    {
        var batch=Batches(root).FirstOrDefault(x=>x.Entries.Any(r=>File.Exists(TrashPath(root,x.Id,r.Destination))));if(batch==null)return 0;
        int restored=0;
        foreach(var row in batch.Entries){var source=TrashPath(root,batch.Id,row.Destination);var target=SourcePath(root,row.Source);
            if(!NoLinks(source)||!NoLinks(target))throw new IOException("恢复路径含链接。");
            if(File.Exists(source)&&!File.Exists(target)){var f=new FileInfo(source);if(f.Length!=row.Bytes||f.LastWriteTimeUtc.Ticks!=row.Modified)throw new IOException("回收文件已变化，停止恢复。");Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);File.Move(source,target);restored++;}}
        RemoveEmpty(root,batch);return restored;
    }
    public static int PurgeTrash(string root,string? expectedVersion=null)
    {
        var batches=Batches(root);if(expectedVersion!=null&&expectedVersion!=StorageTrashVersion.Of(batches))throw new IOException("回收区已变化，请重新预览后确认。");
        int removed=0;
        foreach(var batch in batches){foreach(var row in batch.Entries){var path=TrashPath(root,batch.Id,row.Destination);
            if(!NoLinks(path))throw new IOException("回收路径包含链接，停止永久删除。");
            if(File.Exists(path)){var f=new FileInfo(path);if(f.Length!=row.Bytes||f.LastWriteTimeUtc.Ticks!=row.Modified)throw new IOException("回收文件已变化，请检查。");File.Delete(path);removed++;}}RemoveEmpty(root,batch);}
        return removed;
    }
    private static void RemoveEmpty(string root,StorageTrashBatch batch)
    {
        var directory=System.IO.Path.Combine(root,"cleanup-trash",batch.Id);var manifest=System.IO.Path.Combine(directory,"manifest.json");
        if(!NoLinks(directory)||!NoLinks(manifest))return;
        if(Directory.EnumerateFileSystemEntries(directory).All(x=>x==manifest)){
            File.Delete(manifest);try{Directory.Delete(directory);}catch(IOException){/* Never recursively remove unexpected new files. */}
        }
    }
}
