using System.IO;
using System.Windows;
using Liuzhuan.Models;
using Liuzhuan.Services;
using Liuzhuan.Utils;

namespace Liuzhuan.Views;

public sealed record SourceRepairRow(MaterialItem Item, SourceRelinkMatch? Match)
{
    public string Name => Item.DisplayName;
    public string OriginalPath => "原路径：" + Item.FilePath;
    public string CandidatePath => Match?.Status == SourceRelinkMatchStatus.Unique ? "候选：" + Match.Candidates[0].CandidatePath : "";
    public string Status => Match?.Message ?? "原文件失效 · 可选择新文件或查找所在文件夹";
}

public partial class SourceRepairWindow : Window
{
    private readonly DataStore _store;
    private readonly Action<IReadOnlyList<MaterialItem>> _onRelinked;
    private CancellationTokenSource? _operation;
    private bool _closeAfterOperation;
    private readonly Dictionary<string, SourceRelinkMatch> _matches = new();
    private readonly string? _preferredId;
    public SourceRepairWindow(DataStore store, Action<IReadOnlyList<MaterialItem>> onRelinked, string? selectedId = null)
    {
        InitializeComponent(); _store = store; _onRelinked = onRelinked; _preferredId = selectedId; RefreshRows();
        Loaded += async (_, _) => await CheckSources();
        Closing += (_, e) => { if (_operation == null) return; e.Cancel = true; _closeAfterOperation = true; _operation.Cancel(); StatusText.Text = "正在取消，请稍候…"; };
    }
    private void RefreshRows()
    {
        var rows = _store.Items.Where(x => x.HasFileSource && x.IsSourceMissing)
            .Select(x => new SourceRepairRow(x, _matches.GetValueOrDefault(x.Id))).ToArray();
        var selectedId = (SourceList.SelectedItem as SourceRepairRow)?.Item.Id ?? _preferredId;
        SourceList.ItemsSource = rows;
        if (selectedId != null) SourceList.SelectedItem = rows.FirstOrDefault(x => x.Item.Id == selectedId);
        EmptyText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyButton.IsEnabled = _operation == null && UniquePlans().Length > 0;
        ApplyButton.Content = $"应用 {UniquePlans().Length} 个唯一候选";
    }
    private SourceRelinkPlan[] UniquePlans() => _store.Items.Where(x => x.HasFileSource && x.IsSourceMissing)
        .Select(x => _matches.GetValueOrDefault(x.Id)).Where(x => x?.Status == SourceRelinkMatchStatus.Unique)
        .Select(x => x!.Candidates[0]).ToArray();
    private async Task RunOperation(Func<CancellationToken, Task> action)
    {
        if (_operation != null) return;
        using var cancellation = new CancellationTokenSource(); _operation = cancellation;
        CheckButton.IsEnabled = FindButton.IsEnabled = ChooseButton.IsEnabled = ApplyButton.IsEnabled = false;
        CloseButton.Content = "取消操作";
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { StatusText.Text = "已取消，来源索引未改动。"; }
        catch (Exception ex) { StatusText.Text = "未完成：" + ex.Message; }
        finally
        {
            _operation = null; CheckButton.IsEnabled = FindButton.IsEnabled = ChooseButton.IsEnabled = true;
            CloseButton.Content = "关闭"; RefreshRows();
            if (_closeAfterOperation) Close();
        }
    }
    private Task CheckSources() => RunOperation(async ct =>
    {
        _matches.Clear(); StatusText.Text = "正在检查原文件…";
        var snapshot = SourceRelinkService.Snapshot(_store.Items.Where(x => x.HasFileSource));
        var statuses = await SourceRelinkService.CheckSourcesAsync(snapshot, ct); ct.ThrowIfCancellationRequested();
        var missing = _store.ApplySourceStatuses(statuses);
        StatusText.Text = $"已检查 {statuses.Count} 项 · {missing} 项原文件无法访问";
    });
    private async void Check_Click(object sender, RoutedEventArgs e) => await CheckSources();
    private async void Find_Click(object sender, RoutedEventArgs e)
    {
        if (_operation != null) return;
        try
        {
            var folder = FolderPicker.Show(this); if (folder == null) return;
            await RunOperation(async ct =>
            {
                _matches.Clear(); StatusText.Text = "正在查找候选文件…";
                var snapshot = SourceRelinkService.Snapshot(_store.Items.Where(x => x.HasFileSource && x.IsSourceMissing));
                var progress = new Progress<SourceRelinkProgress>(p => { if (_operation != null && _operation.Token == ct && !ct.IsCancellationRequested) StatusText.Text = $"已扫描 {p.ScannedFiles} 个文件 · {p.ScannedDirectories} 个目录"; });
                var result = await SourceRelinkService.FindCandidatesAsync(folder, snapshot, progress, ct); ct.ThrowIfCancellationRequested();
                foreach (var match in result.Matches) _matches[match.Source.Id] = match;
                StatusText.Text = $"找到 {result.Matches.Count(x => x.Status == SourceRelinkMatchStatus.Unique)} 个唯一候选 · {result.Matches.Count(x => x.Status == SourceRelinkMatchStatus.Ambiguous)} 项存在歧义" +
                    (result.Issues.Count > 0 ? $"\n有 {result.Issues.Count} 项扫描提示：{result.Issues[0].Message}" : "");
            });
        }
        catch (Exception ex) { StatusText.Text = "无法选择文件夹：" + ex.Message; }
    }
    private async void Choose_Click(object sender, RoutedEventArgs e)
    {
        if (_operation != null) return;
        if (SourceList.SelectedItem is not SourceRepairRow row) { StatusText.Text = "先选择一项失效素材。"; return; }
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "选择新的原文件", CheckFileExists = true, Multiselect = false, Filter = "所有文件|*.*" };
        if (picker.ShowDialog(this) != true) return;
        var snapshot = SourceRelinkService.Snapshot(new[]{row.Item})[0]; var path = picker.FileName;
        await RunOperation(async ct =>
        {
            var plan = await Task.Run(() => SourceRelinkService.PrepareSingle(snapshot,path),ct); ct.ThrowIfCancellationRequested();
            if (MessageBox.Show(this,$"确认将「{row.Name}」重新定位到：\n{plan.CandidatePath}\n\n保留收藏、标签、备注和原位置。候选不保证与原文件内容相同。","确认新来源",MessageBoxButton.OKCancel,MessageBoxImage.Question) != MessageBoxResult.OK) return;
            var updated = _store.RelinkSource(plan); _matches.Remove(updated.Id); _onRelinked(new[]{updated});
            StatusText.Text = "已重新定位 1 项素材。";
        });
    }
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_operation != null) return;
        var plans = UniquePlans(); if (plans.Length == 0) return;
        if (MessageBox.Show(this,$"将应用列表中的 {plans.Length} 个唯一候选。\n候选按文件名、类型和已知大小匹配，请确认路径正确。\n\n保留所有整理信息，不移动或覆盖原文件。","确认批量重新定位",MessageBoxButton.OKCancel,MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            var changed = _store.RelinkSources(plans);
            var updated = _store.Items.Where(x => plans.Any(p => p.Source.Id == x.Id)).ToArray();
            _matches.Clear(); _onRelinked(updated); StatusText.Text = $"已重新定位 {changed} 项素材。"; RefreshRows();
        }
        catch (Exception ex) { StatusText.Text = "未应用：" + ex.Message; }
    }
    private void Close_Click(object sender, RoutedEventArgs e) { if (_operation != null) _operation.Cancel(); else Close(); }
}
