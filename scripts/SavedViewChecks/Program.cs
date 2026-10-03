using System.Text.Json;
using Liuzhuan.Models;
using Liuzhuan.Services;

var root = Path.Combine(Path.GetTempPath(), "liuzhuan-savedview-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
void Reject(Action action, string message)
{
    var rejected = false;
    try { action(); } catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException) { rejected = true; }
    Check(rejected, message);
}
var store = new SavedViewStore(root);
Check(store.Views.Count == 0 && store.LoadError == null && !File.Exists(Path.Combine(root,"savedviews.json")), "loading an absent rule store creates no file");
var rule = new SavedMaterialView { Name="  项目封面  ", Tab="Image", GroupName=" 项目A ", Tag=" 参考 ", Keyword="  草稿  " };
store.Add(rule);
var normalized=store.Views.Single();
Check(normalized.Name=="项目封面" && normalized.GroupName=="项目A" && normalized.Tag=="参考" && normalized.Keyword=="草稿", "rules normalize user input without changing their criteria");
Check(normalized.Matches("Image"," 草稿 ","项目A","参考") && !normalized.Matches("Text","草稿","项目A","参考"), "matching combines category, group, tag and search");
Check(new SavedMaterialView {Name="Ungrouped",GroupName=""}.Matches("Recent","","",null) && !new SavedMaterialView {Name="All"}.Matches("Recent","","",null), "ungrouped rules remain distinct from all groups");
Check(SavedViewStore.Normalize(new SavedMaterialView {Name="case",Tag="   "}).Tag==null, "empty tag means all tags");
var reloaded=new SavedViewStore(root);
Check(reloaded.LoadError==null && reloaded.Views.Single()==normalized, "rules persist and reload unchanged");
var path=Path.Combine(root,"savedviews.json");
Check(!File.ReadAllText(path).Contains("Summary"), "rule JSON excludes display-only summaries");
var bytes=File.ReadAllBytes(path);
Reject(()=>store.Add(normalized with {Name="项目封面"}), "duplicate names cannot silently overwrite rules");
Check(bytes.SequenceEqual(File.ReadAllBytes(path)) && store.Views.Count==1, "rejected edits preserve disk and memory");
store.Replace("项目封面",normalized with {Name="新名称",Keyword="最终"});
Check(store.Views.Single().Name=="新名称" && new SavedViewStore(root).Views.Single().Keyword=="最终", "renaming and updating replace one existing rule");
Reject(()=>store.Replace("missing",normalized), "updating a missing rule fails visibly");
store.Add(new SavedMaterialView {Name="另一组"});
Reject(()=>store.Replace("新名称",normalized with {Name="另一组"}), "renaming to an existing name preserves both rules");
Check(store.Views.Count==2 && store.Views[0].Name=="新名称", "duplicate rename changes neither rule");
store.Remove("新名称");
Check(store.Views.Count==1 && new SavedViewStore(root).Views.Single().Name=="另一组", "deletion removes only its rule");
Reject(()=>store.Add(normalized with {Name=new string('n',41)}), "overlong names are rejected without truncation");
Reject(()=>store.Add(normalized with {Name="line\nbreak"}), "multiline names are rejected");
Reject(()=>store.Add(normalized with {Name="bad tab",Tab="Unknown"}), "unknown categories are rejected");
Reject(()=>store.Add(normalized with {Name="long search",Keyword=new string('s',257)}), "overlong search criteria are rejected");
Reject(()=>store.Add(normalized with {Name="bad group",GroupName="a\nb"}), "group validation is shared with organization edits");
Reject(()=>store.Add(normalized with {Name="bad tag",Tag="a,b"}), "one saved tag cannot contain tag separators");
Check(SavedViewStore.NormalizeAll(null).Count==0, "old and null migration rules normalize to an empty list");
for(var i=1;i<SavedViewStore.MaxViews;i++) store.Add(new SavedMaterialView {Name="rule"+i});
Check(store.Views.Count==30, "thirty rules are supported");
Reject(()=>store.Add(new SavedMaterialView {Name="overflow"}), "rule limit rejects the entire edit");
var broken=Path.Combine(root,"broken");Directory.CreateDirectory(broken);File.WriteAllText(Path.Combine(broken,"savedviews.json"),"{corrupt");
var brokenStore=new SavedViewStore(broken);
Check(brokenStore.LoadError!=null && brokenStore.Views.Count==0, "corrupt files are reported without crashing the app");
Reject(()=>brokenStore.Add(normalized), "corrupt rule stores are protected against replacement");
Check(File.ReadAllText(Path.Combine(broken,"savedviews.json"))=="{corrupt", "failed load preserves the original file for recovery");
var blocked=Path.Combine(root,"blocked");Directory.CreateDirectory(Path.Combine(blocked,"savedviews.json"));
var blockedStore=new SavedViewStore(blocked);
Reject(()=>blockedStore.Add(normalized), "write failure is visible to the caller");
Check(blockedStore.Views.Count==0 && Directory.GetFiles(blocked,"*.tmp").Length==0, "write failure retains memory and cleans only its temp file");
Console.WriteLine($"PASS {checks} saved-view checks; isolated artifacts: {root}");
