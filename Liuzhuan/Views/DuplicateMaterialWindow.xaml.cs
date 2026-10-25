using System.IO;
using System.Windows;
using System.Windows.Controls;
using Liuzhuan.Models;
using Liuzhuan.Services;
using Liuzhuan.Utils;

namespace Liuzhuan.Views;

public partial class DuplicateMaterialWindow : Window
{
    private readonly DataStore _store;
    private readonly HashSet<string> _ids;
    private CancellationTokenSource? _operation;
    private bool _closeAfterOperation;
    public DuplicateMaterialWindow(DataStore store, IReadOnlyList<MaterialItem> items)
    {
        InitializeComponent(); _store = store;
        _ids = items.Where(x => x.HasFileSource).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        StartButton.Content = $"开始检查（{_ids.Count} 条素材）";
        StartButton.IsEnabled = _ids.Count > 0;
        Loaded += (_, _) => { MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 32); Height = Math.Min(Height, MaxHeight); };
        Closing += (_, e) =>
        {
            if (_operation == null) return;
            e.Cancel = true; _closeAfterOperation = true; _operation.Cancel(); StatusText.Text = "正在取消，请等待文件读取结束…";
        };
    }
    private IReadOnlyList<IntegritySource> Snapshot() => IntegrityService.Snapshot(_store.Items.Where(x => _ids.Contains(x.Id)));
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_operation != null) return;
        var snapshot = Snapshot();
        using var cancel = new CancellationTokenSource(); _operation = cancel;
        StartButton.IsEnabled = false; CancelButton.IsEnabled = true;
        GroupsList.ItemsSource = FilesList.ItemsSource = IssuesList.ItemsSource = null;
        IssuesTab.Header = "未完成检查";
        SummaryText.Text = "正在检查选定范围…"; Progress.Visibility = Visibility.Visible; Progress.Value = 0;
        var progress = new Progress<DuplicateProgress>(p =>
        {
            if (!ReferenceEquals(_operation, cancel) || cancel.IsCancellationRequested) return;
            var partial = p.FileBytes > 0 ? Math.Min(1, (double)p.ReadBytes / p.FileBytes) : 0;
            Progress.Value = 100 * (p.Completed + partial) / Math.Max(1, p.Total);
            StatusText.Text = $"{p.Stage} · {p.Completed} / {p.Total}" + (p.CurrentName.Length > 0 ? " · " + p.CurrentName : "") +
                (p.FileBytes > 0 ? $" · {FileClassifier.FormatSize(p.ReadBytes)} / {FileClassifier.FormatSize(p.FileBytes)}" : "");
        });
        try
        {
            var scan = await DuplicateMaterialService.ScanAsync(snapshot, progress, cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            var result = DuplicateMaterialService.Reconcile(scan, Snapshot());
            GroupsList.ItemsSource = result.Groups; IssuesList.ItemsSource = result.Issues;
            IssuesTab.Header = $"未完成检查（{result.Issues.Count}）";
            if (result.Groups.Count > 0) { GroupsList.SelectedIndex = 0; FilesTab.IsSelected = true; }
            else if (result.Issues.Count > 0) IssuesTab.IsSelected = true;
            SummaryText.Text = $"{result.Groups.Count} 组同内容文件 · 逻辑重复 {FileClassifier.FormatSize(result.LogicalDuplicateBytes)}\n" +
                $"{result.SourceCount} 条索引 / {result.UniquePaths} 个路径 · 大小候选 {result.CandidatePaths} · 已计算摘要 {result.HashedPaths} · 读取 {FileClassifier.FormatSize(result.ReadBytes)}";
            StatusText.Text = result.Issues.Count > 0 ? "检查已结束，部分来源未能确认，请查看“未完成检查”；结果不代表整个范围均已检查成功。" :
                result.Groups.Count > 0 ? "检查完成。选择左侧分组查看不同来源；本次未修改任何索引、基线或文件。" : "检查完成，未发现已确认的同内容文件。不同大小的文件无需读取内容。";
        }
        catch (OperationCanceledException) { SummaryText.Text = "检查已取消，未发布本次部分结果。"; StatusText.Text = "索引、基线和文件均未修改。"; }
        catch (Exception ex) { SummaryText.Text = "检查未完成。"; StatusText.Text = ex.Message; }
        finally
        {
            _operation = null; StartButton.IsEnabled = _ids.Count > 0; CancelButton.IsEnabled = false; Progress.Visibility = Visibility.Collapsed;
            if (_closeAfterOperation) Close();
        }
    }
    private void Group_Changed(object sender, SelectionChangedEventArgs e)
    { if (FilesList != null) FilesList.ItemsSource = (GroupsList.SelectedItem as DuplicateGroup)?.Files; if (FilesTab != null && GroupsList.SelectedItem != null) FilesTab.IsSelected = true; }
    private void Cancel_Click(object sender, RoutedEventArgs e) { _operation?.Cancel(); StatusText.Text = "正在取消，请等待文件读取结束…"; }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path }) return;
        try { FolderLauncher.Open(Path.GetDirectoryName(path) ?? throw new IOException("未记录所在目录。")); }
        catch (Exception ex) { StatusText.Text = "无法打开所在位置：" + ex.Message; }
    }
}
