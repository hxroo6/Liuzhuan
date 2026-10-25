using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Liuzhuan.Models;
using Liuzhuan.Services;
using Liuzhuan.Views;

internal static class ProjectPackageUiChecks
{
    public static void Run(string output)
    {
        var count=0;
        void Check(bool ok,string text){if(!ok)throw new Exception(text);count++;Console.WriteLine("PASS "+text);}
        void Render(Window window,string name) {
            var root=(FrameworkElement)window.Content;root.Measure(new Size(840,720));root.Arrange(new Rect(0,0,840,720));root.UpdateLayout();
            var bitmap=new RenderTargetBitmap(840,720,96,96,PixelFormats.Pbgra32);bitmap.Render(root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream=File.Create(Path.Combine(output,name));encoder.Save(stream);
        }
        var original=Path.Combine(output,"project-original.bin");File.WriteAllText(original,"original package fixture");
        var text=new MaterialItem {Type=MaterialType.Text,TextContent="project body",DisplayName="项目文字",GroupName="项目分组",Tags=new[]{"参考"},Note="项目备注",IsFavorite=true};
        var file=new MaterialItem {Type=MaterialType.Other,FilePath=original,DisplayName="项目原件"};
        var package=Path.Combine(output,"project.liuzhuan-project.zip");
        ProjectPackageService.ExportAsync(package,ProjectPackageService.Snapshot(new[]{text,file})).GetAwaiter().GetResult();
        using var store=new DataStore();store.Add(new MaterialItem {Type=MaterialType.Text,TextContent="existing library body",DisplayName="已有文字"});
        var callbacks=0;var window=new ProjectPackageWindow(store,store.Items.ToArray(),()=>callbacks++);
        Render(window,"project-before.png");
        var preview=(Button)window.FindName("PreviewButton");var import=(Button)window.FindName("ImportButton");var rows=(ListBox)window.FindName("PreviewList");var status=(TextBlock)window.FindName("Status");
        Check(!Directory.Exists(Path.Combine(ConfigService.GetEffectiveDataDir(),"project-imports"))&&!import.IsEnabled,"opening project window neither reads nor stages a package or enables commit");
        ((TextBox)window.FindName("PackagePath")).Text=package;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        void ClickAndWait(Button button) {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if(preview.IsEnabled)return;
            var frame=new DispatcherFrame();var watch=Stopwatch.StartNew();var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(10)};
            timer.Tick+=(_,_)=>{if(preview.IsEnabled||watch.Elapsed>TimeSpan.FromSeconds(15)){timer.Stop();frame.Continue=false;}};
            timer.Start();Dispatcher.PushFrame(frame);
            if(!preview.IsEnabled)throw new Exception("Project UI did not finish: "+status.Text);
        }
        ClickAndWait(preview);
        Check(rows.Items.Count==2&&import.IsEnabled&&store.Items.Count==1,"full verified preview lists mixed materials without changing the current library");
        Check(((TextBlock)window.FindName("PreviewSummary")).Text.Contains("将新增 2 项"),"preview reports exact proposed additions before commit");
        store.Add(new MaterialItem {Type=MaterialType.Text,TextContent="concurrent arrival",DisplayName="并发新素材"});
        ClickAndWait(import);
        Check(store.Items.Count==2&&status.Text.Contains("重新校验并预览"),"a library change after preview refuses the stale commit");
        ClickAndWait(preview);ClickAndWait(import);
        Check(store.Items.Count==4&&status.Text.Contains("已保存到当前素材库"),"explicit commit merges into the existing library and confirms durable saving");
        var imported=store.Items.Single(x=>x.TextContent=="project body");
        Check(imported.IsFavorite&&imported.GroupName=="项目分组"&&imported.Tags.SequenceEqual(new[]{"参考"})&&imported.Note=="项目备注","project metadata survives the actual UI commit path");
        Check(store.Items.Any(x=>x.TextContent=="existing library body")&&store.Items.Any(x=>x.TextContent=="concurrent arrival")&&callbacks==1,"project commit retains original and concurrently collected materials");
        ClickAndWait(preview);ClickAndWait(import);
        Check(store.Items.Count==4&&status.Text.Contains("同包跳过 2 项"),"preview and reimport of the same package is idempotent on the actual view");
        Check(File.ReadAllText(original)=="original package fixture","project UI never mutates its original file");
        Render(window,"project-imported.png");window.Close();
        Console.WriteLine($"All {count} project package UI checks passed. Isolated output: {output}");
    }
}
