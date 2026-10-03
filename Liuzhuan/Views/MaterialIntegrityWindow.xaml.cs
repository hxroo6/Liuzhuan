using System.Windows;
using System.Windows.Controls;
using Liuzhuan.Models;
using Liuzhuan.Services;

namespace Liuzhuan.Views;

public sealed record IntegrityRow(MaterialItem Item, IntegrityResult? Result)
{
    public string HashLabel => Result?.Sha256 is { } digest ? "本次 SHA-256：" + digest
        : Item.IntegrityBaseline is { } baseline ? $"记录 SHA-256：{baseline.Sha256}\n记录时间：{baseline.RecordedUtc.LocalDateTime:yyyy-MM-dd HH:mm:ss}" : "尚未记录 SHA-256 基线";
}

public partial class MaterialIntegrityWindow : Window
{
    private readonly DataStore _store;
    private readonly HashSet<string> _ids;
    private readonly Func<string, string, bool> _confirm;
    private readonly Dictionary<string, IntegrityResult> _results = new(StringComparer.Ordinal);
    private CancellationTokenSource? _operation;
    private bool _closeAfterOperation;
    public MaterialIntegrityWindow(DataStore store, IReadOnlyList<MaterialItem> items, Func<string, string, bool>? confirm = null)
    {
        InitializeComponent(); _store = store; _ids = items.Where(x => x.HasFileSource).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        _confirm = confirm ?? ((message, title) => MessageBox.Show(this, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK);
        RefreshRows();
        Closing += (_, e) =>
        {
            if (_operation == null) return;
            e.Cancel = true; _closeAfterOperation = true; _operation.Cancel(); StatusText.Text = "正在取消，请等待当前文件读取结束…";
        };
    }
    private MaterialItem[] CurrentItems() => _store.Items.Where(x => _ids.Contains(x.Id) && x.HasFileSource).ToArray();
    private void RefreshRows()
    {
        var selected = IntegrityList.SelectedItems.Cast<IntegrityRow>().Select(x => x.Item.Id).ToHashSet(StringComparer.Ordinal);
        var rows = CurrentItems().Select(x => new IntegrityRow(x, _results.TryGetValue(x.Id, out var result) && IntegrityService.MatchesSource(x, result.Source) ? result : null)).ToArray();
        IntegrityList.ItemsSource = rows;
        foreach (var row in rows.Where(x => selected.Contains(x.Item.Id))) IntegrityList.SelectedItems.Add(row);
        CheckButton.Content = $"开始检查此列表（{rows.Length} 项）";
        CheckButton.IsEnabled = _operation == null && rows.Length > 0;
        UpdateButtons();
    }
    private IntegrityRow[] Selected() => IntegrityList.SelectedItems.Cast<IntegrityRow>().ToArray();
    private void UpdateButtons()
    {
        if (RecordButton == null || _store == null) return;
        var selected = Selected();
        RecordButton.IsEnabled = _operation == null && selected.Length > 0 && selected.All(x => x.Result?.CanRecord == true && IntegrityService.MatchesSource(x.Item, x.Result.Source));
        ClearButton.IsEnabled = _operation == null && selected.Any(x => x.Item.IntegrityBaseline != null);
        SelectButton.IsEnabled = _operation == null && _results.Count > 0;
    }
    private void Selection_Changed(object sender, SelectionChangedEventArgs e) => UpdateButtons();
    private void Select_Click(object sender, RoutedEventArgs e)
    {
        IntegrityList.SelectedItems.Clear();
        foreach (var row in IntegrityList.Items.Cast<IntegrityRow>().Where(x => x.Result?.CanRecord == true && IntegrityService.MatchesSource(x.Item, x.Result.Source))) IntegrityList.SelectedItems.Add(row);
    }
    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        if (_operation != null) return;
        var snapshot = IntegrityService.Snapshot(CurrentItems());
        using var cancellation = new CancellationTokenSource(); _operation = cancellation;
        _results.Clear(); RefreshRows(); Progress.Visibility = Visibility.Visible; Progress.Value = 0;
        CloseButton.Content = "取消检查"; StatusText.Text = "正在读取选定文件…";
        var progress = new Progress<IntegrityProgress>(p =>
        {
            if (!ReferenceEquals(_operation, cancellation) || cancellation.IsCancellationRequested) return;
            var part = p.FileBytes > 0 ? Math.Min(1.0, (double)p.ReadBytes / p.FileBytes) : 0;
            Progress.Value = 100.0 * (p.CompletedItems + part) / Math.Max(1, p.TotalItems);
            StatusText.Text = $"{p.CompletedItems} / {p.TotalItems} · {p.CurrentName}" + (p.FileBytes > 0 ? $" · {p.ReadBytes / 1048576.0:F1} / {p.FileBytes / 1048576.0:F1} MB" : "");
        });
        try
        {
            var results = await IntegrityService.CheckAsync(snapshot, progress, cancellation.Token); cancellation.Token.ThrowIfCancellationRequested();
            var applied = _store.ApplyIntegrityResults(results);
            foreach (var result in results) _results[result.Source.Id] = result;
            StatusText.Text = $"已检查 {applied} 项 · 正常 {results.Count(x => x.Status == MaterialIntegrityStatus.Normal)} · 变化 {results.Count(x => x.Status == MaterialIntegrityStatus.Changed)} · 缺失 {results.Count(x => x.Status == MaterialIntegrityStatus.Missing)} · 未记录 {results.Count(x => x.Status == MaterialIntegrityStatus.Unrecorded)} · 读取失败 {results.Count(x => x.Status == MaterialIntegrityStatus.ReadFailed)}"
                + (applied < results.Count ? "\n来源或基线已变化的结果未应用，请重新检查。" : "");
        }
        catch (OperationCanceledException) { StatusText.Text = "已取消；本次结果未应用，原文件与已有基线保持完整。"; }
        catch (Exception ex) { StatusText.Text = "检查未完成：" + ex.Message; }
        finally
        {
            _operation = null; Progress.Visibility = Visibility.Collapsed; CloseButton.Content = "关闭"; RefreshRows();
            if (_closeAfterOperation) Close();
        }
    }
    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_operation != null) return;
        var rows = Selected();
        if (rows.Length == 0 || rows.Any(x => x.Result?.CanRecord != true)) return;
        if (!_confirm($"将把 {rows.Length} 项本次检查的 SHA-256 记录为新基线。\n已有基线将被替换；内容变化将成为新的参考值。\n\n请仅在确认合法编辑或来源正确后继续。原文件不会修改。", "确认记录内容基线")) return;
        try { var count = _store.SetIntegrityBaselines(rows.Select(x => x.Result!)); _results.Clear(); RefreshRows(); StatusText.Text = $"已记录 {count} 项基线；再次检查可与新基线比较。"; }
        catch (Exception ex) { StatusText.Text = "基线未更新：" + ex.Message; }
    }
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_operation != null) return;
        var ids = Selected().Where(x => x.Item.IntegrityBaseline != null).Select(x => x.Item.Id).ToArray();
        if (ids.Length == 0 || !_confirm($"移除 {ids.Length} 项已记录的基线？\n原文件、收藏和整理信息均保留。", "确认移除基线")) return;
        var count = _store.ClearIntegrityBaselines(ids); _results.Clear(); RefreshRows(); StatusText.Text = $"已移除 {count} 项基线。";
    }
    private void Close_Click(object sender, RoutedEventArgs e) { if (_operation == null) Close(); else { _operation.Cancel(); StatusText.Text = "正在取消，请等待当前文件读取结束…"; } }
}
