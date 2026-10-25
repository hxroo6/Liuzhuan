using System.Windows;
using Liuzhuan.Services;
using Liuzhuan.Utils;

namespace Liuzhuan.Views;

public partial class StorageWindow : Window
{
    private readonly DataStore _store;
    private readonly Func<string,string,bool> _confirm;
    private readonly Action _changed;
    private CancellationTokenSource? _scan;
    private StoragePreview? _preview;
    private bool _policyInvalid;
    private bool _working;
    private readonly string _root;
    private string Root=>_root;
    private void CheckTarget(){if(ConfigService.GetEffectiveDataDir()!=Root||_store.LoadFailed)throw new InvalidOperationException("素材库已变化或不可读取，请关闭后重新打开容量管理。");}
    public StorageWindow(DataStore store,Action changed,Func<string,string,bool>? confirm=null)
    {
        InitializeComponent();_store=store;_changed=changed;_root=ConfigService.GetEffectiveDataDir();
        _confirm=confirm??((message,title)=>MessageBox.Show(this,message,title,MessageBoxButton.OKCancel,MessageBoxImage.Warning)==MessageBoxResult.OK);
        try{var p=StorageRetention.Load(Root);Days.Text=p.Days.ToString();Keep.Text=p.KeepNewest.ToString();}
        catch{_policyInvalid=true;Status.Text="保留策略损坏，原文件已保留；可预览，不能覆盖该配置。";}
        Loaded+=(_,_)=>{MaxHeight=Math.Max(MinHeight,SystemParameters.WorkArea.Height-32);MaxWidth=Math.Max(MinWidth,SystemParameters.WorkArea.Width-32);Height=Math.Min(Height,MaxHeight);Width=Math.Min(Width,MaxWidth);};
        Closed+=(_,_)=>_scan?.Cancel();
        Closing+=(_,e)=>{if(_working){e.Cancel=true;Status.Text="正在完成回收区操作，请稍候…";}};
        void Invalidate(){_preview=null;HistoryList.ItemsSource=FilesList.ItemsSource=null;SetBusy(false);Status.Text="保留条件已变化，请重新扫描后再操作。";}
        Days.TextChanged+=(_,_)=>Invalidate();Keep.TextChanged+=(_,_)=>Invalidate();
        HistoryList.SelectionChanged+=(_,_)=>SetBusy(_scan!=null||_working);
        FilesList.SelectionChanged+=(_,_)=>SetBusy(_scan!=null||_working);
    }
    private (int Days,int Keep) Policy()
    {if(!int.TryParse(Days.Text,out var days)||days is <1 or >3650||!int.TryParse(Keep.Text,out var keep)||keep is <0 or >100000)throw new ArgumentException("天数需1–3650，最新条数需0–100000。");return(days,keep);}
    private async Task PreviewAsync()
    {
        if(_scan!=null)return;
        CheckTarget();
        var p=Policy();var snapshot=StorageMaintenance.Snapshot(_store.Items);var undo=StorageMaintenance.Snapshot(_store.RetainedForUndo);
        using var cancel=new CancellationTokenSource();_scan=cancel;SetBusy(true);Status.Text="正在检查本库文件；可随时取消…";
        try{
            var result=await StorageMaintenance.PreviewAsync(Root,_store.CurrentSequence,snapshot,undo,p.Days,p.Keep,cancel.Token);
            if(cancel.IsCancellationRequested||!IsLoaded)return;
            _preview=result;HistoryList.ItemsSource=result.History;FilesList.ItemsSource=result.Files;
            TrashList.ItemsSource=result.TrashDetails;
            string Size(long n)=>FileClassifier.FormatSize(n);
            Summary.Text=$"索引 {Size(result.IndexBytes)} · 托管副本 {Size(result.ManagedBytes)} · 缩略图 {Size(result.ThumbnailBytes)}\n外部原件（只读统计）{Size(result.ExternalBytes)} · 续传数据（保护）{Size(result.RecoveryBytes)} · 回收区 {Size(result.TrashBytes)}\n收藏保护 {result.Favorites} 项 · 来源缺失 {result.Missing} 项 · 历史候选 {result.History.Length} 项 · 可回收文件 {result.Files.Length} 个";
            Status.Text=result.Warnings.Length>0?string.Join("\n",result.Warnings):"统计为文件逻辑大小，没有预选条目。仅移除索引不删除原件；回收可恢复，永久清空后才释放空间。HEIC原件、撤回来源、在用任务与链接路径均受保护。";
        }catch(OperationCanceledException){Status.Text="扫描已取消，未修改文件。";}finally{_scan=null;SetBusy(false);}
    }
    private void SetBusy(bool busy)
    {Days.IsEnabled=Keep.IsEnabled=PreviewButton.IsEnabled=SavePolicyButton.IsEnabled=RestoreButton.IsEnabled=PurgeButton.IsEnabled=!busy;RemoveButton.IsEnabled=!busy&&_preview!=null&&HistoryList.SelectedItems.Count>0;RecycleButton.IsEnabled=!busy&&_preview?.Complete==true&&FilesList.SelectedItems.Count is >0 and <=200;
        if(_preview!=null){RestoreButton.IsEnabled=PurgeButton.IsEnabled=!busy&&_preview.Complete&&_preview.TrashDetails.Length>0;}}
    private async Task Run(Func<Task> work)
    {try{await work();}catch(Exception ex){Status.Text=ex.Message;}}
    private async void Preview_Click(object sender,RoutedEventArgs e)=>await Run(PreviewAsync);
    private void ProjectStaging_Click(object sender,RoutedEventArgs e)=>new ProjectStagingWindow(_store){Owner=this}.ShowDialog();
    private async void SavePolicy_Click(object sender,RoutedEventArgs e)=>await Run(()=>{
        CheckTarget();if(_policyInvalid)throw new InvalidOperationException("保留策略损坏，不能覆盖，请先检查备份。");var p=Policy();DurableJson.Write(System.IO.Path.Combine(Root,"storage-retention.json"),new StorageRetention(p.Days,p.Keep));Status.Text="策略已保存；只用于手动预览，不会自动清理。";return Task.CompletedTask;});
    private async void Remove_Click(object sender,RoutedEventArgs e)=>await Run(async()=>{
        if(_preview==null)return;var rows=HistoryList.SelectedItems.Cast<StorageReference>().ToArray();if(rows.Length==0)return;
        if(!_confirm($"移除已选择的 {rows.Length} 条历史索引？\n文件原件保留；纯文字正文随索引移除。撤回仅当前进程最多50步，长期保留请先导出备份。","确认移除索引"))return;
        StorageMaintenance.RemoveHistory(_store,_preview,rows.Select(x=>x.Id));await _store.FlushAsync();_changed();await PreviewAsync();});
    private async void Recycle_Click(object sender,RoutedEventArgs e)=>await Run(async()=>{
        if(_preview==null)return;var rows=FilesList.SelectedItems.Cast<StorageFile>().ToArray();if(rows.Length==0)return;
        if(!_confirm($"将所选 {rows.Length} 个未引用文件移入本库回收区？\n可恢复；不会立即释放磁盘。外部原件不会移动。","确认回收文件"))return;
        StorageMaintenance.Recycle(_store,_preview,rows.Select(x=>x.Path));await PreviewAsync();});
    private async void Restore_Click(object sender,RoutedEventArgs e)=>await Run(async()=>{CheckTarget();_working=true;SetBusy(true);int n;
        try{n=await Task.Run(()=>StorageMaintenance.RestoreLatest(Root));}finally{_working=false;SetBusy(false);}await PreviewAsync();Status.Text=$"已恢复 {n} 个文件；同名现有文件不会被覆盖。";});
    private async void Purge_Click(object sender,RoutedEventArgs e)=>await Run(async()=>{
        if(_preview==null){await PreviewAsync();TrashDetails.IsExpanded=true;Status.Text="请核对回收区明细，再次点击永久清空。";return;}
        if(!TrashDetails.IsExpanded){TrashDetails.IsExpanded=true;Status.Text="请核对回收区明细，再次点击永久清空。";return;}
        CheckTarget();if(!_preview.Complete)throw new InvalidOperationException("扫描不完整，请检查回收区后重新预览。");
        if(!_confirm($"永久删除已预览的 {_preview.TrashDetails.Length} 个回收文件（{FileClassifier.FormatSize(_preview.TrashBytes)}）？此操作不能撤回。\n不会删除回收区之外的原文件、索引或续传状态。","永久清空回收区"))return;
        var version=_preview.TrashVersion;_working=true;SetBusy(true);Status.Text="正在后台清空已确认的回收文件…";int n;
        try{n=await Task.Run(()=>StorageMaintenance.PurgeTrash(Root,version));}finally{_working=false;SetBusy(false);}await PreviewAsync();Status.Text=$"已永久清除 {n} 个回收文件。";});
    private void Close_Click(object sender,RoutedEventArgs e){if(_scan!=null)_scan.Cancel();else Close();}
}
