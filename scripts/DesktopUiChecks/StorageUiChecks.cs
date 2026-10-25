using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Liuzhuan.Services;
using Liuzhuan.Views;

static class StorageUiChecks
{
    public static void Run(string output)
    {
        using var store=new DataStore();var window=new StorageWindow(store,()=>{},(_,_)=>false);
        void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS "+message);}
        Check(((TextBlock)window.FindName("Summary")).Text.Contains("不扫描"),"opening storage management cannot start scanning or deleting");
        Check(!((Button)window.FindName("RemoveButton")).IsEnabled&&!((Button)window.FindName("RecycleButton")).IsEnabled,"no cleanup action before an explicit preview");
        Check(((ListBox)window.FindName("HistoryList")).SelectedItems.Count==0&&((ListBox)window.FindName("FilesList")).SelectedItems.Count==0,"cleanup never preselects user content");
        Check(((TextBox)window.FindName("Days")).Text=="30"&&((TextBox)window.FindName("Keep")).Text=="500","default retention protects recent history");
        ((TextBlock)window.FindName("Summary")).Text="索引 4.2 MB · 托管副本 1.8 GB · 缩略图 83 MB\n外部原件（只读统计）12.4 GB · 续传数据（保护）256 MB · 回收区 0 B\n收藏保护 84 项 · 来源缺失 3 项 · 历史候选 42 项 · 可回收文件 12 个";
        ((ListBox)window.FindName("HistoryList")).ItemsSource=new[]{new StorageReference("fixture","项目说明 · 本地预览","","",false,DateTime.Now,0)};
        ((ListBox)window.FindName("FilesList")).ItemsSource=new[]{new StorageFile("示例路径已隐藏 / cache.png",8192,DateTime.UtcNow.AddDays(-60).Ticks,"未引用缩略图")};
        foreach(var size in new[]{new Size(940,740),new Size(720,560)}) {
            var root=(FrameworkElement)window.Content;root.Measure(size);root.Arrange(new Rect(size));root.UpdateLayout();
            var bitmap=new RenderTargetBitmap((int)size.Width,(int)size.Height,96,96,PixelFormats.Pbgra32);bitmap.Render(root);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file=System.IO.File.Create(System.IO.Path.Combine(output,$"storage-{size.Width}.png"));png.Save(file);
            var purge=(Button)window.FindName("PurgeButton");var p=purge.TransformToAncestor(root).Transform(new Point());
            Check(p.Y>=0&&p.Y+purge.ActualHeight<=size.Height,"destructive confirmation entry remains in bounds at "+size.Width+" DIP");
        }
        var tall=System.IO.Path.Combine(output,"tall.png");var pixels=new byte[8*12000*4];Array.Fill(pixels,(byte)120);
        var image=BitmapSource.Create(8,12000,96,96,PixelFormats.Bgra32,null,pixels,32);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using(var file=System.IO.File.Create(tall))encoder.Save(file);
        var decoded=ThumbnailBudget.Decode(tall);
        Check(decoded.PixelWidth<=320&&decoded.PixelHeight<=320&&decoded.IsFrozen,"extremely tall source respects both decode dimensions and freezes the result");
        window.Close();
    }
}
