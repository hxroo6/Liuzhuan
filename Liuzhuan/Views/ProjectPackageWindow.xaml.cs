using System.IO;
using System.Windows;
using Liuzhuan.Models;
using Liuzhuan.Services;

namespace Liuzhuan.Views;

public partial class ProjectPackageWindow : Window
{
    private readonly DataStore _store;
    private readonly HashSet<string> _selection;
    private readonly string _root;
    private readonly Action? _changed;
    private ProjectPackagePrepared? _prepared;
    private long _previewSequence;
    private CancellationTokenSource? _operation;
    private bool _committing,_closeAfter;
    public ProjectPackageWindow(DataStore store,IReadOnlyList<MaterialItem> selected,Action? changed=null)
    {
        InitializeComponent();_store=store;_selection=selected.Select(x=>x.Id).ToHashSet(StringComparer.Ordinal);
        _root=Path.GetFullPath(ConfigService.GetEffectiveDataDir());_changed=changed;
        SelectionText.Text=_selection.Count==0?"先在素材栏选择要带走的文件或文字，也可在此导入项目包。":$"已选择 {_selection.Count} 项。打包只带素材及其整理信息。";
        ExportButton.IsEnabled=_selection.Count>0;
        Loaded+=(_,_)=>{MaxHeight=Math.Max(MinHeight,SystemParameters.WorkArea.Height-32);Height=Math.Min(Height,MaxHeight);};
        Closing+=(_,e)=>{if(_operation==null)return;e.Cancel=true;_closeAfter=true;if(!_committing)_operation.Cancel();Status.Text=_committing?"正在保存本机索引，请等待结果…":"正在取消，请稍候…";};
    }
    private void VerifyRoot()
    {
        if(!string.Equals(Path.TrimEndingDirectorySeparator(_root),Path.TrimEndingDirectorySeparator(Path.GetFullPath(ConfigService.GetEffectiveDataDir())),StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("素材库位置已变化，请关闭并重新打开项目包窗口。");
    }
    private void Choose_Click(object sender,RoutedEventArgs e)
    {
        if(_operation!=null)return;
        var picker=new Microsoft.Win32.OpenFileDialog {Title="选择流转项目素材包",Filter="项目素材包 (*.zip)|*.zip",CheckFileExists=true};
        if(picker.ShowDialog(this)!=true)return;
        PackagePath.Text=picker.FileName;_prepared=null;ImportButton.IsEnabled=false;PreviewList.ItemsSource=null;PreviewSummary.Text="点击校验并预览，确认内容后再合入。";
    }
    private async void Export_Click(object sender,RoutedEventArgs e)
    {
        if(_operation!=null)return;
        var picker=new Microsoft.Win32.SaveFileDialog {Title="导出选中素材为项目包",Filter="项目素材包 (*.liuzhuan-project.zip)|*.liuzhuan-project.zip|ZIP (*.zip)|*.zip",DefaultExt=".zip",FileName=$"流转项目-{DateTime.Now:yyyyMMdd-HHmm}.liuzhuan-project.zip",AddExtension=true};
        if(picker.ShowDialog(this)!=true)return;
        await Run(async (progress,token)=> {
            VerifyRoot();
            var snapshot=ProjectPackageService.Snapshot(_store.Items.Where(x=>_selection.Contains(x.Id)));
            var result=await ProjectPackageService.ExportAsync(picker.FileName,snapshot,progress,token);
            Status.Text=$"项目包已导出，共 {result.ItemCount} 项。\n{picker.FileName}";
        });
    }
    private async void Preview_Click(object sender,RoutedEventArgs e)
    {
        if(_operation!=null)return;
        if(string.IsNullOrWhiteSpace(PackagePath.Text)){Status.Text="请先选择项目包。";return;}
        _prepared=null;ImportButton.IsEnabled=false;PreviewList.ItemsSource=null;
        var path=PackagePath.Text;
        await Run(async (progress,token)=> {
            VerifyRoot();var prepared=await ProjectPackageService.ReadPreviewAsync(path,_root,progress,token);
            token.ThrowIfCancellationRequested();VerifyRoot();
            var sequence=_store.CurrentSequence;
            var counts=_store.PreviewProjectBatch(prepared.CreateMaterials(),sequence,_root);
            _prepared=prepared;_previewSequence=sequence;PreviewList.ItemsSource=prepared.Preview.Entries;
            PreviewSummary.Text=$"{prepared.Preview.ItemCount} 项 · {prepared.Preview.FileCount} 个原件 · {Utils.FileClassifier.FormatSize(prepared.Preview.PayloadBytes)}\n将新增 {counts.Added} 项 · 同包已登记 {counts.AlreadyImported} 项 · 同文保留现有 {counts.DuplicateText} 项";
            Status.Text="全部内容已校验并准备。合入保留当前库已有素材与设置；同包条目和相同文字不会覆盖本机整理信息。";
        });
    }
    private async void Import_Click(object sender,RoutedEventArgs e)
    {
        if(_operation!=null||_prepared==null)return;
        var expected=_prepared.Preview;var sequence=_previewSequence;var path=PackagePath.Text;
        await Run(async (progress,token)=> {
            VerifyRoot();if(sequence!=_store.CurrentSequence)throw new InvalidOperationException("素材库在预览后已变化，请重新校验并预览。");
            var verified=await ProjectPackageService.PrepareImportAsync(path,_root,progress,token,expected);
            using var commitLease=ProjectStagingService.AcquirePreparationLease(_root);
            await ProjectPackageService.ValidatePreparedAsync(verified,progress,token);
            token.ThrowIfCancellationRequested();VerifyRoot();
            var materials=verified.CreateMaterials();
            // Validation still runs before the irreversible part. After this point, cancellation
            // cannot roll back files or rows which may already be referenced by a durable snapshot.
            _store.PreviewProjectBatch(materials,sequence,_root);
            _committing=true;CancelButton.IsEnabled=false;Status.Text="正在保存到本机索引，请等待确认…";
            try {
                var result=_store.ImportProjectBatch(materials,sequence,_root);_changed?.Invoke();
                await _store.FlushAsync();
                var live=_store.Items.ToHashSet();
                foreach(var material in materials.Where(x=>live.Contains(x)&&x.Type is MaterialType.Image or MaterialType.Audio or MaterialType.Video))
                    DragDropService.LoadDeferredPropertiesAsync(material,_changed);
                _prepared=null;Status.Text=$"已保存到当前素材库：新增 {result.Added} 项，同包跳过 {result.AlreadyImported} 项，同文保留现有 {result.DuplicateText} 项。";
            }catch {
                _prepared=null;_changed?.Invoke();
                throw new IOException("合入后的保存尚未确认。原件和当前会话中的素材已保留；请重新校验预览并重试，避免重复手动添加。");
            }
        });
    }
    private async Task Run(Func<IProgress<ProjectPackageProgress>,CancellationToken,Task> action)
    {
        if(_operation!=null)return;
        using var cancel=new CancellationTokenSource();_operation=cancel;_committing=false;
        ExportButton.IsEnabled=ChooseButton.IsEnabled=PreviewButton.IsEnabled=ImportButton.IsEnabled=false;
        Progress.Visibility=CancelButton.Visibility=Visibility.Visible;Progress.IsIndeterminate=true;CancelButton.IsEnabled=true;
        var progress=new Progress<ProjectPackageProgress>(value=> {
            if(!ReferenceEquals(_operation,cancel)||cancel.IsCancellationRequested||_committing)return;
            Status.Text=$"{value.Stage} · {value.CompletedItems} / {value.TotalItems}"+(value.CurrentName.Length>0?" · "+value.CurrentName:"");
            if(value.TotalBytes>0){Progress.IsIndeterminate=false;Progress.Maximum=value.TotalBytes;Progress.Value=Math.Min(value.TotalBytes,value.CopiedBytes);}
        });
        try {await action(progress,cancel.Token);}
        catch(OperationCanceledException){Status.Text="已取消本次准备，当前素材索引未提交。已校验的暂存原件保留供重试。";}
        catch(Exception ex){Status.Text="未完成："+ex.Message;}
        finally {
            _operation=null;_committing=false;Progress.Visibility=CancelButton.Visibility=Visibility.Collapsed;
            ExportButton.IsEnabled=_selection.Count>0;ChooseButton.IsEnabled=PreviewButton.IsEnabled=true;ImportButton.IsEnabled=_prepared!=null;
            if(_closeAfter)Close();
        }
    }
    private void Cancel_Click(object sender,RoutedEventArgs e){if(!_committing)_operation?.Cancel();}
    private void Close_Click(object sender,RoutedEventArgs e)=>Close();
}
