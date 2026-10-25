using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Liuzhuan.Services;
using Liuzhuan.Views;

internal static class AutomationUiChecks
{
    public static void Run(string output)
    {
        var dir=Path.Combine(output,"automation-data");Directory.CreateDirectory(dir);
        typeof(Liuzhuan.App).GetProperty(nameof(Liuzhuan.App.DataDir))!.SetValue(null,dir);
        using var store=new DataStore();using var folders=new FolderCollectionService(dir,_=>throw new Exception("UI must not start collecting"));
        var window=new AutomationWindow(store,folders,()=>{},false,_=>true);
        var root=(FrameworkElement)window.Content;
        var tabs=Find<TabControl>(root).Single();
        foreach(var tab in Enumerable.Range(0,3))
        {
            tabs.SelectedIndex=tab;root.Measure(new Size(692,690));root.Arrange(new Rect(0,0,692,690));root.UpdateLayout();
            if(tab==0) {
                typeof(AutomationWindow).GetMethod("NewRule_Click",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,new object[]{window,new RoutedEventArgs()});
                ((TextBox)window.FindName("NameInput")).Text="手机照片自动归档";((TextBox)window.FindName("GroupInput")).Text="影像参考";
                ((TextBox)window.FindName("TagsInput")).Text="参考, 待整理";((ComboBox)window.FindName("TypeInput")).SelectedValue="Image";
                root.UpdateLayout();
            }
            var bitmap=new RenderTargetBitmap(692,690,96,96,PixelFormats.Pbgra32);bitmap.Render(root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(Path.Combine(output,$"automation-tab-{tab}.png"));encoder.Save(stream);
            Console.WriteLine("PASS automation tab "+tab+" renders with production theme");
        }
        if(store.Automation.Export().Rules.Count!=0)throw new Exception("Opening and editing draft cannot save automatically");Console.WriteLine("PASS UI drafts do not change live rules before save");
        var before=store.Items.Count;typeof(AutomationWindow).GetMethod("Preview_Click",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,new object[]{window,new RoutedEventArgs()});if(store.Items.Count!=before)throw new Exception("preview mutated library");Console.WriteLine("PASS preview leaves library unchanged");
        window.Close();
        var main=new Liuzhuan.MainWindow();
        var rpc=typeof(Liuzhuan.MainWindow).GetMethod("HandleAutomationRequest",BindingFlags.NonPublic|BindingFlags.Instance)!;
        object Request(object request)=>rpc.Invoke(main,new object[]{System.Text.Json.JsonSerializer.SerializeToElement(request,AutomationStore.Json)})!;
        var current=System.Text.Json.JsonSerializer.SerializeToElement(Request(new{action="get"}),AutomationStore.Json).GetProperty("document");
        var rules=System.Text.Json.JsonSerializer.Deserialize<AutomationDocument>(current,AutomationStore.Json)!;
        rules=rules with {Rules=new(){new CollectionRule{Name="fixture",Group="参考"}}};
        Request(new{action="preview",document=rules});
        Request(new{action="save",document=rules});
        if(new AutomationStore(dir).Export().Rules.Count!=1)throw new Exception("remote rules not durably saved");Console.WriteLine("PASS production Android RPC get/preview/save persists validated rules on owner dispatcher");
    }
    private static IEnumerable<T> Find<T>(DependencyObject node) where T:DependencyObject {if(node is T item)yield return item;for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)foreach(var child in Find<T>(VisualTreeHelper.GetChild(node,i)))yield return child;}
}
