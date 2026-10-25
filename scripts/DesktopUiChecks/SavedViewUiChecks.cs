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

internal static class SavedViewUiChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static void Run(MainWindow window, DataStore store, string output)
    {
        void Check(bool value,string name) { if(!value)throw new Exception(name);Console.WriteLine("PASS "+name); }
        var rules=(SavedViewStore)typeof(MainWindow).GetField("_savedViews",Private)!.GetValue(window)!;
        var text=store.Items.First(x=>x.Type==MaterialType.Text);
        var original=(text.GroupName,text.Tags,text.Note);
        var search=(TextBox)window.FindName("SearchBox");
        void Apply(SavedMaterialView rule)=>typeof(MainWindow).GetMethod("ApplySavedView",Private)!.Invoke(window,new object[]{rule});
        void Refresh()=>typeof(MainWindow).GetMethod("RefreshView",Private)!.Invoke(window,null);
        void Render(Window view,string filename,int width,int height)
        {
            var root=(FrameworkElement)view.Content;root.Measure(new Size(width,height));root.Arrange(new Rect(0,0,width,height));root.UpdateLayout();
            var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(root);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(Path.Combine(output,filename));encoder.Save(stream);
        }
        try
        {
            store.UpdateOrganization(new[]{text.Id},new(){GroupName="设计项目",Tags=new[]{"封面"},Note="reviewtoken"});
            var rule=new SavedMaterialView{Name="待审核封面",Tab="Text",GroupName="设计项目",Tag="封面",Keyword="reviewtoken"};
            rules.Add(rule);Apply(rule);
            var list=(ListView)window.FindName("TextView");
            Check(list.Items.Count==1 && list.Items.Contains(text) && search.Text=="reviewtoken", "saved views restore category, group, tag and note search together");
            Check(((TextBlock)window.FindName("SavedViewText")).Text=="待审核封面", "saved-view button identifies matching criteria");
            Render(window,"desktop-saved-view.png",380,680);
            var added=new MaterialItem{Type=MaterialType.Text,DisplayName="新加入的封面",TextContent="新素材",GroupName="设计项目",Tags=new[]{"封面"},Note="reviewtoken"};
            store.Add(added);Refresh();
            Check(list.Items.Count==2 && list.Items.Contains(added), "new matching materials appear without resaving the rule");
            store.Remove(added.Id);Refresh();
            search.Text="different";
            DesktopQueryWait.UntilApplied(window, () => list.Items.Count == 0, "manual saved-view keyword change");
            Check(((TextBlock)window.FindName("SavedViewText")).Text=="常用" && list.Items.Count==0, "manual criteria changes clear the saved-view highlight");
            Apply(rule);rules.Remove(rule.Name);Refresh();
            Check(list.Items.Count==1 && store.Items.Contains(text), "removing a saved rule leaves filters and materials intact");
            var called=0;
            var editor=new SavedViewNameWindow(rule.Summary,"",name=>{rules.Add(rule with{Name=name});called++;});
            ((TextBox)editor.FindName("NameInput")).Text=new string('n',41);
            typeof(SavedViewNameWindow).GetMethod("Save_Click",Private)!.Invoke(editor,new object[]{editor,new RoutedEventArgs()});
            Check(called==0 && !editor.Saved && ((TextBlock)editor.FindName("Status")).Text.StartsWith("未保存"), "invalid names keep the editor open without altering rules");
            var preview=new SavedViewNameWindow(rule.Summary,"待审核封面",_=>{});
            Render(preview,"desktop-save-filter-dialog.png",440,310);
            Check(((TextBlock)preview.FindName("Criteria")).Text.Contains("reviewtoken") && ((TextBox)preview.FindName("NameInput")).Text=="待审核封面", "save dialog describes the full filter rather than a fixed material list");
        }
        finally
        {
            foreach(var view in rules.Views.ToArray())rules.Remove(view.Name);
            store.UpdateOrganization(new[]{text.Id},new(){GroupName=original.GroupName,Tags=original.Tags,Note=original.Note});
            Apply(new SavedMaterialView{Name="全部"});search.Clear();Refresh();
        }
    }
}
