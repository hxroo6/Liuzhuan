using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Liuzhuan.Models;
using Liuzhuan.Services;
using Liuzhuan.Views;

internal static class IntegrityUiChecks
{
    public static void Run(DataStore store,string output)
    {
        void Check(bool condition,string text) {if(!condition)throw new Exception(text);Console.WriteLine("PASS "+text);}
        MaterialItem Fixture(string name,bool baseline=false)
        {
            var path=Path.Combine(output,"integrity-ui-"+name+".bin");File.WriteAllText(path,"fixture "+name);
            return store.Add(new MaterialItem {DisplayName=name,Type=MaterialType.Other,FilePath=path,
                IntegrityBaseline=baseline ? new(new string('A',64),0,DateTimeOffset.UtcNow,path,MaterialType.Other) : null});
        }
        var items=new[]{Fixture("上次正常",true),Fixture("内容变化",true),Fixture("来源缺失"),Fixture("读取失败"),Fixture("尚未记录"),Fixture("尚未检查",true)};
        int confirmations=0;
        var window=new MaterialIntegrityWindow(store,items,(_,_)=>{confirmations++;return false;});
        var list=(ListBox)window.FindName("IntegrityList");
        var check=(Button)window.FindName("CheckButton");var record=(Button)window.FindName("RecordButton");var clear=(Button)window.FindName("ClearButton");
        Check(list.Items.Count==6&&items.All(x=>x.IntegrityStatus is MaterialIntegrityStatus.NotChecked or MaterialIntegrityStatus.Unrecorded),"opening integrity window lists scope without hashing");
        Check(!record.IsEnabled&&!clear.IsEnabled&&check.IsEnabled,"initial integrity buttons require explicit check and selection");
        Check(ReferenceEquals(check.Style.BasedOn,Application.Current.Resources["ActionButtonStyle"]),"integrity buttons reuse production dark style");
        list.SelectedItem=list.Items[5];Check(clear.IsEnabled&&!record.IsEnabled,"existing baseline may be removed but no unread digest can be recorded");list.SelectedItems.Clear();
        var statuses=new[]{MaterialIntegrityStatus.Normal,MaterialIntegrityStatus.Changed,MaterialIntegrityStatus.Missing,MaterialIntegrityStatus.ReadFailed,MaterialIntegrityStatus.Unrecorded};
        var results=IntegrityService.Snapshot(items.Take(5)).Select((source,index)=>new IntegrityResult(source,statuses[index],index<2?new string('B',64):null,0,default,DateTimeOffset.UtcNow,"离屏展示结果；仅代表上次读取。")).ToArray();
        store.ApplyIntegrityResults(results);
        var resultCache=(Dictionary<string,IntegrityResult>)typeof(MaterialIntegrityWindow).GetField("_results",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
        foreach(var result in results)resultCache[result.Source.Id]=result;
        typeof(MaterialIntegrityWindow).GetMethod("RefreshRows",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null);
        list.SelectedItem=list.Items.Cast<IntegrityRow>().Single(x=>x.Item.Id==items[0].Id);
        var baselineBefore=items[0].IntegrityBaseline;var bytesBefore=File.ReadAllBytes(items[0].FilePath);
        Check(record.IsEnabled&&clear.IsEnabled,"completed readable selection enables explicit baseline actions");
        record.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(confirmations==2&&items[0].IntegrityBaseline==baselineBefore,"canceling actual record and clear button handlers leaves baseline untouched");
        Check(File.ReadAllBytes(items[0].FilePath).SequenceEqual(bytesBefore),"canceling baseline confirmations leaves source bytes untouched");
        list.SelectedItems.Clear();
        VirtualizingPanel.SetIsVirtualizing(list,false);
        var root=(FrameworkElement)window.Content;
        IEnumerable<T> Descendants<T>(DependencyObject parent) where T:DependencyObject
        {for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);if(child is T found)yield return found;foreach(var next in Descendants<T>(child))yield return next;}}
        void Render(string name,double scale)
        {
            root.Measure(new Size(852,602));root.Arrange(new Rect(0,0,852,602));root.UpdateLayout();
            var bitmap=new RenderTargetBitmap((int)(852*scale),(int)(602*scale),96*scale,96*scale,PixelFormats.Pbgra32);bitmap.Render(root);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(Path.Combine(output,name));encoder.Save(stream);
        }
        Render("integrity-100.png",1);Render("integrity-150.png",1.5);
        var labels=Descendants<TextBlock>(root).Where(x=>Grid.GetColumn(x)==1&&items.Any(item=>item.IntegrityStatusLabel==x.Text)).ToArray();
        foreach(var status in statuses.Append(MaterialIntegrityStatus.NotChecked))
        {
            var item=items.Single(x=>x.IntegrityStatus==status);var label=labels.Single(x=>x.Text==item.IntegrityStatusLabel);
            var resource=status==MaterialIntegrityStatus.Normal ? "AccentBrush" : status is MaterialIntegrityStatus.Changed or MaterialIntegrityStatus.Missing or MaterialIntegrityStatus.ReadFailed ? "WarmBrush" : "TextDimBrush";
            Check(((SolidColorBrush)label.Foreground).Color==((SolidColorBrush)Application.Current.Resources[resource]).Color,"integrity status color: "+status);
        }
        Check(items[0].IntegrityStatusLabel=="上次检查正常"&&items[0].IntegrityDetail.Contains("检查时间"),"integrity UI labels and details present snapshot boundary");
        Check(File.Exists(Path.Combine(output,"integrity-100.png"))&&File.Exists(Path.Combine(output,"integrity-150.png")),"real integrity XAML renders at 100 and 150 percent");
        window.Close();
    }
}
