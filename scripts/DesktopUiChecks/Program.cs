using System.IO;
using System.Reflection;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Liuzhuan;
using Liuzhuan.Models;
using Liuzhuan.Services;

internal static class Program
{
    // DispatcherFrame 只用于驱动动画；不能触发正式 App 的单实例锁、网络和数据目录初始化。
    private sealed class OffscreenApp : Application
    {
        protected override void OnStartup(StartupEventArgs e) { }
    }

    private static ResourceDictionary ReadProductionResources()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Liuzhuan", "App.xaml")))
            directory = directory.Parent;
        if (directory == null) throw new FileNotFoundException("Run DesktopUiChecks from within the source repository so the production App.xaml resources can be loaded.");
        var document = XDocument.Load(Path.Combine(directory.FullName, "Liuzhuan", "App.xaml"));
        var application = document.Root!;
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var dictionary = new XElement(application.Element(presentation + "Application.Resources")!.Element(presentation + "ResourceDictionary")!);
        foreach (var declaration in application.Attributes().Where(x => x.IsNamespaceDeclaration))
        {
            var value = declaration.Value.StartsWith("clr-namespace:Liuzhuan.", StringComparison.Ordinal) && !declaration.Value.Contains(";assembly=")
                ? declaration.Value + ";assembly=Liuzhuan" : declaration.Value;
            if (value != declaration.Value)
            {
                foreach (var element in dictionary.DescendantsAndSelf())
                {
                    if (element.Name.NamespaceName == declaration.Value) element.Name = XName.Get(element.Name.LocalName, value);
                    foreach (var attribute in element.Attributes().Where(x => x.Name.NamespaceName == declaration.Value).ToList())
                    {
                        element.SetAttributeValue(XName.Get(attribute.Name.LocalName, value), attribute.Value);
                        attribute.Remove();
                    }
                }
            }
            dictionary.SetAttributeValue(declaration.Name, value);
        }
        return (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
    }

    [STAThread]
    static void Main(string[] args)
    {
        var output = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(output);
        typeof(App).GetProperty(nameof(App.DataDir))!.SetValue(null, Path.Combine(output, "test-data-" + Guid.NewGuid().ToString("N")));
        Liuzhuan.Utils.Logger.Init(output);
        var app = new OffscreenApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources = ReadProductionResources();
        var window = new MainWindow(); // 离屏渲染，不启动服务，不访问用户素材库。
        var loadedHandler = typeof(MainWindow).GetMethod("Window_Loaded", BindingFlags.Instance | BindingFlags.NonPublic)!;
        window.Loaded -= (RoutedEventHandler)loadedHandler.CreateDelegate(typeof(RoutedEventHandler), window);
        var store = (DataStore)typeof(MainWindow).GetField("_dataStore", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        void Invoke(string method) => typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
        for (int i=0;i<6;i++) store.Add(new MaterialItem { Type=MaterialType.Text, DisplayName="灵感笔记 " + i, TextContent=i + " · 让素材在设备间自然流转。\n单击复制，拖出使用；常用内容加星收藏。", AddedTime=DateTime.Now.AddMinutes(-i) });
        if (args.Length > 1) store.Add(new MaterialItem { Type=MaterialType.Image, DisplayName="午后光影.png", FilePath=args[1], ThumbnailPath=args[1] });
        Invoke("RefreshView");
        var grid=(ListView)window.FindName("GridView");
        grid.SelectedItem=grid.Items[1]; var selected=grid.SelectedItem;
        var selector = grid.ItemTemplateSelector;
        Invoke("RefreshView");
        Check(ReferenceEquals(grid.SelectedItem,selected),"refresh preserves selection");
        Check(selector != null && ReferenceEquals(grid.ItemTemplateSelector, selector), "refresh reuses material template selector");
        var root=(FrameworkElement)window.Content;
        void Render(string name,double scale=1)
        {
            root.Measure(new Size(380,680)); root.Arrange(new Rect(0,0,380,680)); root.UpdateLayout();
            var bmp=new RenderTargetBitmap((int)(380*scale),(int)(680*scale),96*scale,96*scale,PixelFormats.Pbgra32);
            bmp.Render(root); var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var stream=File.Create(Path.Combine(output,name)); encoder.Save(stream);
        }
        Render("desktop-default.png");
        Image? FindThumbnail(DependencyObject parent)
        {
            if (parent is Image image && image.Name == "ThumbImage") return image;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                if (FindThumbnail(VisualTreeHelper.GetChild(parent, i)) is Image child) return child;
            return null;
        }
        if (args.Length > 1)
        {
            var imageItem = grid.Items.Cast<MaterialItem>().First(x => x.Type == MaterialType.Image);
            var imageContainer = (FrameworkElement)grid.ItemContainerGenerator.ContainerFromItem(imageItem);
            var thumbnail = FindThumbnail(imageContainer) ?? throw new Exception("Image card has no thumbnail element.");
            var originalSource = imageItem.ThumbnailSource!;
            Check(BindingOperations.IsDataBound(thumbnail, Image.SourceProperty) && ReferenceEquals(thumbnail.Source, originalSource),
                "thumbnail renders through its live source binding");
            var replacementSource = ((BitmapSource)originalSource).Clone(); replacementSource.Freeze();
            grid.SelectedItem = imageItem;
            imageItem.ThumbnailSource = replacementSource;
            Pump(20); Invoke("RefreshView"); root.UpdateLayout();
            Check(ReferenceEquals(thumbnail.Source, replacementSource) && BindingOperations.IsDataBound(thumbnail, Image.SourceProperty),
                "deferred thumbnail change reaches the existing image binding");
            Check(ReferenceEquals(grid.ItemContainerGenerator.ContainerFromItem(imageItem), imageContainer) && ReferenceEquals(grid.SelectedItem, imageItem),
                "thumbnail refresh preserves container and selected material");
            imageItem.ThumbnailSource = originalSource;
            grid.SelectedItem = selected;
            Pump(20);
        }
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

        // 最后执行时钟测试，既保留前面的静态截图，也避免无窗口的 App 启动真实功能。
        const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        const BindingFlags privateStatic = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        var motionType = typeof(MainWindow).Assembly.GetType("Liuzhuan.Utils.UiMotion", throwOnError: true)!;
        var motionOverrideProperty = motionType.GetProperty("EnabledOverride", privateStatic);
        var motionOverrideField = motionType.GetField("EnabledOverride", privateStatic);
        void SetMotionOverride(bool? enabled)
        {
            if (motionOverrideProperty != null) motionOverrideProperty.SetValue(null, enabled);
            else if (motionOverrideField != null) motionOverrideField.SetValue(null, enabled);
            else throw new MissingMemberException(motionType.FullName, "EnabledOverride");
        }
        void Pump(int milliseconds)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); }
        }
        void SetField(string name, object value) => typeof(MainWindow).GetField(name, privateInstance)!.SetValue(window, value);
        bool IsExpanded() => (bool)typeof(MainWindow).GetField("_isExpanded", privateInstance)!.GetValue(window)!;
        SetField("_expandedLeft", 1000d);
        SetField("_collapsedLeft", 1372d);
        SetField("_isPinned", false);
        SetField("_menuOpen", false);
        SetField("_isLeftAnchored", false);
        var panel = (FrameworkElement)window.FindName("MainPanel");
        var panelColumn = (ColumnDefinition)window.FindName("PanelColumn");
        var panelTranslate = (TranslateTransform)typeof(MainWindow).GetField("_panelTranslate", privateInstance)!.GetValue(window)!;
        try
        {
            SetMotionOverride(true);
            // 不让 Dispatcher 先渲染：排队的收起动画必须被同步复位永久取消。
            // 这与拖窗开始或同一帧反向操作时的调用顺序相同。
            var queuedTranslate = new TranslateTransform();
            var obsoleteCompletions = 0;
            var resetCompletions = 0;
            Liuzhuan.Utils.UiMotion.Animate(queuedTranslate, TranslateTransform.XProperty, 372, 200,
                completed: () => obsoleteCompletions++);
            Liuzhuan.Utils.UiMotion.Animate(queuedTranslate, TranslateTransform.XProperty, 0, 0,
                completed: () => resetCompletions++);
            Check(Math.Abs(queuedTranslate.X) < 0.1 && resetCompletions == 1,
                "immediate reset settles a queued animation synchronously");
            Pump(60);
            Check(Math.Abs(queuedTranslate.X) < 0.1,
                "queued animation cannot revive after an immediate reset");
            Pump(320);
            Check(Math.Abs(queuedTranslate.X) < 0.1 && obsoleteCompletions == 0 && resetCompletions == 1,
                "cancelled queued animation cannot change the value or invoke its completion");

            // 失效范围必须精确到对象和属性，否则复位 X 会误杀 Y 或别的控件的动画。
            var otherTranslate = new TranslateTransform();
            var independentCompletions = 0;
            Liuzhuan.Utils.UiMotion.Animate(queuedTranslate, TranslateTransform.XProperty, 100, 120);
            Liuzhuan.Utils.UiMotion.Animate(queuedTranslate, TranslateTransform.YProperty, 24, 120,
                completed: () => independentCompletions++);
            Liuzhuan.Utils.UiMotion.Animate(otherTranslate, TranslateTransform.XProperty, 48, 120,
                completed: () => independentCompletions++);
            Liuzhuan.Utils.UiMotion.Animate(queuedTranslate, TranslateTransform.XProperty, 0, 0);
            Pump(320);
            Check(Math.Abs(queuedTranslate.X) < 0.1 && Math.Abs(queuedTranslate.Y - 24) < 0.1
                && Math.Abs(otherTranslate.X - 48) < 0.1 && independentCompletions == 2,
                "animation cancellation preserves other properties and other objects");

            Invoke("CollapsePanel"); Invoke("ExpandPanel");
            Pump(60);
            Check(IsExpanded() && window.Width == 380 && panel.Visibility == Visibility.Visible && Math.Abs(panelTranslate.X) < 0.1,
                "same-frame collapse and expansion keep the main panel visible");
            Pump(320);
            Check(IsExpanded() && panelColumn.Width.Value == 372 && Math.Abs(panelTranslate.X) < 0.1,
                "same-frame reversal cannot leave an expanded window with offscreen content");

            Invoke("CollapsePanel"); Pump(60);
            var collapsingX = panelTranslate.X;
            Check(collapsingX > 0 && collapsingX < 372, "collapse clock reaches an intermediate position");
            Invoke("ExpandPanel");
            Check(Math.Abs(panelTranslate.X - collapsingX) < 1, $"collapse reversal continues from current position ({collapsingX:F2} -> {panelTranslate.X:F2})");
            Pump(40);
            var expandingX = panelTranslate.X;
            Check(expandingX < collapsingX, "reversed panel travels toward the expanded position");
            Invoke("CollapsePanel");
            Check(Math.Abs(panelTranslate.X - expandingX) < 1, "second reversal preserves visual continuity");
            Pump(400);
            Check(!IsExpanded() && window.Width == 8 && window.Left == 1372 && panel.Visibility == Visibility.Collapsed && panelColumn.Width.Value == 0,
                "collapse-expand-collapse ends at the latest collapsed target");

            root.Measure(new Size(8,680)); root.Arrange(new Rect(0,0,8,680)); root.UpdateLayout();
            var trigger = (FrameworkElement)window.FindName("TriggerBar");
            var triggerPosition = trigger.TranslatePoint(new Point(), root);
            Check(trigger.Visibility == Visibility.Visible && trigger.ActualWidth > 0 && triggerPosition.X >= -0.1 && triggerPosition.X + trigger.ActualWidth <= 8.1,
                "collapsed trigger is visible inside the eight-pixel window");
            var collapsedShot = new RenderTargetBitmap(8,680,96,96,PixelFormats.Pbgra32);
            collapsedShot.Render(root);
            var collapsedEncoder = new PngBitmapEncoder(); collapsedEncoder.Frames.Add(BitmapFrame.Create(collapsedShot));
            using (var collapsedOutput = File.Create(Path.Combine(output,"collapsed-state.png"))) collapsedEncoder.Save(collapsedOutput);

            Invoke("ExpandPanel"); Pump(60);
            Invoke("CollapsePanel"); Pump(40);
            Invoke("ExpandPanel"); Pump(400);
            Check(IsExpanded() && window.Width == 380 && window.Left == 1000 && panel.Visibility == Visibility.Visible && panelColumn.Width.Value == 372 && Math.Abs(panelTranslate.X) < 0.1,
                "expand-collapse-expand ignores obsolete collapse completions");

            // DragMove 会继续泵消息。用手动驱动的 Dispatcher 验证拖窗期间的状态，
            // 不调用原生 DragMove / SnapToEdge，也不创建真实窗口或启动热区轮询。
            var originalCollapseTimer = typeof(MainWindow).GetField("_collapseTimer", privateInstance)!.GetValue(window);
            var originalHotspotTimer = typeof(MainWindow).GetField("_hotspotTimer", privateInstance)!.GetValue(window);
            var dragCollapseTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
            var dragHotspotTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
            SetField("_collapseTimer", dragCollapseTimer);
            SetField("_hotspotTimer", dragHotspotTimer);
            try
            {
                foreach (var delayBeforeDrag in new[] { 0, 60 })
                {
                    var phase = delayBeforeDrag == 0 ? "queued" : "running";
                    Invoke("CollapsePanel");
                    if (delayBeforeDrag > 0) Pump(delayBeforeDrag);
                    // 模拟用户已经拖离旧停靠位置，强制展开不能把窗口跳回旧坐标。
                    window.Left = 920;
                    dragCollapseTimer.Start(); dragHotspotTimer.Start();
                    Invoke("BeginWindowDrag");
                    Check((bool)typeof(MainWindow).GetField("_isWindowDragging", privateInstance)!.GetValue(window)!
                        && !dragCollapseTimer.IsEnabled && !dragHotspotTimer.IsEnabled,
                        $"drag beginning with a {phase} animation suspends collapse and hotspot timers");
                    Check(IsExpanded() && window.Width == 380 && window.Left == 920 && panel.Visibility == Visibility.Visible
                        && panelColumn.Width.Value == 372 && Math.Abs(panelTranslate.X) < 0.1,
                        $"drag beginning with a {phase} animation restores content without moving the window");
                    Invoke("CollapsePanel");
                    Pump(60);
                    Check(IsExpanded() && window.Width == 380 && window.Left == 920 && panel.Visibility == Visibility.Visible
                        && Math.Abs(panelTranslate.X) < 0.1,
                        $"drag ignores collapse requests and the obsolete {phase} animation");
                    Pump(320);
                    Check(IsExpanded() && window.Width == 380 && panelColumn.Width.Value == 372 && Math.Abs(panelTranslate.X) < 0.1,
                        $"drag stays fully visible after the obsolete {phase} animation would have completed");
                    SetField("_isWindowDragging", false);
                    window.Left = 1000;
                }
            }
            finally
            {
                dragCollapseTimer.Stop(); dragHotspotTimer.Stop();
                typeof(MainWindow).GetField("_collapseTimer", privateInstance)!.SetValue(window, originalCollapseTimer);
                typeof(MainWindow).GetField("_hotspotTimer", privateInstance)!.SetValue(window, originalHotspotTimer);
                SetField("_isWindowDragging", false);
            }
            Render("desktop-drag-recovered.png", 1.5);

            // 系统禁用动画时必须同步完成布局；中途禁用也不能让旧动画回调再次缩窗。
            Invoke("CollapsePanel"); Pump(40);
            SetMotionOverride(false);
            Invoke("ExpandPanel");
            Check(IsExpanded() && window.Width == 380 && panel.Visibility == Visibility.Visible && Math.Abs(panelTranslate.X) < 0.1,
                "disabling motion settles an interrupted expansion immediately");
            Pump(400);
            Check(IsExpanded() && window.Width == 380 && panelColumn.Width.Value == 372,
                "disabled motion invalidates pending collapse completion");
            Invoke("CollapsePanel");
            Check(!IsExpanded() && window.Width == 8 && panel.Visibility == Visibility.Collapsed && panelColumn.Width.Value == 0,
                "disabled motion collapses synchronously");
            Invoke("ExpandPanel");
            Check(IsExpanded() && window.Width == 380 && panel.Visibility == Visibility.Visible && panelColumn.Width.Value == 372 && Math.Abs(panelTranslate.X) < 0.1,
                "disabled motion expands synchronously");

            SetField("_menuOpen", true); Invoke("CollapsePanel");
            Check(IsExpanded(), "open menu prevents collapse");
            SetField("_menuOpen", false); SetField("_isLeftAnchored", true); Invoke("CollapsePanel");
            Check(IsExpanded(), "left anchored panel stays expanded");
            SetField("_isLeftAnchored", false);
            Check(typeof(MainWindow).GetField("_hotspotTimer", privateInstance)!.GetValue(window) == null,
                "animation checks never initialize desktop hotspot or network startup");
        }
        finally { SetMotionOverride(null); }
        store.Dispose();
        Console.WriteLine("Rendered to " + output);
    }
}
