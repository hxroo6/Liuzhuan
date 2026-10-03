using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Liuzhuan;
using Liuzhuan.Models;
using Liuzhuan.Services;

internal static class Program
{
    private static int _checks;
    private sealed class ImmediateProgress<T> : IProgress<T>
    { private readonly Action<T> _action; public ImmediateProgress(Action<T> action) => _action=action; public void Report(T value)=>_action(value); }
    private static void Check(bool condition,string description)
    { if(!condition) throw new Exception(description); _checks++; Console.WriteLine("PASS " + description); }
    private static void Reject(Action action,string description)
    { try { action(); } catch(Exception ex) when(ex is ArgumentException or IOException or InvalidOperationException or InvalidDataException) {Check(true,description); return;} throw new Exception("Did not reject: " + description); }
    private static async Task Main()
    {
        var root=Path.Combine(Path.GetTempPath(),"liuzhuan-integrity-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        App.DataDir=Path.Combine(root,"index"); Directory.CreateDirectory(App.DataDir); Liuzhuan.Utils.Logger.Init(root);
        string FileAt(string name,string text) {var path=Path.Combine(root,name); File.WriteAllText(path,text); return path;}
        MaterialItem Source(string name,string contents,MaterialType type=MaterialType.Other) => new() {DisplayName=name,Type=type,FilePath=FileAt(name,contents),IsFavorite=true,GroupName="project",Tags=new[]{"tag"},Note="note"};
        foreach(var json in new[]{"{}","{\"IntegrityBaseline\":null}"}) Check(JsonSerializer.Deserialize<MaterialItem>(json)!.IntegrityBaseline==null,"old/null index has optional baseline");
        using(var store=new DataStore())
        {
            var item=store.Add(Source("one.bin","abc")); var original=File.ReadAllBytes(item.FilePath); var sequence=store.CurrentSequence; int events=0; store.ItemAdded+=_=>events++;
            async Task<IntegrityResult> Read(MaterialItem material) => (await IntegrityService.CheckAsync(IntegrityService.Snapshot(new[]{material}))).Single();
            var first=await Read(item);
            Check(first.Sha256=="BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD" && first.Status==MaterialIntegrityStatus.Unrecorded,"real SHA256 matches known abc digest and unrecorded status");
            Check(item.IntegrityBaseline==null && File.ReadAllBytes(item.FilePath).SequenceEqual(original),"checking neither records baseline nor writes source");
            Check(store.ApplyIntegrityResults(new[]{first})==1 && item.IntegrityDetail.Contains("检查时间"),"results apply snapshot timestamp");
            Check(store.SetIntegrityBaselines(new[]{first})==1 && item.HasIntegrityBaseline,"explicit confirmation API records baseline");
            Check(item.IntegrityStatus==MaterialIntegrityStatus.NotChecked,"new baseline requires next comparison");
            var normal=await Read(item); Check(normal.Status==MaterialIntegrityStatus.Normal,"second real read matches recorded content");
            store.ApplyIntegrityResults(new[]{normal}); Check(item.IntegrityStatusLabel=="上次检查正常","normal status does not claim continuous monitoring");
            File.WriteAllText(item.FilePath,"abd"); var changed=await Read(item); var oldDigest=item.IntegrityBaseline!.Sha256;
            Check(changed.Status==MaterialIntegrityStatus.Changed && changed.Size==normal.Size,"same name and equal length different bytes are changed");
            store.ApplyIntegrityResults(new[]{changed}); Check(item.IntegrityBaseline.Sha256==oldDigest,"changed content never accepted automatically");
            store.SetIntegrityBaselines(new[]{changed}); Check((await Read(item)).Status==MaterialIntegrityStatus.Normal,"explicitly accepting legitimate edit creates new baseline");
            Check(events==0 && sequence==store.CurrentSequence,"integrity checks and baseline edits do not send LAN additions");
            var stale=await Read(item); var path=item.FilePath; item.FilePath=FileAt("other.bin","abd");
            Check(!item.HasIntegrityBaseline && item.IntegrityStatus==MaterialIntegrityStatus.Unrecorded,"path change invalidates baseline binding");
            Check(store.ApplyIntegrityResults(new[]{stale})==0,"old path result cannot apply after relink");
            item.FilePath=path; Check(store.ApplyIntegrityResults(new[]{stale})==0,"A to B to A path race rejected by source revision");
            var baselineRace=await Read(item); item.IntegrityBaseline=item.IntegrityBaseline with {RecordedUtc=DateTimeOffset.UtcNow.AddSeconds(1)};
            Check(store.ApplyIntegrityResults(new[]{baselineRace})==0,"baseline generation rejects old callback");
            var typeRace=await Read(item); item.Type=MaterialType.Image; item.Type=MaterialType.Other;
            Check(store.ApplyIntegrityResults(new[]{typeRace})==0,"type A to B to A rejects old callback");
            var firstBatch=await Read(item); var second=store.Add(Source("two.bin","second")); var secondBatch=await Read(second);
            File.AppendAllText(second.FilePath,"edited"); var before=item.IntegrityBaseline;
            Reject(()=>store.SetIntegrityBaselines(new[]{firstBatch,secondBatch}),"baseline batch rejects modified source before any updates");
            Check(item.IntegrityBaseline==before && second.IntegrityBaseline==null,"invalid second result leaves first baseline untouched");
            var missing=store.Add(new MaterialItem {Type=MaterialType.Other,FilePath=Path.Combine(root,"missing.bin"),DisplayName="missing"});
            var locked=store.Add(Source("locked.bin","lock"));
            using(var held=new FileStream(locked.FilePath,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            {var outcomes=await IntegrityService.CheckAsync(IntegrityService.Snapshot(new[]{missing,locked,item})); Check(outcomes[0].Status==MaterialIntegrityStatus.Missing && outcomes[1].Status==MaterialIntegrityStatus.ReadFailed && outcomes[2].CanRecord,"missing and read failure stay distinct and other files continue");}
            var textFile=store.Add(Source("note.txt","full original text",MaterialType.Text)); var plain=new MaterialItem{Type=MaterialType.Text,TextContent="inline"};
            Check(IntegrityService.Snapshot(new[]{textFile,plain}).Count==1,"file-backed Text participates and inline text does not");
            var largePath=Path.Combine(root,"large.bin"); var largeBytes=new byte[8*1024*1024]; new Random(7).NextBytes(largeBytes); File.WriteAllBytes(largePath,largeBytes);
            var large=store.Add(new MaterialItem{Type=MaterialType.Other,DisplayName="large",FilePath=largePath});
            using(var cancel=new CancellationTokenSource())
            {bool readBytes=false; var progress=new ImmediateProgress<IntegrityProgress>(p=>{if(p.CurrentName=="large"&&p.ReadBytes>0){readBytes=true;cancel.Cancel();}});
                try{await IntegrityService.CheckAsync(IntegrityService.Snapshot(new[]{item,large}),progress,cancel.Token);throw new Exception("Cancellation ignored");}
                catch(OperationCanceledException){Check(readBytes,"cancellation occurs after actual bytes are read");}
                Check(large.IntegrityBaseline==null && large.IntegrityStatus==MaterialIntegrityStatus.Unrecorded,"canceled operation applies no partial model results");
                Check(SHA256.HashData(File.ReadAllBytes(largePath)).SequenceEqual(SHA256.HashData(largeBytes)),"read cancellation leaves source bytes unchanged");}
            var readRace=false; var raceSnapshot=IntegrityService.Snapshot(new[]{large});
            var raceResults=await IntegrityService.CheckAsync(raceSnapshot,new ImmediateProgress<IntegrityProgress>(p=>{if(p.ReadBytes>0 && !readRace){readRace=true;large.FilePath=path;large.FilePath=largePath;}}));
            Check(readRace && store.ApplyIntegrityResults(raceResults)==0,"controlled path race during asynchronous read rejects result");
            var baselineRace2=false;
            var baselineResult=await IntegrityService.CheckAsync(IntegrityService.Snapshot(new[]{large}),new ImmediateProgress<IntegrityProgress>(p=>{if(p.ReadBytes>0&&!baselineRace2){baselineRace2=true;large.IntegrityBaseline=new("A".PadRight(64,'A'),1,DateTimeOffset.UtcNow,largePath,large.Type);}}));
            Check(baselineRace2&&store.ApplyIntegrityResults(baselineResult)==0,"controlled baseline race during read rejects result");
            var serialization=JsonSerializer.Serialize(item,MigrationService.Json);
            Check(!serialization.Contains("IntegrityStatus")&&!serialization.Contains("IntegrityRevision")&&!serialization.Contains("SourceRevision"),"only baseline persists, transient checks and revisions do not");
            await MigrationCases(root,store,item,textFile,Read);
        }
        using(var reloaded=new DataStore()) Check(reloaded.Items.Any(x=>x.DisplayName=="one.bin"&&x.IntegrityBaseline!=null&&x.IsFavorite&&x.Note=="note"),"saved baseline reloads with organization and favorites");
        Console.WriteLine($"IntegrityChecks passed {_checks} checks. Isolated output: {root}");
    }
    private static async Task MigrationCases(string root,DataStore store,MaterialItem item,MaterialItem text,Func<MaterialItem,Task<IntegrityResult>> read)
    {
        store.SetIntegrityBaselines(new[]{await read(item),await read(text)});
        var originalDigest=item.IntegrityBaseline!.Sha256;
        File.WriteAllText(item.FilePath,"abe");
        var package=Path.Combine(root,"integrity.zip");
        await MigrationService.ExportAsync(package,MigrationService.Snapshot(new[]{item,text},new()),null,CancellationToken.None);
        var manifest=MigrationService.ReadManifest(package);
        Check(manifest.Items.All(x=>x.IntegrityBaseline!.SourcePath==x.FilePath),"export maps baseline binding to portable payload path");
        Check(manifest.Items.Single(x=>x.Id==item.Id).IntegrityBaseline!.Sha256==originalDigest,"packaging digest does not silently replace old baseline after equal size edit");
        var target=Path.Combine(root,"import-app"); var importedRoot=await MigrationService.PrepareImportAsync(package,target,null,CancellationToken.None);
        var imported=JsonSerializer.Deserialize<List<MaterialItem>>(File.ReadAllText(Path.Combine(importedRoot,"data.json")),MigrationService.Json)!;
        var outcomes=await IntegrityService.CheckAsync(IntegrityService.Snapshot(imported));
        Check(outcomes.Single(x=>x.Source.Id==item.Id).Status==MaterialIntegrityStatus.Changed,"same name same size edited content remains changed after export and import");
        Check(outcomes.Single(x=>x.Source.Id==text.Id).Status==MaterialIntegrityStatus.Normal,"file-backed Text baseline migrates and compares correctly");
        Check(imported.All(x=>x.IntegrityBaseline!.SourcePath==x.FilePath),"import baseline binds to new absolute file path");
        void Rewrite(string suffix,Action<JsonObject> alter)
        {var destination=Path.Combine(root,suffix+".zip");File.Copy(package,destination);using(var zip=ZipFile.Open(destination,ZipArchiveMode.Update)){var entry=zip.GetEntry("manifest.json")!;JsonObject node;using(var stream=entry.Open())node=JsonNode.Parse(stream)!.AsObject();alter(node);entry.Delete();using var output=zip.CreateEntry("manifest.json").Open();JsonSerializer.Serialize(output,node);} }
        Rewrite("old",node=>{foreach(var material in node["Items"]!.AsArray())material!.AsObject().Remove("IntegrityBaseline");});
        Check(MigrationService.ReadManifest(Path.Combine(root,"old.zip")).Items.All(x=>x.IntegrityBaseline==null),"legacy migration package without baseline remains readable");
        Rewrite("null",node=>{foreach(var material in node["Items"]!.AsArray())material!["IntegrityBaseline"]=null;});
        Check(MigrationService.ReadManifest(Path.Combine(root,"null.zip")).Items.All(x=>x.IntegrityBaseline==null),"explicit null migration baseline remains readable");
        foreach(var invalid in new[]{"hash","size","binding","type","date"})
        {
            Rewrite(invalid,node=>{var baseline=node["Items"]![0]!["IntegrityBaseline"]!;switch(invalid){case "hash":baseline["Sha256"]="garbage";break;case "size":baseline["Size"]=-1;break;case "binding":baseline["SourcePath"]="files/other/source.bin";break;case "type":baseline["SourceType"]="Text";break;case "date":baseline["RecordedUtc"]=DateTimeOffset.MinValue.ToString("O");break;}});
            Reject(()=>MigrationService.ReadManifest(Path.Combine(root,invalid+".zip")),"migration rejects invalid baseline "+invalid);
        }
    }
}
