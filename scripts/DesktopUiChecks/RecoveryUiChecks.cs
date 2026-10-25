using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Liuzhuan.Services;
using Liuzhuan.Views;

internal static class RecoveryUiChecks
{
    public static void Run(string output)
    {
        int checks=0;void Check(bool ok,string name){if(!ok)throw new Exception(name);checks++;Console.WriteLine("PASS "+name);}
        using var store=new DataStore();
        var health=new LibraryHealthWindow(store);var staging=new ProjectStagingWindow(store);
        Check(!File.Exists(Path.Combine(store.DataRoot,"library-action-pending.json")),"opening recovery cannot schedule a restore or library switch");
        Check(!Directory.Exists(Path.Combine(store.DataRoot,"project-imports")),"opening staging cannot prepare or scan project files");
        Check(!((Button)staging.FindName("RecycleButton")).IsEnabled,"project recycling remains disabled before preview");
        ((ListBox)health.FindName("Libraries")).ItemsSource=new[]{new LibraryLocation("示例素材库 / 项目甲","当前素材库",5800,1200000,true,"")};
        ((ItemsControl)health.FindName("Versions")).ItemsSource=new[]{new LibraryIndexVersion("示例历史索引",DateTime.UtcNow,1100000)};
        ((TextBlock)staging.FindName("Summary")).Text="12 个项目 · 已引用 1.8 GB\n未引用且准备完整 420 MB · 未完成准备 32 MB\n受阻内容 0 B · 项目回收区 64 MB";
        foreach(var pair in new[]{(Window:(Window)health,Name:"recovery"),(Window:(Window)staging,Name:"project-staging")})
        foreach(var size in new[]{new Size(820,740),new Size(650,570)}){
            var root=(FrameworkElement)pair.Window.Content;root.Measure(size);root.Arrange(new Rect(size));root.UpdateLayout();
            var bitmap=new RenderTargetBitmap((int)size.Width,(int)size.Height,96,96,PixelFormats.Pbgra32);bitmap.Render(root);
            var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(output,$"{pair.Name}-{size.Width}.png")))png.Save(file);
            foreach(var button in Descendants<Button>(root).Where(x=>x.IsVisible||x.ActualHeight>0)){
                var location=button.TransformToAncestor(root).Transform(new Point());
                Check(location.Y>=0&&location.Y+button.ActualHeight<=size.Height,$"{pair.Name} action stays within {size.Width} DIP layout");
            }
        }
        health.Close();staging.Close();Console.WriteLine($"{checks} recovery UI checks passed.");
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T:DependencyObject
    {for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);if(child is T result)yield return result;foreach(var nested in Descendants<T>(child))yield return nested;}}
}
