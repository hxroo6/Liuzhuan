using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Liuzhuan.Models;
using Liuzhuan.Services;
using Liuzhuan.Views;

internal static class DuplicateUiChecks
{
    public static void Run(string output)
    {
        void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS "+message);}
        void Render(Window window,string filename) {
            var root=(FrameworkElement)window.Content;root.Measure(new Size(940,650));root.Arrange(new Rect(0,0,940,650));root.UpdateLayout();
            var bitmap=new RenderTargetBitmap(940,650,96,96,PixelFormats.Pbgra32);bitmap.Render(root);
            var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(Path.Combine(output,filename));png.Save(stream);
        }
        var original=Path.Combine(output,"original.bin");var renamed=Path.Combine(output,"renamed.bin");
        File.WriteAllText(original,"duplicate UI synthetic fixture");File.WriteAllText(renamed,File.ReadAllText(original));
        using var store=new DataStore();
        store.Add(new MaterialItem {Type=MaterialType.Other,FilePath=original,DisplayName="original.bin"});
        store.Add(new MaterialItem {Type=MaterialType.Other,FilePath=renamed,DisplayName="renamed.bin"});
        var window=new DuplicateMaterialWindow(store,store.Items.ToArray());Render(window,"duplicates-before.png");
        var status=(TextBlock)window.FindName("SummaryText");var groups=(ListBox)window.FindName("GroupsList");
        Check(status.Text=="尚未读取文件。"&&groups.Items.Count==0,"opening duplicate tool does not start a scan");
        var start=(Button)window.FindName("StartButton");
        System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var frame=new DispatcherFrame();var clock=Stopwatch.StartNew();var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(10)};
        timer.Tick+=(_,_)=>{if(start.IsEnabled||clock.Elapsed>TimeSpan.FromSeconds(10)){timer.Stop();frame.Continue=false;}};
        timer.Start();Dispatcher.PushFrame(frame);
        Check(start.IsEnabled&&groups.Items.Count==1,"explicit scan displays its exact-content group and releases busy state");
        Check(((ListBox)window.FindName("FilesList")).Items.Count==2&&((TabItem)window.FindName("FilesTab")).IsSelected,"matching sources are selected and visible after successful scan");
        Check(File.ReadAllText(original)=="duplicate UI synthetic fixture"&&File.ReadAllText(renamed)==File.ReadAllText(original)&&store.Items.Count==2,"duplicate UI leaves source files and library indices intact");
        Render(window,"duplicates-result.png");window.Close();
        Console.WriteLine("All 4 duplicate UI checks passed. Isolated render: "+output);
    }
}
