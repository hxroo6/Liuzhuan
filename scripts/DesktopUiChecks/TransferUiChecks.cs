using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Liuzhuan.Services;
using Liuzhuan.Views;

internal static class TransferUiChecks
{
    static void Check(bool ok,string text) {if(!ok)throw new Exception(text);Console.WriteLine("PASS "+text);}
    static IEnumerable<T> Children<T>(DependencyObject root) where T:DependencyObject {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) {var child=VisualTreeHelper.GetChild(root,i);if(child is T item)yield return item;foreach(var nested in Children<T>(child))yield return nested;}
    }
    static FrameworkElement Render(Window window,string path,int width,int height) {
        var root=(FrameworkElement)window.Content;root.Measure(new Size(width,height));root.Arrange(new Rect(0,0,width,height));root.UpdateLayout();
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(root);
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(path))png.Save(stream);return root;
    }
    public static void Run(string output) {
        using var control=TransferJournal.RegisterControl("ui-running",()=>{});
        TransferJournal.Report(new("ui-running","正在传输的演示视频.mp4","电脑 → 手机",1048576,8388608,"传输中","",65536));
        TransferJournal.Report(new("ui-saved","已接收的演示照片.png","手机 → 电脑",2097152,2097152,"已接收","",0,Path.Combine(output,"uploads","示例.png")));
        var window=new TransferWindow();var root=Render(window,Path.Combine(output,"transfer-wide.png"),800,620);
        Check(window.Width>=800 && window.MinWidth>=600,"transfer window offers expanded resizable space");
        var buttons=Children<Button>(root).Where(x=>x.IsVisible || x.Visibility==Visibility.Visible).ToList();
        Check(buttons.Any(x=>Equals(x.Content,"打开接收目录")) && buttons.Any(x=>Equals(x.Content,"文件管理器设置")),"receive directory and manager configuration are visible");
        Check(buttons.Any(x=>Equals(x.Content,"暂停")) && buttons.Any(x=>Equals(x.Content,"取消")),"live file task offers pause and cancel controls");
        Check(buttons.Any(x=>Equals(x.Content,"打开保存位置") && x.Visibility==Visibility.Visible),"completed received task opens its own saved location");
        TransferJournal.TogglePause("ui-running");root.UpdateLayout();
        Render(window,Path.Combine(output,"transfer-paused.png"),800,620);
        Check(Children<Button>(root).Any(x=>Equals(x.Content,"继续") && x.Visibility==Visibility.Visible),"paused task shows continue on the actual WPF view");
        var manager=new FileManagerWindow();var managerRoot=Render(manager,Path.Combine(output,"file-manager.png"),620,380);
        Check(Children<Button>(managerRoot).Any(x=>Equals(x.Content,"保存")) && manager.Height>=400,"manager dialog renders its settings and save button");
        TransferJournal.EndControl("ui-running");Console.WriteLine("All 6 transfer UI checks passed.");
    }
}
