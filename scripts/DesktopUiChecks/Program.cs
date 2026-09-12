using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Liuzhuan;
using Liuzhuan.Models;
using Liuzhuan.Services;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var output = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(output);
        typeof(App).GetProperty(nameof(App.DataDir))!.SetValue(null, Path.Combine(output, "test-data-" + Guid.NewGuid().ToString("N")));
        Liuzhuan.Utils.Logger.Init(output);
        var app = new App(); app.InitializeComponent();
        var window = new MainWindow(); // 离屏渲染，不启动服务，不访问用户素材库。
        var store = (DataStore)typeof(MainWindow).GetField("_dataStore", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        void Invoke(string method) => typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
        for (int i=0;i<6;i++) store.Add(new MaterialItem { Type=MaterialType.Text, DisplayName="灵感笔记 " + i, TextContent=i + " · 让素材在设备间自然流转。\n单击复制，拖出使用；常用内容加星收藏。", AddedTime=DateTime.Now.AddMinutes(-i) });
        if (args.Length > 1) store.Add(new MaterialItem { Type=MaterialType.Image, DisplayName="午后光影.png", FilePath=args[1], ThumbnailPath=args[1] });
        Invoke("RefreshView");
        var grid=(ListView)window.FindName("GridView");
        grid.SelectedItem=grid.Items[1]; var selected=grid.SelectedItem;
        Invoke("RefreshView");
        Check(ReferenceEquals(grid.SelectedItem,selected),"refresh preserves selection");
        void FillImages(DependencyObject parent)
        {
            if(parent is Image image && image.DataContext is MaterialItem item) image.Source=item.ThumbnailSource;
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++) FillImages(VisualTreeHelper.GetChild(parent,i));
        }
        var root=(FrameworkElement)window.Content;
        void Render(string name,double scale=1)
        {
            root.Measure(new Size(380,680)); root.Arrange(new Rect(0,0,380,680)); root.UpdateLayout(); FillImages(root); root.UpdateLayout();
            var bmp=new RenderTargetBitmap((int)(380*scale),(int)(680*scale),96*scale,96*scale,PixelFormats.Pbgra32);
            bmp.Render(root); var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var stream=File.Create(Path.Combine(output,name)); encoder.Save(stream);
        }
        Render("desktop-default.png");
        var first=(FrameworkElement)grid.ItemContainerGenerator.ContainerFromIndex(0);
        var second=(FrameworkElement)grid.ItemContainerGenerator.ContainerFromIndex(1);
        Check(Math.Abs(first.TranslatePoint(new Point(),root).Y-second.TranslatePoint(new Point(),root).Y)<1,"two columns with scrollbar");
        ((Button)window.FindName("PinBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(((Button)window.FindName("PinBtn")).Content.ToString()=="已固定","pin feedback");
        Invoke("CollapsePanel");
        Check((bool)typeof(MainWindow).GetField("_isExpanded",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(window)!,"pin prevents collapse");
        ((Button)window.FindName("PinBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Render("desktop-150-percent.png",1.5);
        var search=(TextBox)window.FindName("SearchBox"); search.Text="不会匹配的关键词";
        Check(grid.Items.Count==0,"search filters library");
        Check(((TextBlock)window.FindName("EmptyTitle")).Text=="没有找到匹配素材","search empty state");
        Render("desktop-empty-search.png");
        search.Clear(); Check(grid.Items.Count>=6,"clearing search restores library");
        ((RadioButton)window.FindName("TabFavorites")).IsChecked=true;
        Check(((TextBlock)window.FindName("EmptyTitle")).Text=="还没有收藏","favorites empty state");
        Render("desktop-empty-favorites.png");
        ((RadioButton)window.FindName("TabText")).IsChecked=true;
        Check(((ListView)window.FindName("TextView")).Visibility==Visibility.Visible,"text reading view");
        Render("desktop-text.png");
        if(args.Length>1)
        {
            var previews=new List<MaterialItem> {new() {Type=MaterialType.Image,FilePath=args[1],DisplayName="第一张"}, new() {Type=MaterialType.Image,FilePath=args[1],DisplayName="第二张"}};
            var preview=new Liuzhuan.Views.QuickPreviewWindow(previews,0,_=>{});
            var move=preview.GetType().GetMethod("Move",BindingFlags.NonPublic|BindingFlags.Instance)!;
            move.Invoke(preview,new object[]{1});move.Invoke(preview,new object[]{-1});
            var content=(ContentControl)preview.GetType().GetField("_content",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(preview)!;
            Check(content.Content is ScrollViewer,"image preview navigation reuses image without parent conflict");
            var previewRoot=(FrameworkElement)preview.Content;previewRoot.Measure(new Size(720,550));previewRoot.Arrange(new Rect(0,0,720,550));previewRoot.UpdateLayout();
            var previewImage=(Image)preview.GetType().GetField("_image",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(preview)!;
            Check(previewImage.ActualHeight>300,"preview adapts image to available content area");
            var shot=new RenderTargetBitmap(720,550,96,96,PixelFormats.Pbgra32);shot.Render(previewRoot);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(shot));using(var outputFile=File.Create(Path.Combine(output,"quick-preview.png"))) encoder.Save(outputFile);
        }
        TransferJournal.Report(new("preview-active","旅行照片.png","手机 → 电脑",6*1048576,12*1048576,"传输中","",2*1048576));
        TransferJournal.Report(new("preview-failed","设计稿.pdf","电脑 → 手机",1048576,8*1048576,"失败","连接中断，请在手机重试",0));
        var tasksWindow=new Liuzhuan.Views.TransferWindow();
        var tasksRoot=(FrameworkElement)tasksWindow.Content;tasksRoot.Measure(new Size(480,500));tasksRoot.Arrange(new Rect(0,0,480,500));tasksRoot.UpdateLayout();
        var tasksShot=new RenderTargetBitmap(480,500,96,96,PixelFormats.Pbgra32);tasksShot.Render(tasksRoot);var tasksEncoder=new PngBitmapEncoder();tasksEncoder.Frames.Add(BitmapFrame.Create(tasksShot));using(var tasksOutput=File.Create(Path.Combine(output,"transfer-tasks.png"))) tasksEncoder.Save(tasksOutput);
        store.Dispose();
        Console.WriteLine("Rendered to " + output);
    }
}
