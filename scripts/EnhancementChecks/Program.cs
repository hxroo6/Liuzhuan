using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Liuzhuan;
using Liuzhuan.Models;
using Liuzhuan.Services;
using Liuzhuan.Services.Lan;

var output=Path.GetFullPath(args.Length>0?args[0]:"F:/appdata/Temp/liuzhuan-m57-enhancements");
var root=Path.Combine(output,"run-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
typeof(App).GetProperty(nameof(App.DataDir))!.SetValue(null,Path.Combine(root,"library"));
Liuzhuan.Utils.Logger.Init(root);
int checks=0;void Check(bool value,string name){if(!value)throw new Exception(name);checks++;Console.WriteLine("PASS "+name);}
var tall=Path.Combine(root,"tall.png");
var pixels=BitmapSource.Create(512,8192,96,96,PixelFormats.Bgra32,null,new byte[512*8192*4],512*4);pixels.Freeze();
var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(pixels));using(var file=File.Create(tall))encoder.Save(file);
var png=await NetworkThumbnailService.CreateAsync(tall,1);
using(var stream=new MemoryStream(png)){
    var frame=BitmapDecoder.Create(stream,BitmapCreateOptions.None,BitmapCacheOption.OnLoad).Frames[0];
    Check(frame.PixelWidth<=256&&frame.PixelHeight<=256,"network thumbnail bounds BOTH dimensions for a tall screenshot");
}
Check(ReferenceEquals(png,await NetworkThumbnailService.CreateAsync(tall,1)),"same source version reuses a bounded thumbnail");
Check(!ReferenceEquals(png,await NetworkThumbnailService.CreateAsync(tall,2)),"source revision invalidates an equal-path thumbnail");

var row=new MaterialItem {Id="immutable",Type=MaterialType.Text,DisplayName="alpha",TextContent="original",GroupName="group",Tags=new[]{"tag"}};
var snapshot=DesktopMaterialQuery.Capture(new[]{row});row.DisplayName="beta";row.TextContent="modified";row.Tags=new[]{"changed"};
Check(DesktopMaterialQuery.Find(snapshot,"Text","alpha",null,null).Single()==row,"query reads immutable fields and returns the original owner object");
Check(DesktopMaterialQuery.Find(snapshot,"Text","beta",null,null).Length==0,"later mutable fields cannot change an in-flight query");
Check(DesktopMaterialQuery.Find(snapshot,"Recent","",null,"tag").Length==1,"captured tags remain isolated");
using(var cancel=new CancellationTokenSource()){cancel.Cancel();try{DesktopMaterialQuery.Find(snapshot,"Recent","",null,null,cancel.Token);throw new Exception("query ignored cancellation");}catch(OperationCanceledException){Check(true,"obsolete query observes cancellation");}}

using(var store=new DataStore()){
    var journal=new LibraryChangeJournal(store);
    var text=new MaterialItem {Type=MaterialType.Text,TextContent="journal-text",DisplayName="journal"};store.Add(text);store.SetFavorite(text.Id,true);
    var opts=new JsonSerializerOptions {PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
    JsonElement Read(string run,long since,long until=-1)=>JsonSerializer.SerializeToElement(journal.Read("library",run,since,until,store.CurrentSequence,store.Items.Count),opts);
    var first=Read(journal.ServerRun,0);Check(!first.GetProperty("requiresFull").GetBoolean()&&first.GetProperty("changes").GetArrayLength()==2,"bounded journal includes adds and metadata updates");
    Check(first.GetProperty("changes")[1].GetProperty("item").GetProperty("favorite").GetBoolean(),"metadata delta carries the authoritative updated fields");
    Check(Read("another-run",0).GetProperty("requiresFull").GetBoolean(),"server restart/run mismatch requires a full snapshot");
    Check(Read(journal.ServerRun,0,store.CurrentSequence-1).GetProperty("requiresFull").GetBoolean(),"moving sequence refuses an inconsistent multi-page update");
    var before=store.CurrentSequence;store.Remove(text.Id);Check(Read(journal.ServerRun,before).GetProperty("changes")[0].GetProperty("kind").GetString()=="delete","journal preserves a deletion tombstone");
    await store.FlushAsync();
}

var subscription=Path.Combine(root,"subscription");Directory.CreateDirectory(subscription);
var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var folderLibrary=Path.Combine(root,"folder-library");Directory.CreateDirectory(folderLibrary);
using(var folders=new FolderCollectionService(folderLibrary,async path=>{entered.TrySetResult();await gate.Task;})){
    await folders.AddAsync(subscription);File.WriteAllText(Path.Combine(subscription,"new.txt"),"durable-order");
    var scan=folders.ScanAsync();await entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
    Check(!File.ReadAllText(Path.Combine(folderLibrary,"collection-folders.json")).Contains("new.txt"),"Seen is not persisted while durable material confirmation is pending");
    gate.TrySetResult();await scan;
    Check(File.ReadAllText(Path.Combine(folderLibrary,"collection-folders.json")).Contains("new.txt"),"Seen advances only AFTER durable confirmation");
}
Console.WriteLine($"{checks} enhancement checks passed. Evidence: {root}");
