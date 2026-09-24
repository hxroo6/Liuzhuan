using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Liuzhuan;
using Liuzhuan.Models;
using Liuzhuan.Services;

internal static class Program
{
    private sealed class DirectProgress : IProgress<string> { private readonly Action<string> _action; public DirectProgress(Action<string> action) { _action = action; } public void Report(string value) => _action(value); }
    static async Task Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "liuzhuan-migration-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Liuzhuan.Utils.Logger.Init(root);
        var old = Path.Combine(root, "old-pc"); Directory.CreateDirectory(old);
        var source = Path.Combine(old, "同名素材.bin");
        await File.WriteAllBytesAsync(source, Enumerable.Range(0, 4 * 1024 * 1024).Select(i => (byte)(i % 251)).ToArray());
        var thumb = Path.Combine(old, "thumb.png"); await File.WriteAllBytesAsync(thumb, new byte[]{1,2,3,4});
        var second = Path.Combine(old, "second"); Directory.CreateDirectory(second);
        var secondSource = Path.Combine(second, "同名素材.bin"); await File.WriteAllTextAsync(secondSource, "another file");
        var items = new[] {
            new MaterialItem { Id="a", Type=MaterialType.Other, DisplayName="主素材", FilePath=source, ThumbnailPath=thumb, IsFavorite=true, AddedTime=new DateTime(2026,9,1), Size=10 },
            new MaterialItem { Id="b", Type=MaterialType.Text, DisplayName="笔记", TextContent="换台电脑也保留的文字\n第二行", IsFavorite=true },
            new MaterialItem { Id="c", Type=MaterialType.Other, DisplayName="同名素材", FilePath=secondSource },
            new MaterialItem { Id="d", Type=MaterialType.Other, DisplayName="重复引用", FilePath=source }
        };
        var settings = new MigrationService.Settings { ClipboardMonitorEnabled=true, HeicConversion=HeicConversionMode.Jpeg, LanEnabled=true, Password="test-only", Port=8999, AutoStart=true };
        var snapshot=MigrationService.Snapshot(items, settings);
        var package=Path.Combine(root,"roundtrip.zip");
        await MigrationService.ExportAsync(package,snapshot,null,default);
        void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);}
        var manifest=MigrationService.ReadManifest(package);
        Check(manifest.Files.Count==3,"deduplicates repeated paths while retaining same-name distinct files");
        Check(manifest.Items[0].FilePath.StartsWith("files/") && snapshot.Items[0].FilePath==source,"portable paths; original UI snapshot unchanged");
        var target=Path.Combine(root,"new-pc"); Directory.CreateDirectory(target);
        var existing=Path.Combine(root,"existing-library"); Directory.CreateDirectory(existing);
        await File.WriteAllTextAsync(Path.Combine(existing,"keep.txt"),"old library");
        var oldConfig=JsonSerializer.Serialize(new{DataDir=existing});
        await File.WriteAllTextAsync(Path.Combine(target,"config.json"),oldConfig);
        // Rename the entire source folder: import cannot fall back to old absolute paths.
        var moved=Path.Combine(root,"offline-old-pc"); Directory.Move(old,moved);
        var imported=await MigrationService.PrepareImportAsync(package,target,null,default);
        Check(File.ReadAllText(Path.Combine(target,"config.json"))==oldConfig,"prepare does not switch active config or overwrite old library");
        var restored=JsonSerializer.Deserialize<List<MaterialItem>>(File.ReadAllText(Path.Combine(imported,"data.json")),MigrationService.Json)!;
        Check(restored.Count==4 && restored[0].IsFavorite && restored[1].TextContent==items[1].TextContent && restored[0].AddedTime==items[0].AddedTime,"text, IDs, favorites and timestamps survive");
        Check(File.ReadAllBytes(restored[0].FilePath).SequenceEqual(File.ReadAllBytes(Path.Combine(moved,"同名素材.bin"))) && File.ReadAllText(restored[2].FilePath)=="another file","original file bytes survive relocation and filename collisions");
        Check(restored[0].Size==new FileInfo(restored[0].FilePath).Length && File.Exists(restored[0].ThumbnailPath),"file size refreshed and thumbnail path remapped");
        var applied=MigrationService.ApplyPending(target)!;
        Check(applied.AutoStart && applied.LanEnabled && applied.Port==8999 && applied.Password=="test-only" && applied.HeicConversion==HeicConversionMode.Jpeg,"portable settings restored");
        Check(Directory.GetFiles(target,"config.before-import-*.json").Any(p=>File.ReadAllText(p)==oldConfig) && File.ReadAllText(Path.Combine(existing,"keep.txt"))=="old library","original config backup and original library retained");
        Check(MigrationService.ApplyPending(target)==null,"activation is one-shot");
        typeof(App).GetProperty(nameof(App.DataDir))!.SetValue(null,target);
        using(var store=new DataStore()) Check(store.Items.Count==4,"production DataStore loads imported files with old PC unavailable");
        string Variant(string name,Action<ZipArchive> rewrite){var path=Path.Combine(root,name+".zip");File.Copy(package,path);using(var z=ZipFile.Open(path,ZipArchiveMode.Update))rewrite(z);return path;}
        void EditManifest(ZipArchive z,Action<MigrationService.Manifest> edit){var e=z.GetEntry("manifest.json")!;MigrationService.Manifest m;using(var s=e.Open())m=JsonSerializer.Deserialize<MigrationService.Manifest>(s,MigrationService.Json)!;edit(m);e.Delete();using var output=z.CreateEntry("manifest.json").Open();JsonSerializer.Serialize(output,m,MigrationService.Json);}
        async Task Reject(string path,string label){var dest=Path.Combine(root,Guid.NewGuid().ToString("N"));bool failed=false;try{await MigrationService.PrepareImportAsync(path,dest,null,default);}catch(Exception e)when(e is InvalidDataException or JsonException){failed=true;}Check(failed && !File.Exists(Path.Combine(dest,"migration-pending.json")) && !File.Exists(Path.Combine(dest,"config.json")),label);}
        await Reject(Variant("traversal",z=>{using var s=new StreamWriter(z.CreateEntry("../escape.txt").Open());s.Write("bad");}),"rejects zip path traversal");
        await Reject(Variant("duplicate",z=>{using var s=new StreamWriter(z.CreateEntry("manifest.json").Open());s.Write("{}");}),"rejects duplicate ZIP entries");
        await Reject(Variant("version",z=>EditManifest(z,m=>m.Version=900)),"rejects unsupported version");
        await Reject(Variant("external-ref",z=>EditManifest(z,m=>m.Items[0].FilePath="C:/external.txt")),"rejects external material paths");
        await Reject(Variant("hash",z=>EditManifest(z,m=>m.Files[0].Sha256=new string('0',64))),"rejects tampered bytes before activation");
        await Reject(Variant("size",z=>EditManifest(z,m=>m.Files[0].Size++)),"rejects truncated/incorrect file lengths");
        await Reject(Variant("missing-fields",z=>{z.GetEntry("manifest.json")!.Delete();using var s=new StreamWriter(z.CreateEntry("manifest.json").Open());s.Write("{}");}),"rejects incomplete manifest");
        using(var cancel=new CancellationTokenSource()){
            var dest=Path.Combine(root,"cancel-import"); bool stopped=false;
            try{await MigrationService.PrepareImportAsync(package,dest,new DirectProgress(_=>cancel.Cancel()),cancel.Token);}catch(OperationCanceledException){stopped=true;}
            Check(stopped && !File.Exists(Path.Combine(dest,"migration-pending.json")) && !Directory.EnumerateFiles(dest,"*",SearchOption.AllDirectories).Any(),"cancelled import cleans staging and never activates");
        }
        var existingPackage=Path.Combine(root,"keep.zip"); File.WriteAllText(existingPackage,"preserve existing output");
        using(var cancel=new CancellationTokenSource()){
            var valid=MigrationService.Snapshot(new[]{new MaterialItem{Type=MaterialType.Other,FilePath=Path.Combine(moved,"同名素材.bin")}},settings);
            bool stopped=false;try{await MigrationService.ExportAsync(existingPackage,valid,new DirectProgress(_=>cancel.Cancel()),cancel.Token);}catch(OperationCanceledException){stopped=true;}
            Check(stopped && File.ReadAllText(existingPackage)=="preserve existing output" && Directory.GetFiles(root,"*.partial").Length==0,"cancelled export preserves existing destination and removes partial output");
        }
        bool missing=false;try{await MigrationService.ExportAsync(existingPackage,snapshot,null,default);}catch(FileNotFoundException){missing=true;}
        Check(missing && File.ReadAllText(existingPackage)=="preserve existing output","missing source fails visibly without replacing export");
                var changedTarget=Path.Combine(root,"changed-preview"); var expected=MigrationService.ReadManifest(package); expected.Settings.Port++;
        bool changed=false; try { await MigrationService.PrepareImportAsync(package,changedTarget,null,default,expected); } catch(IOException) { changed=true; }
        Check(changed && !File.Exists(Path.Combine(changedTarget,"migration-pending.json")),"rejects archive changed after preview");
        Console.WriteLine("Artifacts: "+root);
    }
}
