using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
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
            new MaterialItem { Id="a", Type=MaterialType.Other, DisplayName="主素材", FilePath=source, ThumbnailPath=thumb, IsFavorite=true, AddedTime=new DateTime(2026,9,1), Size=10, GroupName="视觉参考", Tags=new[]{"封面", "待用"}, Note="换机后保留的备注\n第二行" },
            new MaterialItem { Id="b", Type=MaterialType.Text, DisplayName="笔记", TextContent="换台电脑也保留的文字\n第二行", IsFavorite=true, GroupName="灵感", Tags=new[]{"文案"}, Note="文字素材的备注" },
            new MaterialItem { Id="c", Type=MaterialType.Other, DisplayName="同名素材", FilePath=secondSource },
            new MaterialItem { Id="d", Type=MaterialType.Other, DisplayName="重复引用", FilePath=source }
        };
        var settings = new MigrationService.Settings { ClipboardMonitorEnabled=true, ExcludedClipboardApps=new[]{"KeePassXC.exe"}, HeicConversion=HeicConversionMode.Jpeg, LanEnabled=true, Password="test-only", Port=8999, AutoStart=true,
            SavedViews=new(){new SavedMaterialView{Name="收藏参考",Tab="Favorites",GroupName="视觉参考",Tag="封面",Keyword="备注"},new SavedMaterialView{Name="未分组",GroupName=""}},
            ImagePresets=new(){Presets=new(){new ImageProcessingPreset{Name="交付 JPEG",Format=ImageOutputFormat.Jpeg,MaxEdge=1920,JpegQuality=82},new ImageProcessingPreset{Name="透明 PNG",Format=ImageOutputFormat.Png,MaxEdge=null,JpegQuality=90}}} };
        void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);}
        var snapshot=MigrationService.Snapshot(items, settings);
        settings.ExcludedClipboardApps[0]="changed.exe";
        Check(snapshot.Settings.ExcludedClipboardApps!.SequenceEqual(new[]{"KeePassXC.exe"}), "snapshot isolates automatic-capture exclusions");
        settings.SavedViews.Clear();
        Check(snapshot.Settings.SavedViews!.Count==2, "snapshot isolates saved filter rules from later configuration changes");
        settings.ImagePresets.Presets.Clear();
        Check(snapshot.Settings.ImagePresets!.Presets.Count==2, "snapshot isolates image presets from later settings changes");
        items[0].Tags[0]="原库随后修改";
        items[0].GroupName="原库的新分组";
        items[0].Note="原库的新备注";
        Check(snapshot.Items[0].Tags.SequenceEqual(new[]{"封面", "待用"}) && snapshot.Items[0].GroupName=="视觉参考" && snapshot.Items[0].Note=="换机后保留的备注\n第二行", "snapshot isolates organization fields and mutable tag arrays");
        var package=Path.Combine(root,"roundtrip.zip");
        await MigrationService.ExportAsync(package,snapshot,null,default);
        var manifest=MigrationService.ReadManifest(package);
        Check(manifest.Settings.SavedViews!.Count==2 && manifest.Settings.SavedViews[0].Tag=="封面" && manifest.Settings.SavedViews[1].GroupName=="", "export preserves named rules and ungrouped criteria");
        Check(manifest.Settings.ImagePresets!.Presets.Count==2 && manifest.Settings.ImagePresets.Presets[0].JpegQuality==82 && manifest.Settings.ImagePresets.Presets[1].MaxEdge==null, "export preserves named image options including original size");
        Check(manifest.Version==1 && manifest.Items[0].GroupName=="视觉参考" && manifest.Items[0].Tags.SequenceEqual(new[]{"封面", "待用"}) && manifest.Items[0].Note=="换机后保留的备注\n第二行", "version-1 export retains groups, tags and multiline notes");
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
        Check(restored[0].GroupName=="视觉参考" && restored[0].Tags.SequenceEqual(new[]{"封面", "待用"}) && restored[0].Note=="换机后保留的备注\n第二行" && restored[1].GroupName=="灵感" && restored[1].Tags.SequenceEqual(new[]{"文案"}), "imported index retains organization for file and text materials");
        Check(File.ReadAllBytes(restored[0].FilePath).SequenceEqual(File.ReadAllBytes(Path.Combine(moved,"同名素材.bin"))) && File.ReadAllText(restored[2].FilePath)=="another file","original file bytes survive relocation and filename collisions");
        Check(restored[0].Size==new FileInfo(restored[0].FilePath).Length && File.Exists(restored[0].ThumbnailPath),"file size refreshed and thumbnail path remapped");
        var applied=MigrationService.ApplyPending(target)!;
        var importedRules=new SavedViewStore(imported);
        Check(importedRules.LoadError==null && importedRules.Views.Count==2 && importedRules.Views[0].Keyword=="备注", "new PC reloads portable saved rules from its imported library");
        var importedPresets=new ImageProcessingPresetStore(imported);
        Check(importedPresets.LoadError==null && importedPresets.Presets.Count==2 && importedPresets.Presets[0].Format==ImageOutputFormat.Jpeg && importedPresets.Presets[1].MaxEdge==null, "new PC reloads image presets from its imported library");
        Check(applied.AutoStart && applied.LanEnabled && applied.Port==8999 && applied.Password=="test-only" && applied.HeicConversion==HeicConversionMode.Jpeg,"portable settings restored");
        var privacyConfig=JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(imported,"appsettings.json")));
        Check(applied.ExcludedClipboardApps!.SequenceEqual(new[]{"KeePassXC.exe"}) && privacyConfig.GetProperty("ExcludedClipboardApps")[0].GetString()=="KeePassXC.exe" && !privacyConfig.TryGetProperty("ClipboardPausedUntil",out _),"migration carries app exclusions but does not carry a temporary pause");
        Check(Directory.GetFiles(target,"config.before-import-*.json").Any(p=>File.ReadAllText(p)==oldConfig) && File.ReadAllText(Path.Combine(existing,"keep.txt"))=="old library","original config backup and original library retained");
        Check(MigrationService.ApplyPending(target)==null,"activation is one-shot");
        typeof(App).GetProperty(nameof(App.DataDir))!.SetValue(null,target);
        using(var store=new DataStore()) {
            Check(store.Items.Count==4,"production DataStore loads imported files with old PC unavailable");
            var organized=store.Items.Single(i=>i.Id=="a");
            Check(organized.GroupName=="视觉参考" && organized.Tags.SequenceEqual(new[]{"封面", "待用"}) && organized.Note=="换机后保留的备注\n第二行", "production DataStore reload preserves imported organization");
        }
        string Variant(string name,Action<ZipArchive> rewrite){var path=Path.Combine(root,name+".zip");File.Copy(package,path);using(var z=ZipFile.Open(path,ZipArchiveMode.Update))rewrite(z);return path;}
        void EditManifest(ZipArchive z,Action<MigrationService.Manifest> edit){var e=z.GetEntry("manifest.json")!;MigrationService.Manifest m;using(var s=e.Open())m=JsonSerializer.Deserialize<MigrationService.Manifest>(s,MigrationService.Json)!;edit(m);e.Delete();using var output=z.CreateEntry("manifest.json").Open();JsonSerializer.Serialize(output,m,MigrationService.Json);}
        void EditManifestJson(ZipArchive z,Action<JsonObject> edit){var e=z.GetEntry("manifest.json")!;JsonObject m;using(var s=e.Open())m=JsonNode.Parse(s)!.AsObject();edit(m);e.Delete();using var output=z.CreateEntry("manifest.json").Open();JsonSerializer.Serialize(output,m,MigrationService.Json);}
        var legacyPackage=Variant("legacy-organization",z=>EditManifestJson(z,m=>{
            m["Settings"]!.AsObject().Remove("SavedViews");
            m["Settings"]!.AsObject().Remove("ImagePresets");
            foreach(var item in m["Items"]!.AsArray()) {var obj=item!.AsObject();obj.Remove("GroupName");obj.Remove("Tags");obj.Remove("Note");}
        }));
        var legacyManifest=MigrationService.ReadManifest(legacyPackage);
        Check(legacyManifest.Settings.SavedViews!.Count==0, "old migration packages without saved rules remain supported");
        Check(legacyManifest.Settings.ImagePresets==null, "old migration packages without image presets remain supported");
        Check(legacyManifest.Items.All(i=>i.GroupName=="" && i.Tags.Length==0 && i.Note==""), "legacy packages without organization fields normalize to empty");
        var legacyTarget=Path.Combine(root,"legacy-pc");
        await MigrationService.PrepareImportAsync(legacyPackage,legacyTarget,null,default,legacyManifest);
        Check(!Directory.EnumerateFiles(legacyTarget,ImageProcessingPresetStore.FileName,SearchOption.AllDirectories).Any(), "old packages do not invent an image preset configuration");
        MigrationService.ApplyPending(legacyTarget);
        typeof(App).GetProperty(nameof(App.DataDir))!.SetValue(null,legacyTarget);
        using(var legacyStore=new DataStore()) Check(legacyStore.Items.Count==4 && legacyStore.Items.All(i=>i.GroupName=="" && i.Tags.Length==0 && i.Note==""), "legacy package imports and production DataStore reloads without organization");
        var nullPackage=Variant("null-organization",z=>EditManifestJson(z,m=>{
            foreach(var item in m["Items"]!.AsArray()) {var obj=item!.AsObject();obj["GroupName"]=null;obj["Tags"]=null;obj["Note"]=null;}
        }));
        Check(MigrationService.ReadManifest(nullPackage).Items.All(i=>i.GroupName=="" && i.Tags.Length==0 && i.Note==""), "explicit null organization values normalize to empty");
        async Task Reject(string path,string label){var dest=Path.Combine(root,Guid.NewGuid().ToString("N"));bool failed=false;try{await MigrationService.PrepareImportAsync(path,dest,null,default);}catch(Exception e)when(e is InvalidDataException or JsonException){failed=true;}Check(failed && !File.Exists(Path.Combine(dest,"migration-pending.json")) && !File.Exists(Path.Combine(dest,"config.json")),label);}
        await Reject(Variant("group-too-long",z=>EditManifestJson(z,m=>m["Items"]![0]!["GroupName"]=new string('组',49))),"rejects overlong group before activation");
        await Reject(Variant("invalid-saved-rule",z=>EditManifestJson(z,m=>m["Settings"]!["SavedViews"]![0]!["Tab"]="Unknown")),"rejects invalid saved rules before activation");
        await Reject(Variant("duplicate-saved-rule",z=>EditManifestJson(z,m=>m["Settings"]!["SavedViews"]![1]!["Name"]="收藏参考")),"rejects duplicate rule names before activation");
        await Reject(Variant("preset-version",z=>EditManifestJson(z,m=>m["Settings"]!["ImagePresets"]!["Version"]=99)),"rejects unsupported preset format before activation");
        await Reject(Variant("preset-quality",z=>EditManifestJson(z,m=>m["Settings"]!["ImagePresets"]!["Presets"]![0]!["JpegQuality"]=0)),"rejects invalid image quality before activation");
        await Reject(Variant("preset-edge",z=>EditManifestJson(z,m=>m["Settings"]!["ImagePresets"]!["Presets"]![0]!["MaxEdge"]=0)),"rejects invalid image dimensions before activation");
        await Reject(Variant("preset-duplicate",z=>EditManifestJson(z,m=>m["Settings"]!["ImagePresets"]!["Presets"]![1]!["Name"]="交付 JPEG")),"rejects duplicate image preset names before activation");
        await Reject(Variant("preset-missing-field",z=>EditManifestJson(z,m=>m["Settings"]!["ImagePresets"]!["Presets"]![0]!.AsObject().Remove("Format"))),"rejects incomplete image presets before activation");
        await Reject(Variant("group-control",z=>EditManifestJson(z,m=>m["Items"]![0]!["GroupName"]="第一行\n第二行")),"rejects multiline group before activation");
        await Reject(Variant("tag-separator",z=>EditManifestJson(z,m=>m["Items"]![0]!["Tags"]=new JsonArray(JsonValue.Create("设计,稿")))),"rejects a single tag containing an editor separator before activation");
        await Reject(Variant("too-many-tags",z=>EditManifestJson(z,m=>m["Items"]![0]!["Tags"]=new JsonArray(Enumerable.Range(0,21).Select(i=>JsonValue.Create("标签"+i)).ToArray()))),"rejects too many tags before activation");
        await Reject(Variant("tag-too-long",z=>EditManifestJson(z,m=>m["Items"]![0]!["Tags"]=new JsonArray(JsonValue.Create(new string('签',33))))),"rejects overlong tag before activation");
        await Reject(Variant("note-too-long",z=>EditManifestJson(z,m=>m["Items"]![0]!["Note"]=new string('注',4001))),"rejects overlong note before activation");
        await Reject(Variant("traversal",z=>{using var s=new StreamWriter(z.CreateEntry("../escape.txt").Open());s.Write("bad");}),"rejects zip path traversal");
        await Reject(Variant("duplicate",z=>{using var s=new StreamWriter(z.CreateEntry("manifest.json").Open());s.Write("{}");}),"rejects duplicate ZIP entries");
        await Reject(Variant("version",z=>EditManifest(z,m=>m.Version=900)),"rejects unsupported version");
        await Reject(Variant("external-ref",z=>EditManifest(z,m=>m.Items[0].FilePath="C:/external.txt")),"rejects external material paths");
        await Reject(Variant("invalid-exclusions",z=>EditManifest(z,m=>m.Settings.ExcludedClipboardApps=new[]{"F:/bad.exe"})),"rejects invalid app exclusions before activation");
        await Reject(Variant("hash",z=>EditManifest(z,m=>m.Files[0].Sha256=new string('0',64))),"rejects tampered bytes before activation");
        await Reject(Variant("size",z=>EditManifest(z,m=>m.Files[0].Size++)),"rejects truncated/incorrect file lengths");
        await Reject(Variant("missing-fields",z=>{z.GetEntry("manifest.json")!.Delete();using var s=new StreamWriter(z.CreateEntry("manifest.json").Open());s.Write("{}");}),"rejects incomplete manifest");
        using(var cancel=new CancellationTokenSource()){
            var dest=Path.Combine(root,"cancel-import"); bool stopped=false;
            try{await MigrationService.PrepareImportAsync(package,dest,new DirectProgress(_=>cancel.Cancel()),cancel.Token);}catch(OperationCanceledException){stopped=true;}
            Check(stopped && !File.Exists(Path.Combine(dest,"migration-pending.json")) && !Directory.EnumerateFiles(dest,"*",SearchOption.AllDirectories).Any(),"cancelled import cleans staging and never activates");
        }
        var fullText=Path.Combine(root,"完整原文.txt");var originalText=new string('文',5000);File.WriteAllText(fullText,originalText);
        var textSnapshot=MigrationService.Snapshot(new[]{new MaterialItem{Type=MaterialType.Text,FilePath=fullText,TextContent="仅作预览",DisplayName="完整原文",Note="保留原文文件"}},settings);
        var textPackage=Path.Combine(root,"text-file.zip");await MigrationService.ExportAsync(textPackage,textSnapshot,null,default);
        Check(MigrationService.ReadManifest(textPackage).Files.Count==1,"text-file migration packs the original bytes instead of only its short preview");
        File.Move(fullText,fullText+".old");
        var textImported=await MigrationService.PrepareImportAsync(textPackage,Path.Combine(root,"text-new-pc"),null,default);
        var textItem=JsonSerializer.Deserialize<List<MaterialItem>>(File.ReadAllText(Path.Combine(textImported,"data.json")),MigrationService.Json)!.Single();
        Check(textItem.HasFileSource && File.ReadAllText(textItem.FilePath)==originalText && textItem.TextContent=="仅作预览" && textItem.Note=="保留原文文件","import retains the complete text source and its existing preview and note");
        var existingPackage=Path.Combine(root,"keep.zip"); File.WriteAllText(existingPackage,"preserve existing output");
        using(var cancel=new CancellationTokenSource()){
            var valid=MigrationService.Snapshot(new[]{new MaterialItem{Type=MaterialType.Other,FilePath=Path.Combine(moved,"同名素材.bin")}},settings);
            bool stopped=false;
            try{await MigrationService.ExportAsync(existingPackage,valid,new DirectProgress(message=>{if(message=="正在校验迁移包索引…")cancel.Cancel();}),cancel.Token);}catch(OperationCanceledException){stopped=true;}
            Check(stopped && File.ReadAllText(existingPackage)=="preserve existing output" && Directory.GetFiles(root,"*.partial").Length==0,"cancellation during final manifest validation cannot replace an existing archive");
        }
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
