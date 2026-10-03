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

internal static class V17UiChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static void Run(MainWindow window, DataStore store, string output)
    {
        void Check(bool value,string name) { if(!value)throw new Exception(name);Console.WriteLine("PASS "+name); }
        void Call(object target,string method) => target.GetType().GetMethod(method,Private)!.Invoke(target,new object[]{target,new RoutedEventArgs()});
        void Render(Window view,string filename,int width,int height)
        {
            var root=(FrameworkElement)view.Content;root.Measure(new Size(width,height));root.Arrange(new Rect(0,0,width,height));root.UpdateLayout();
            var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(root);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(Path.Combine(output,filename));encoder.Save(stream);
        }
        var oldEnabled=AppSettings.ClipboardMonitorEnabled;var oldApps=AppSettings.ExcludedClipboardApps;var oldDeadline=AppSettings.ClipboardPausedUntil;
        var source=new MaterialItem { Type=MaterialType.Image,DisplayName="项目封面 · 已移动的原图",FilePath=Path.Combine(output,"offline-original","项目封面.png"),Size=380000,GroupName="视觉参考",Tags=new[]{"封面"},IsFavorite=true };
        store.Add(source);
        try
        {
            AppSettings.ClipboardMonitorEnabled=true;
            AppSettings.ConfigurePrivacy(Array.Empty<string>(),DateTimeOffset.UtcNow.AddDays(40));
            var farPause=new ClipboardPrivacyWindow();
            typeof(MainWindow).GetMethod("UpdatePrivacyStatus",Private)!.Invoke(window,null);
            Check(AppSettings.IsClipboardPaused && ((Button)window.FindName("ClipboardPrivacyBanner")).Visibility==Visibility.Visible,"far persisted pause opens both status controls without overflowing native timer limits");farPause.Close();
            var privacy=new ClipboardPrivacyWindow();
            ((TextBox)privacy.FindName("AppsText")).Text="WeChat.exe\nKeePassXC.exe";Call(privacy,"Pause15_Click");
            Check(AppSettings.IsClipboardPaused && ((TextBlock)privacy.FindName("StateText")).Text.Contains("已暂停"),"privacy window persists a timed pause with a visible deadline");
            Check(AppSettings.ExcludedClipboardApps.Length==2,"privacy controls persist the entered app exclusions");
            Render(privacy,"desktop-privacy-v17.png",550,440);
            typeof(MainWindow).GetMethod("UpdatePrivacyStatus",Private)!.Invoke(window,null);
            Check(((Button)window.FindName("ClipboardPrivacyBanner")).Visibility==Visibility.Visible,"paused automatic collection remains visible on the shelf");
            ((TextBox)privacy.FindName("AppsText")).Text="F:/bad.exe";Call(privacy,"Resume_Click");
            Check(AppSettings.IsClipboardPaused && !string.IsNullOrEmpty(((TextBlock)privacy.FindName("ErrorText")).Text),"invalid privacy input cannot accidentally resume capture");
            ((TextBox)privacy.FindName("AppsText")).Text="KeePassXC.exe";Call(privacy,"Resume_Click");
            Check(!AppSettings.IsClipboardPaused && ((TextBlock)privacy.FindName("StateText")).Text.Contains("运行中"),"manual resume updates the visible state");privacy.Close();
            var repair=new SourceRepairWindow(store,_=>{},source.Id);
            var list=(ListBox)repair.FindName("SourceList");
            Check(list.Items.Cast<SourceRepairRow>().Any(x=>x.Item==source) && (list.SelectedItem as SourceRepairRow)?.Item==source,"source manager retains a missing favorite and selects the requested item");
            var matches=(Dictionary<string,SourceRelinkMatch>)typeof(SourceRepairWindow).GetField("_matches",Private)!.GetValue(repair)!;
            var snapshot=SourceRelinkService.Snapshot(new[]{source})[0];
            var plan=new SourceRelinkPlan(snapshot,Path.Combine(output,"new-project","项目封面.png"),source.Size,true);
            matches[source.Id]=new(snapshot,new[]{plan},SourceRelinkMatchStatus.Unique,"唯一候选 · 应用前确认路径与内容");
            typeof(SourceRepairWindow).GetMethod("RefreshRows",Private)!.Invoke(repair,null);
            Check(((Button)repair.FindName("ApplyButton")).IsEnabled && ((SourceRepairRow)list.SelectedItem).CandidatePath.Contains("new-project"),"candidate paths are reviewable before the explicit batch apply");
            Render(repair,"desktop-source-repair-v17.png",790,565);
            matches[source.Id]=new(snapshot,new[]{plan,plan},SourceRelinkMatchStatus.Ambiguous,"存在多个候选，请逐条选择。");
            typeof(SourceRepairWindow).GetMethod("RefreshRows",Private)!.Invoke(repair,null);
            Check(!((Button)repair.FindName("ApplyButton")).IsEnabled,"ambiguous candidates cannot enter the batch apply action");repair.Close();
            var template=(DataTemplate)window.Resources["GridCardTemplate"];
            var content=(FrameworkElement)template.LoadContent();content.DataContext=source;
            content.Measure(new Size(170,300));content.Arrange(new Rect(0,0,170,300));content.UpdateLayout();
            Check(Texts(content).Any(x=>x.Text=="原文件失效" && x.Visibility==Visibility.Visible),"missing source is visibly marked on the actual material card template");
        }
        finally
        {
            store.Remove(source.Id);AppSettings.ClipboardMonitorEnabled=oldEnabled;AppSettings.ConfigurePrivacy(oldApps,oldDeadline);
            typeof(MainWindow).GetMethod("UpdatePrivacyStatus",Private)!.Invoke(window,null);
            typeof(MainWindow).GetMethod("RefreshView",Private)!.Invoke(window,null);
        }
    }
    private static IEnumerable<TextBlock> Texts(DependencyObject parent)
    {
        if(parent is TextBlock text)yield return text;
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)foreach(var child in Texts(VisualTreeHelper.GetChild(parent,i)))yield return child;
    }
}
