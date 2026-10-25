using System.Windows;
using System.Windows.Controls;
using Liuzhuan.Services;

namespace Liuzhuan.Views;

public partial class LibraryHealthWindow : Window
{
    private readonly DataStore _store;
    private bool _busy, _closeAfter;
    private LibraryIndexVersion? _compared;
    private LibraryIndexDiff? _diff;
    private long _comparedSequence;
    public LibraryHealthWindow(DataStore store)
    {
        InitializeComponent(); _store = store; RootText.Text = "当前素材库：" + store.DataRoot;
        Loaded += async (_,_) => { MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 32); Height = Math.Min(Height, MaxHeight); await Refresh(); };
        Closing += (_, e) => { if (_busy) { e.Cancel = true; _closeAfter = true; } };
    }
    private async Task Refresh()
    {
        await Run(async () => {
            Libraries.ItemsSource = await LibraryRecoveryService.ListLibrariesAsync(App.DataDir, _store.DataRoot);
            Versions.ItemsSource = await LibraryRecoveryService.HistoryAsync(_store.DataRoot);
            PendingText.Text = LibraryRecoveryService.PendingDescription(App.DataDir) ?? "没有待应用计划。";
            Status.Text = LibraryRecoveryService.HistoryWarning(_store.DataRoot) ?? "切换保留原库；恢复前会保留原索引备份。此处不恢复下载原件或已删除的文件。";
            _compared = null; _diff = null;
        });
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await Refresh();
    private void Library_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();
    private void Version_SelectionChanged(object sender, SelectionChangedEventArgs e) { _compared = null; _diff = null; UpdateButtons(); }
    private async void Diff_Click(object sender, RoutedEventArgs e)
    {
        if (Versions.SelectedItem is not LibraryIndexVersion version) { Status.Text = "请先选择索引历史。"; return; }
        await Run(async () => {
            var sequence = _store.CurrentSequence;
            if (!_store.LoadFailed) await _store.FlushAsync();
            var diff = await LibraryRecoveryService.DiffAsync(_store.DataRoot, version.Path);
            if (sequence != _store.CurrentSequence) throw new InvalidOperationException("素材库已变化，请重新比较。");
            _compared = version; _comparedSequence = sequence; _diff = diff; Differences.ItemsSource = diff.Details;
            DifferenceSummary.Text = diff.CurrentUnreadable ? "当前索引不可读取，无法比较差异。所选历史已校验；恢复前将保留原索引备份。" :
                $"将恢复索引 {diff.Reappearing} 项 · 移出索引 {diff.RemovedFromIndex} 项 · 恢复旧信息 {diff.Changed} 项 · 不变 {diff.Unchanged} 项。明细最多显示100项。";
            Status.Text = "请核对变化；原件不会因恢复被删除或改写。安排后继续编辑会使恢复计划失效。";
        });
    }
    private async void Switch_Click(object sender, RoutedEventArgs e)
    {
        if (Libraries.SelectedItem is not LibraryLocation { Readable:true } library) return;
        if (MessageBox.Show(this, "下次启动切换到：\n" + library.Root + "\n\n当前素材库保留，当前会话继续使用原库。是否安排？", "安排素材库切换", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await Run(async () => {
            if (!_store.LoadFailed) await _store.FlushAsync();
            await LibraryRecoveryService.QueueSwitchAsync(App.DataDir, _store.DataRoot, library.Root);
            PendingText.Text = LibraryRecoveryService.PendingDescription(App.DataDir); Status.Text = "已安排，请正常退出并重新打开流转。";
        });
    }
    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_compared == null || _diff == null) return;
        var version = _compared;
        if (MessageBox.Show(this, DifferenceSummary.Text + "\n\n下次启动应用所选索引历史；恢复前保留原索引备份。原件和设置保持现状。是否安排？", "安排索引恢复", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await Run(async () => {
            if (_store.CurrentSequence != _comparedSequence) throw new InvalidOperationException("素材库已变化，请重新比较。");
            if (!_store.LoadFailed) await _store.FlushAsync();
            if (_store.CurrentSequence != _comparedSequence) throw new InvalidOperationException("素材库已变化，请重新比较。");
            await LibraryRecoveryService.QueueRestoreAsync(App.DataDir, _store.DataRoot, version.Path);
            PendingText.Text = LibraryRecoveryService.PendingDescription(App.DataDir); Status.Text = "已安排。请正常退出并重开；期间继续编辑会使计划失效，可在这里取消。";
        });
    }
    private async void CancelPlan_Click(object sender, RoutedEventArgs e) => await Run(async () => { await Task.Run(() => LibraryRecoveryService.CancelPending(App.DataDir)); PendingText.Text = "没有待应用计划。"; Status.Text = "已取消本地恢复／切换计划。"; });
    private async Task Run(Func<Task> action)
    {
        if (_busy) return; _busy = true; Busy.Visibility = Visibility.Visible; UpdateButtons();
        try { await action(); } catch (Exception ex) { Status.Text = "未完成：" + ex.Message; }
        finally { _busy = false; Busy.Visibility = Visibility.Collapsed; UpdateButtons(); if (_closeAfter) Close(); }
    }
    private void UpdateButtons()
    {
        if (SwitchButton == null) return;
        RefreshButton.IsEnabled = CancelPlanButton.IsEnabled = DiffButton.IsEnabled = !_busy;
        Libraries.IsEnabled = Versions.IsEnabled = !_busy;
        SwitchButton.IsEnabled = !_busy && Libraries.SelectedItem is LibraryLocation { Readable:true } library && !string.Equals(library.Root, _store.DataRoot, StringComparison.OrdinalIgnoreCase);
        RestoreButton.IsEnabled = !_busy && _compared != null && _diff != null;
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
