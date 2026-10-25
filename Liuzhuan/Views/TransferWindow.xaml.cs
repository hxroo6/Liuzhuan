using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Liuzhuan.Services;

namespace Liuzhuan.Views;
public partial class TransferWindow : System.Windows.Window
{
    private readonly ListCollectionView _view;
    private readonly DispatcherTimer _searchDelay;
    private readonly DispatcherTimer _policyTick;
    private TransferTaskCategory _category;
    private bool _ready;
    public TransferWindow()
    {
        InitializeComponent();
        TransferPolicyService.EnsureLoaded();
        PriorityBox.ItemsSource=new Dictionary<string,string>{{"balanced","按加入顺序"},{"send","发送优先"},{"receive","接收优先"}};
        var rates=new[]{0,1,2,5,10,20,50,100}.ToDictionary(x=>x,x=>x==0?"不限速":$"{x} MiB/s");
        SendRateBox.ItemsSource=rates;ReceiveRateBox.ItemsSource=rates;
        var policy=TransferPolicyService.Current;PriorityBox.SelectedValue=policy.Priority;SendRateBox.SelectedValue=policy.SendMiB;ReceiveRateBox.SelectedValue=policy.ReceiveMiB;AudioYieldBox.IsChecked=policy.YieldToAudio;
        _policyTick=new DispatcherTimer(DispatcherPriority.Background,Dispatcher){Interval=TimeSpan.FromMilliseconds(500)};
        _policyTick.Tick+=(_,_)=>PolicySummary.Text=TransferPolicyService.Summary+(TransferPolicyService.Error.Length>0?$" · {TransferPolicyService.Error}":"");
        PolicySummary.Text=TransferPolicyService.Summary;_policyTick.Start();
        _view=new ListCollectionView(TransferJournal.Entries) {Filter=value=>value is TransferEntry entry && Matches(entry)};
        TaskList.ItemsSource=_view;
        _searchDelay=new DispatcherTimer(DispatcherPriority.Background,Dispatcher) {Interval=TimeSpan.FromMilliseconds(150)};
        _searchDelay.Tick+=(_,_)=>{_searchDelay.Stop();RefreshResults();};
        _ready=true;
        TransferJournal.Entries.CollectionChanged+=Tasks_Changed;
        Closed+=(_,_)=>{_ready=false;_searchDelay.Stop();_policyTick.Stop();TransferJournal.Entries.CollectionChanged-=Tasks_Changed;};
        RefreshResults();
        Loaded+=(_,_)=>Utils.UiMotion.Reveal((System.Windows.FrameworkElement)Content);
    }
    private bool Matches(TransferEntry entry)=>TransferTaskQuery.Matches(entry,_category,(TransferTaskDirection)Math.Max(0,DirectionBox.SelectedIndex),SearchBox.Text);
    private void Tasks_Changed(object? sender,NotifyCollectionChangedEventArgs e) {if(_ready)UpdateSummary();}
    private void Search_Changed(object sender,TextChangedEventArgs e) {if(_ready){_searchDelay.Stop();_searchDelay.Start();}}
    private void Direction_Changed(object sender,SelectionChangedEventArgs e) {if(_ready)RefreshResults();}
    private void Filter_Click(object sender,RoutedEventArgs e) {
        if(sender is Button {Tag:string tag} && Enum.TryParse<TransferTaskCategory>(tag,out var category)){_category=category;RefreshResults();}
    }
    private void RefreshResults() {
        _view.Refresh();UpdateSummary();
        foreach(var button in new[]{AllFilter,ActiveFilter,AttentionFilter,FinishedFilter}) {
            var selected=string.Equals(button.Tag?.ToString(),_category.ToString(),StringComparison.Ordinal);
            button.Background=(Brush)FindResource(selected?"AccentBrush":"PanelLightBrush");
            button.Foreground=(Brush)FindResource(selected?"PanelBrush":"TextBrush");
            button.FontWeight=selected?FontWeights.SemiBold:FontWeights.Normal;
        }
    }
    private void UpdateSummary() {
        OverviewText.Text=TransferTaskQuery.Overview(TransferJournal.Entries).Summary;
        // This listener may precede the collection view's own replacement notification.
        var shown=TransferJournal.Entries.Count(Matches);
        ResultText.Text=$"显示 {shown} / {TransferJournal.Entries.Count} 项";
        EmptyText.Visibility=shown==0?Visibility.Visible:Visibility.Collapsed;
        EmptyText.Text=TransferJournal.Entries.Count==0?"暂无收发任务，传输文件后会显示在这里。":"当前条件没有任务，可清空搜索或切换筛选。";
    }
    private void OpenDirectory(string directory) {
        try { Services.FolderLauncher.Open(directory); }
        catch(Exception ex) {System.Windows.MessageBox.Show(this,ex.Message,"无法打开目录",System.Windows.MessageBoxButton.OK,System.Windows.MessageBoxImage.Warning);}
    }
    private void OpenReceived_Click(object sender,System.Windows.RoutedEventArgs e) {
        var directory=System.IO.Path.Combine(Services.ConfigService.GetEffectiveDataDir(),"uploads");
        System.IO.Directory.CreateDirectory(directory);OpenDirectory(directory);
    }
    private void OpenSaved_Click(object sender,System.Windows.RoutedEventArgs e) {
        if(sender is System.Windows.Controls.Button {Tag:string file}) OpenDirectory(System.IO.Path.GetDirectoryName(file)!);
    }
    private void Manager_Click(object sender,System.Windows.RoutedEventArgs e)=>new FileManagerWindow {Owner=this}.ShowDialog();
    private void Pause_Click(object sender,System.Windows.RoutedEventArgs e) {
        if(sender is System.Windows.Controls.Button {Tag:string id}) Services.TransferJournal.TogglePause(id);
    }
    private void Cancel_Click(object sender,System.Windows.RoutedEventArgs e) {
        if(sender is System.Windows.Controls.Button {Tag:string id}) Services.TransferJournal.Cancel(id);
    }
    private async void SavePolicy_Click(object sender,RoutedEventArgs e) {
        var value=new TransferPolicyOptions(PriorityBox.SelectedValue as string??"balanced",SendRateBox.SelectedValue is int send?send:0,ReceiveRateBox.SelectedValue is int receive?receive:0,AudioYieldBox.IsChecked==true);
        SavePolicyButton.IsEnabled=false;
        try {await TransferPolicyService.SaveAsync(value);PolicySummary.Text=TransferPolicyService.Summary;}
        catch {System.Windows.MessageBox.Show(this,"传输偏好未能保存，原设置仍然生效。请检查存储空间后重试。","保存失败",MessageBoxButton.OK,MessageBoxImage.Warning);}
        finally {SavePolicyButton.IsEnabled=true;}
    }
}
