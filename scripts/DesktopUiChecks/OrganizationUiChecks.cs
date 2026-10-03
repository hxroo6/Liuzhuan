using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Liuzhuan;
using Liuzhuan.Models;
using Liuzhuan.Services;
using Liuzhuan.Views;

internal static class OrganizationUiChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static void Run(MainWindow window, DataStore store, string output)
    {
        void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); }
        void Invoke(string name) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, null);
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
        var grid = (ListView)window.FindName("GridView");
        var search = (TextBox)window.FindName("SearchBox");
        var originals = store.Items.Select(x => (Item:x, Group:x.GroupName, Tags:x.Tags, Note:x.Note, Favorite:x.IsFavorite)).ToArray();
        var texts = store.Items.Where(x => x.Type == MaterialType.Text).Take(2).ToArray();
        var another = store.Items.First(x => !texts.Contains(x));
        void Assign(MaterialItem item, string group, string[] tags, string note) => store.UpdateOrganization(new[] {item.Id},
            new MaterialOrganizationPatch { GroupName=group, Tags=tags, Note=note });
        MaterialOrganizationPatch Patch(MaterialOrganizationWindow editor) => (MaterialOrganizationPatch)typeof(MaterialOrganizationWindow)
            .GetMethod("BuildPatch", Private)!.Invoke(editor, null)!;
        void Render(Window view, string name, int width, int height)
        {
            var root = (FrameworkElement)view.Content;
            root.Measure(new Size(width, height)); root.Arrange(new Rect(0,0,width,height)); root.UpdateLayout();
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output,name)); encoder.Save(stream);
        }
        try
        {
            Assign(texts[0], "项目A", new[]{"待处理","灵感"}, "封面搜索线索");
            Assign(texts[1], "项目B", new[]{"待处理"}, "第二条的原备注");
            Assign(another, "项目A", new[]{"参考"}, "原始参考文件");
            var single = new MaterialOrganizationWindow(new[]{texts[0]}, new[]{"项目A","项目B"}, new[]{"待处理","灵感"}, _ => 0);
            var singlePatch = Patch(single);
            Check(singlePatch.GroupName == "项目A" && singlePatch.Tags!.SequenceEqual(new[]{"待处理","灵感"}) && singlePatch.Note == "封面搜索线索",
                "single organization editor loads all existing information without truncation");
            Render(single, "desktop-organization-editor.png", 560, 650);
            ((CheckBox)single.FindName("GroupApply")).IsChecked = false;
            ((CheckBox)single.FindName("TagsApply")).IsChecked = false;
            ((TextBox)single.FindName("NoteInput")).Text = "只修改备注";
            singlePatch = Patch(single);
            Check(singlePatch.GroupName == null && singlePatch.Tags == null && singlePatch.Note == "只修改备注",
                "unchecked organization fields produce preserve-value patches");

            var batch = new MaterialOrganizationWindow(texts, new[]{"项目A","项目B"}, new[]{"待处理","灵感"}, _ => 0);
            var patch = Patch(batch);
            Check(patch.GroupName == null && patch.Tags == null && patch.Note == null && store.UpdateOrganization(texts.Select(x=>x.Id),patch) == 0,
                "batch editor preserves different existing values by default");
            ((CheckBox)batch.FindName("GroupApply")).IsChecked = true;
            ((TextBox)batch.FindName("GroupInput")).Text = " 新项目 ";
            ((CheckBox)batch.FindName("TagsApply")).IsChecked = true;
            ((TextBox)batch.FindName("TagsInput")).Text = "发布，发布; Review；review";
            patch = Patch(batch);
            var sequence = store.CurrentSequence;
            Check(patch.GroupName == "新项目" && patch.Tags!.SequenceEqual(new[]{"发布","Review"}) && patch.TagsMode == TagsMode.Add,
                "batch editor normalizes group names and delimiter-separated tags");
            store.UpdateOrganization(texts.Select(x=>x.Id),patch);
            Check(texts.All(x=>x.GroupName == "新项目" && x.Tags.Contains("待处理") && x.Tags.Contains("发布"))
                && texts[0].Note == "封面搜索线索" && texts[1].Note == "第二条的原备注" && store.CurrentSequence == sequence,
                "batch append changes chosen fields while retaining notes and LAN order");
            Render(batch, "desktop-organization-batch.png", 560, 650);
            ((RadioButton)batch.FindName("TagsReplace")).IsChecked = true;
            ((TextBox)batch.FindName("TagsInput")).Clear();
            patch = Patch(batch);
            Check(patch.TagsMode == TagsMode.Replace && patch.Tags!.Length == 0, "explicit empty replace clears tags rather than skipping the field");

            var saveCalls = 0;
            var invalid = new MaterialOrganizationWindow(new[]{texts[0]}, Array.Empty<string>(),Array.Empty<string>(), _ => ++saveCalls);
            ((TextBox)invalid.FindName("GroupInput")).Text = new string('x',49);
            typeof(MaterialOrganizationWindow).GetMethod("Save_Click",Private)!.Invoke(invalid,new object[]{invalid,new RoutedEventArgs()});
            Check(saveCalls == 0 && !invalid.Saved && ((TextBlock)invalid.FindName("Status")).Text.Contains("48"),
                "invalid organization input stays in the editor without saving");

            Assign(texts[0], "项目A",new[]{"待处理","灵感"},"封面搜索线索");
            Assign(texts[1], "项目B",new[]{"待处理"},"第二条的原备注");
            Set("_groupFilter","项目A"); Set("_tagFilter",null); Invoke("RefreshView");
            Check(grid.Items.Count == 2 && grid.Items.Contains(another) && grid.Items.Contains(texts[0]), "group filter presents only its materials");
            Set("_tagFilter","待处理"); Invoke("RefreshView");
            Check(grid.Items.Count == 1 && grid.Items.Contains(texts[0]), "group and tag filters combine without revealing hidden materials");
            search.Text = "封面搜索线索";
            Check(grid.Items.Count == 1 && grid.Items.Contains(texts[0]), "search finds notes within the active organization filters");
            Render(window,"desktop-organization-filter.png",380,680);
            search.Clear(); Set("_groupFilter",null); Set("_tagFilter","不存在的标签"); Invoke("RefreshView");
            Check(grid.Items.Count == 0 && ((TextBlock)window.FindName("EmptyTitle")).Text == "没有找到匹配素材"
                && ((Button)window.FindName("OrganizationClearButton")).Visibility == Visibility.Visible, "empty organization results show a clear-filter action");
            Invoke("ClearOrganizationFilters");
            Check(grid.Items.Count == store.Items.Count, "clearing organization filters restores the library");
            Set("_groupFilter",""); Invoke("RefreshView");
            Check(!grid.Items.Contains(texts[0]) && !grid.Items.Contains(texts[1]) && !grid.Items.Contains(another), "ungrouped is distinct from all groups");

            store.SetFavorite(another.Id,true);
            Set("_groupFilter","项目A"); Set("_tagFilter",null); Invoke("RefreshView");
            var targets = (List<MaterialItem>)typeof(MainWindow).GetMethod("CaptureCurrentClearTargets",Private)!.Invoke(window,null)!;
            Check(targets.Count == 1 && ReferenceEquals(targets[0],texts[0]), "clear current results captures only visible nonfavorites");
            typeof(MainWindow).GetMethod("ApplyCurrentClearTargets",Private)!.Invoke(window,new object[]{targets,false});
            Check(!store.Items.Contains(texts[0]) && store.Items.Contains(texts[1]) && store.Items.Contains(another), "clearing a filtered group retains hidden and favorite materials");
            store.Undo(); Invoke("RefreshView");
            Check(store.Items.Contains(texts[0]) && texts[0].GroupName == "项目A" && texts[0].Tags.Contains("灵感") && texts[0].Note == "封面搜索线索",
                "undo a filtered removal restores all organization information");
            store.SetFavorite(texts[0].Id,true); store.SetFavorite(texts[1].Id,true);
            ((RadioButton)window.FindName("TabFavorites")).IsChecked = true;
            Set("_groupFilter","项目B"); Invoke("RefreshView");
            targets = (List<MaterialItem>)typeof(MainWindow).GetMethod("CaptureCurrentClearTargets",Private)!.Invoke(window,null)!;
            typeof(MainWindow).GetMethod("ApplyCurrentClearTargets",Private)!.Invoke(window,new object[]{targets,true});
            Check(!texts[1].IsFavorite && texts[0].IsFavorite && another.IsFavorite && store.Items.Contains(texts[1]), "unfavorite current results retains hidden favorites and every source");
            Set("_menuOpen",false); Set("_toolDialogOpen",true); Invoke("CollapsePanel");
            Check((bool)typeof(MainWindow).GetField("_isExpanded",Private)!.GetValue(window)!, "a tool dialog prevents collapse even after its launching menu closes");
        }
        finally
        {
            Set("_toolDialogOpen",false); Set("_groupFilter",null); Set("_tagFilter",null);
            foreach (var original in originals)
            {
                store.UpdateOrganization(new[]{original.Item.Id},new MaterialOrganizationPatch {GroupName=original.Group,Tags=original.Tags,Note=original.Note});
                store.SetFavorite(original.Item.Id,original.Favorite);
            }
            search.Clear(); ((RadioButton)window.FindName("TabRecent")).IsChecked = true; Invoke("RefreshView"); grid.UnselectAll();
        }
    }
}
