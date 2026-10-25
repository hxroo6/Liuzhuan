using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Liuzhuan;
using Liuzhuan.Models;
using Liuzhuan.Services;

internal static class PerformanceUiChecks
{
    public static void Run(string output,int count=10000,double dpi=1)
    {
        var window=new MainWindow();
        var type=typeof(MainWindow);const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
        window.Loaded-=(RoutedEventHandler)type.GetMethod("Window_Loaded",flags)!.CreateDelegate(typeof(RoutedEventHandler),window);
        var store=(DataStore)type.GetField("_dataStore",flags)!.GetValue(window)!;
        var colors=new[]{MaterialType.Text,MaterialType.Image,MaterialType.Video,MaterialType.Audio,MaterialType.Other};
        for(int i=0;i<count;i++)store.Items.Add(new(){Id="fixture-"+i,Type=colors[i%5],DisplayName="测试素材 "+i,
            TextContent=i%5==0?"完整的文字内容 · 设计灵感 · "+i:"",IsFavorite=i%7==0,AddedTime=DateTime.Now.AddSeconds(-i)});
        var root=(FrameworkElement)window.Content;window.Content=null;
        using var source=new HwndSource(new HwndSourceParameters("Liuzhuan offscreen performance"){Width=380,Height=760,PositionX=-32000,PositionY=-32000,WindowStyle=unchecked((int)0x80000000)});
        source.RootVisual=root;
        void Pump(){var frame=new DispatcherFrame();var t=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(15)};t.Tick+=(_,_)=>{t.Stop();frame.Continue=false;};t.Start();Dispatcher.PushFrame(frame);}
        int ExpectedCount(string tab)=>tab switch {
            "Text"=>(count+4)/5,"Image"=>(count+3)/5,"Video"=>(count+2)/5,
            "Audio"=>(count+1)/5,"Other"=>count/5,"Favorites"=>(count+6)/7,_=>count};
        void Refresh(){
            type.GetMethod("RefreshView",flags)!.Invoke(window,null);
            var tab=(string)type.GetField("_currentTab",flags)!.GetValue(window)!;
            DesktopQueryWait.UntilApplied(window,()=>((ListView)window.FindName("GridView")).Items.Count==ExpectedCount(tab),
                "performance category "+tab+" with "+count+" entries");
            root.Measure(new Size(380,760));root.Arrange(new Rect(0,0,380,760));root.UpdateLayout();Pump();root.UpdateLayout();
        }
        int Count(DependencyObject node){var count=node is ListViewItem?1:0;for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)count+=Count(VisualTreeHelper.GetChild(node,i));return count;}
        void Check(bool ok,string label){if(!ok)throw new Exception(label);Console.WriteLine("PASS "+label);}
        var initialClock=Stopwatch.StartNew();Refresh();initialClock.Stop();var grid=(ListView)window.FindName("GridView");
        var realized=Count(grid);Check(realized>0 && realized<100,count+" material grid realizes fewer than 100 card containers (actual "+realized+")");
        var current=(INotifyCollectionChanged)type.GetField("_currentView",flags)!.GetValue(window)!;
        int events=0,emptyResets=0,resultResets=0,individualChanges=0;
        current.CollectionChanged+=(_,change)=>{
            events++;
            if(change.Action!=NotifyCollectionChangedAction.Reset)individualChanges++;
            else if(((IEnumerable<MaterialItem>)current).Any())resultResets++;
            else emptyResets++;
        };
        var durations=new List<double>();
        foreach(var tab in Enumerable.Range(0,4).SelectMany(_=>new[]{"Text","Recent","Image","Video","Audio","Other","Favorites"})){
            type.GetField("_currentTab",flags)!.SetValue(window,tab);events=emptyResets=resultResets=individualChanges=0;
            var clock=Stopwatch.StartNew();Refresh();clock.Stop();durations.Add(clock.Elapsed.TotalMilliseconds);
            Check(resultResets==1 && individualChanges==0 && emptyResets==(count>2000?1:0) && events==(count>2000?2:1),
                "category "+tab+" publishes exactly one completed result"+(count>2000?" after its loading reset":" reset"));
            Check(((IEnumerable<MaterialItem>)current).Count()==ExpectedCount(tab),"category "+tab+" is ready with its full expected result count");
        }
        type.GetField("_currentTab",flags)!.SetValue(window,"Text");Refresh();
        var text=(ListView)window.FindName("TextView");var container=(ListViewItem)text.ItemContainerGenerator.ContainerFromIndex(0);
        Border? FindText(DependencyObject node){if(node is Border b && b.Name=="TextCardBorder")return b;for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++){var found=FindText(VisualTreeHelper.GetChild(node,i));if(found!=null)return found;}return null;}
        var textCard=FindText(container);Check(textCard!=null && textCard.ActualWidth>260 && textCard.ActualHeight<=164,"text-only category preserves full width and compact card height");
        Check(Count(text)<60,"text-only list also virtualizes offscreen entries");
        type.GetField("_currentTab",flags)!.SetValue(window,"Recent");Refresh();grid.SelectedItem=grid.Items[0];events=0;Refresh();
        Check(events==0 && grid.SelectedItems.Count==1,"unchanged thumbnail refresh retains selection without collection notifications");
        type.GetField("_changingSelection",flags)!.SetValue(window,true);
        grid.SelectAll();
        var selectionClock=Stopwatch.StartNew();
        var selected=(List<MaterialItem>)type.GetMethod("GetSelectedItems",flags)!.Invoke(window,null)!;
        selectionClock.Stop();
        Check(selected.Count==count && selected.SequenceEqual((IEnumerable<MaterialItem>)current),"bulk selection retains every selected material in current view order");
        grid.UnselectAll();type.GetField("_changingSelection",flags)!.SetValue(window,false);
        var thumbPath=Path.Combine(output,"fixture-thumbnail.png");
        var tiny=BitmapSource.Create(2,2,96,96,PixelFormats.Bgra32,null,new byte[]{0,190,90,255,0,190,90,255,0,190,90,255,0,190,90,255},8);
        var thumbEncoder=new PngBitmapEncoder();thumbEncoder.Frames.Add(BitmapFrame.Create(tiny));using(var f=File.Create(thumbPath))thumbEncoder.Save(f);
        var imageItem=store.Items[1];imageItem.ThumbnailPath=thumbPath;
        for(int i=0;i<50 && imageItem.ThumbnailSource==null;i++)Pump();
        Check(imageItem.ThumbnailSource!=null && imageItem.ThumbnailSource.IsFrozen,"visible image decodes off the UI thread and publishes a frozen bitmap");
        grid.ScrollIntoView(store.Items.Last());Refresh();
        Check(Count(grid)<100 && imageItem.ThumbnailSource==null,"recycled offscreen image releases thumbnail memory and demand");
        grid.ScrollIntoView(store.Items.First());Refresh();
        for(int i=0;i<50 && imageItem.ThumbnailSource==null;i++)Pump();
        Check(imageItem.ThumbnailSource!=null,"scrolling back reacquires the correct recycled image");
        var getter=Stopwatch.StartNew();foreach(var item in store.Items){item.ThumbnailPath=Path.Combine(output,"missing-thumb-"+item.Id);_ =item.ThumbnailSource;}getter.Stop();
        Check(getter.ElapsedMilliseconds<1000,"unrealized thumbnail getters perform no disk reads or image decode");
        var bitmap=new RenderTargetBitmap((int)(380*dpi),(int)(760*dpi),96*dpi,96*dpi,PixelFormats.Pbgra32);bitmap.Render(root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using(var file=File.Create(Path.Combine(output,"performance-materials.png")))encoder.Save(file);
        durations.Sort();var report=new{Materials=store.Items.Count,ExportDpi=96*dpi,RealizedGridContainers=realized,
            InitialReadyMs=initialClock.Elapsed.TotalMilliseconds,SwitchMeasurement="request through final expected result and completed layout",
            MedianSwitchMs=durations[durations.Count/2],P95SwitchMs=durations[(int)(durations.Count*.95)],MaxSwitchMs=durations.Max(),ThumbnailGetterMs=getter.Elapsed.TotalMilliseconds,BulkSelectionMs=selectionClock.Elapsed.TotalMilliseconds,ThumbnailPeak=ThumbnailBudget.Peak};
        File.WriteAllText(Path.Combine(output,"performance.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine(JsonSerializer.Serialize(report));source.RootVisual=null;store.Dispose();
    }
}
