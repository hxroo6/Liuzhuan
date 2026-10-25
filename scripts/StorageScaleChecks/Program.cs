using System.Diagnostics;
using System.Text.Json;
using Liuzhuan;
using Liuzhuan.Models;
using Liuzhuan.Services;
using Liuzhuan.Services.Lan;

class Program
{
    static int checks;
    static void Check(bool condition,string label){if(!condition)throw new Exception(label);checks++;Console.WriteLine("PASS "+label);}
    static bool Refuses(Action action){try{action();return false;}catch{return true;}}
    static async Task Main(string[] args)
    {
        if(args.Length>1&&args[1]=="--concurrency"){await ConcurrentChecks.Run(args[0]);return;}
        var parent=Path.GetFullPath(args[0]);Directory.CreateDirectory(parent);var root=Path.Combine(parent,"fixture-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        typeof(App).GetProperty(nameof(App.DataDir))!.SetValue(null,root);Liuzhuan.Utils.Logger.Init(root);
        using var store=new DataStore();var old=DateTime.Now.AddDays(-90);
        string Create(string dir,string name,string content="fixture bytes") {var folder=Path.Combine(root,dir);Directory.CreateDirectory(folder);var file=Path.Combine(folder,name);File.WriteAllText(file,content);File.SetCreationTime(file,old);File.SetLastWriteTime(file,old);return file;}
        var external=Create("outside","external.txt"); // an external-to-managed file, never eligible for physical cleanup
        var used=Create("uploads","used.bin");var undo=Create("uploads","undo.bin");var orphan=Create("uploads","orphan.bin");var thumb=Create("thumbnails","orphan.png");
        var fresh=Create("temp","fresh.bin");File.SetLastWriteTime(fresh,DateTime.Now);
        var heic=Create("uploads","original.png");File.WriteAllBytes(heic,new byte[]{0,0,0,24,102,116,121,112,104,101,105,99,0,0,0,0,109,105,102,49,104,101,105,99});File.SetLastWriteTime(heic,old);File.SetCreationTime(heic,old);
        store.Items.Add(new(){Id="old",FilePath=external,DisplayName="old",AddedTime=old});
        store.Items.Add(new(){Id="favorite",FilePath=used,DisplayName="favorite",AddedTime=old,IsFavorite=true});
        store.Items.Add(new(){Id="newest",DisplayName="newest",TextContent="newest",AddedTime=DateTime.Now});
        store.Items.Add(new(){Id="undo",FilePath=undo,DisplayName="undo",AddedTime=old});store.Remove("undo");
        async Task<StoragePreview> Preview()=>await StorageMaintenance.PreviewAsync(root,store.CurrentSequence,StorageMaintenance.Snapshot(store.Items),StorageMaintenance.Snapshot(store.RetainedForUndo),30,1);
        await store.FlushAsync();var before=Directory.GetFiles(root,"*",SearchOption.AllDirectories).Length;
        var preview=await Preview();
        Check(preview.Complete&&preview.History.Select(x=>x.Id).SequenceEqual(new[]{"old"}),"retention protects favorites and newest records");
        Check(preview.Files.Select(x=>x.Path).ToHashSet().SetEquals(new[]{orphan,thumb}),"only stale unreferenced managed copies and thumbnails are recyclable");
        Check(File.Exists(heic)&&!preview.Files.Any(x=>x.Path==heic),"HEIC original retained even without an index reference");
        Check(!preview.Files.Any(x=>x.Path==used||x.Path==undo||x.Path==external||x.Path==fresh),"live, undo, external and recently written files remain protected");
        Check(Directory.GetFiles(root,"*",SearchOption.AllDirectories).Length==before,"preview never mutates files");
        Check(Refuses(()=>StorageMaintenance.Recycle(store,preview,new[]{external})),"path outside preview cannot be injected");
        File.AppendAllText(orphan,"changed");
        Check(Refuses(()=>StorageMaintenance.Recycle(store,preview,new[]{orphan})),"file mutation after preview invalidates the recycle plan");
        preview=await Preview();
        Check(StorageMaintenance.Recycle(store,preview,new[]{thumb})==1&&!File.Exists(thumb),"explicit recycle moves only selected orphan into private trash");
        Check(StorageMaintenance.RestoreLatest(root)==1&&File.Exists(thumb),"trash restores the original location");
        preview=await Preview();StorageMaintenance.Recycle(store,preview,new[]{thumb});File.WriteAllText(thumb,"new occupant");
        Check(StorageMaintenance.RestoreLatest(root)==0&&File.ReadAllText(thumb)=="new occupant","restore never overwrites a new same-name file");
        Check(StorageMaintenance.PurgeTrash(root)==1&&File.ReadAllText(thumb)=="new occupant","permanent purge only deletes manifest-listed trash payloads");
        preview=await Preview();store.SetFavorite("old",true);
        Check(Refuses(()=>StorageMaintenance.RemoveHistory(store,preview,new[]{"old"})),"metadata changes and new favorite invalidate old cleanup plans");
        store.SetFavorite("old",false);preview=await Preview();
        Check(StorageMaintenance.RemoveHistory(store,preview,new[]{"old"})==1&&File.Exists(external),"history cleanup removes index only and preserves original bytes");
        Check(store.Undo()&&store.Items.Any(x=>x.Id=="old"),"index cleanup remains undoable");
        var protectedTransfer=Create("uploads","transfer-source.bin");var transfers=Path.Combine(root,"transfers-v1");Directory.CreateDirectory(transfers);
        File.WriteAllText(Path.Combine(transfers,"fixture.json"),JsonSerializer.Serialize(new{path=protectedTransfer,direction="upload",id="fixture"}));
        preview=await Preview();Check(!preview.Files.Any(x=>x.Path==protectedTransfer),"camelCase resumable descriptor protects its source");
        File.WriteAllText(Path.Combine(transfers,"fixture.json"),"broken");preview=await Preview();
        Check(!preview.Complete&&Refuses(()=>StorageMaintenance.Recycle(store,preview,new[]{orphan})),"unreadable transfer state fails closed instead of discarding resumable data");
        File.Delete(Path.Combine(transfers,"fixture.json"));
        var queued=Create("uploads","queued.bin");preview=await Preview();
        TransferJournal.Report(new("active-fixture","fixture","upload",0,100,"已暂停","",0));
        Check(Refuses(()=>StorageMaintenance.Recycle(store,preview,new[]{queued})),"paused transfer history blocks physical recycle");
        TransferJournal.Report(new("active-fixture","fixture","upload",100,100,"已接收","",0));
        var trashId=Guid.NewGuid().ToString("N");var damagedTrash=Path.Combine(root,"cleanup-trash",trashId);Directory.CreateDirectory(damagedTrash);File.WriteAllText(Path.Combine(damagedTrash,"manifest.json"),"broken");
        Check(Refuses(()=>StorageMaintenance.PurgeTrash(root))&&File.Exists(queued),"corrupt trash manifest stops purge without touching source files");
        File.Delete(Path.Combine(damagedTrash,"manifest.json"));Directory.Delete(damagedTrash);
        var uploads=Path.Combine(root,"uploads");var parked=Path.Combine(root,"parked-uploads");
        Check(StorageMaintenance.Under(root,uploads)&&StorageMaintenance.Under(root,parked),"junction fixture move paths stay within the isolated fixture root");
        Directory.Move(uploads,parked);
        try {
            var start=new ProcessStartInfo("cmd.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var arg in new[]{"/d","/c","mklink","/J",uploads,Path.Combine(root,"outside")})start.ArgumentList.Add(arg);
            using var command=Process.Start(start)!;command.WaitForExit();
            if(command.ExitCode!=0)throw new Exception("Cannot create required isolated junction fixture");
            var linked=await Preview();Check(!StorageMaintenance.NoLinks(uploads)&&!linked.Complete&&!linked.Files.Any(x=>x.Path.StartsWith(uploads)),"directory junctions are detected and excluded from cleanup");
        } finally {if(Directory.Exists(uploads)&&(File.GetAttributes(uploads)&FileAttributes.ReparsePoint)!=0)Directory.Delete(uploads);Directory.Move(parked,uploads);}
        var largeBefore=store.Items.Count;for(int i=0;i<5000;i++)store.Items.Add(new(){Id="bulk-"+i,DisplayName="bulk",AddedTime=old});
        var batchTime=Stopwatch.StartNew();store.RemoveMultiple(Enumerable.Range(0,5000).Select(i=>"bulk-"+i).ToList());var removedMs=batchTime.Elapsed.TotalMilliseconds;batchTime.Restart();store.Undo();var undoMs=batchTime.Elapsed.TotalMilliseconds;
        Check(store.Items.Count==largeBefore+5000&&store.Items.Where(x=>x.Id.StartsWith("bulk-")).Select(x=>x.Id).SequenceEqual(Enumerable.Range(0,5000).Select(i=>"bulk-"+i)),"5000-row removal/undo preserves exact order using batched collections");
        store.Items.Clear();for(int i=0;i<120;i++)store.Items.Add(new(){Id="anchor-"+i,DisplayName="anchor",AddedTime=old});
        var originalOrder=store.Items.Select(x=>x.Id).ToArray();var removedRows=originalOrder.Select((id,index)=>(id,index)).Where(x=>x.index%3==0).ToArray();
        store.RemoveMultiple(removedRows.Select(x=>x.id).ToList());store.Items.Move(10,0);store.Items.Insert(0,new(){Id="new-arrival",DisplayName="new arrival"});
        var oracle=store.Items.Select(x=>x.Id).ToList();
        foreach(var row in removedRows.Reverse()){
            int target=-1;for(int j=row.index+1;j<originalOrder.Length;j++){var found=oracle.IndexOf(originalOrder[j]);if(found>=0){target=found;break;}}
            if(target<0)for(int j=row.index-1;j>=0;j--){var found=oracle.IndexOf(originalOrder[j]);if(found>=0){target=found+1;break;}}
            oracle.Insert(target<0?oracle.Count:target,row.id);
        }
        store.Undo();Check(store.Items.Select(x=>x.Id).SequenceEqual(oracle),"bulk undo matches original neighbour-anchor semantics after survivor reordering and new arrivals");
        var library=new MaterialLibraryService(store,a=>{a();return Task.CompletedTask;},()=>"fixture-lib");
        JsonElement Request(long offset=0,long? sequence=null)=>JsonSerializer.SerializeToElement(new{action="query",libraryId="fixture-lib",offset,sequence=sequence??store.CurrentSequence});
        await library.HandleAsync(Request());for(int i=0;i<10;i++)await library.HandleAsync(Request(100*i));
        Check(library.SnapshotBuilds==1,"repeated pages reuse one immutable owner-thread snapshot");
        store.SetFavorite(store.Items.First().Id,true);var stale=JsonSerializer.SerializeToElement(await library.HandleAsync(Request(100,0)));
        Check(stale.TryGetProperty("stale",out var marker)&&marker.GetBoolean(),"changed library invalidates stale page sequence");
        var after=JsonSerializer.SerializeToElement(await library.HandleAsync(Request()));
        Check(after.GetProperty("total").GetInt32()==store.Items.Count&&library.SnapshotBuilds==2,"stale response cannot leave old filtered rows cached");
        var scale=new List<object>();
        foreach(var count in new[]{1000,10000,50000}){
            store.Items.Clear();for(int i=0;i<count;i++)store.Items.Add(new(){Id="scale-"+i,DisplayName="row "+i,AddedTime=DateTime.Now.AddSeconds(-i),Type=(MaterialType)(i%5)});
            var service=new MaterialLibraryService(store,a=>{a();return Task.CompletedTask;},()=>"fixture-lib");
            var clock=Stopwatch.StartNew();await service.HandleAsync(Request());var first=clock.Elapsed.TotalMilliseconds;clock.Restart();
            for(int i=0;i<20;i++)await service.HandleAsync(Request(Math.Min(i*100,count-100)));
            scale.Add(new{Rows=count,FirstPageMs=first,RepeatedPageMeanMs=clock.Elapsed.TotalMilliseconds/20,SnapshotBuilds=service.SnapshotBuilds});
            Check(service.SnapshotBuilds==1,$"{count}-row pages keep one captured snapshot");
        }
        File.WriteAllText(Path.Combine(parent,"storage-scale.json"),JsonSerializer.Serialize(new{Checks=checks,BulkRemoveMs=removedMs,BulkUndoMs=undoMs,Scale=scale},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"Storage/scale checks: {checks} passed. Synthetic fixture only; no user data cleaned.");
    }
}
