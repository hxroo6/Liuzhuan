using System.Windows;
using System.Windows.Controls;
using Liuzhuan.Services;

namespace Liuzhuan.Views;

public partial class ProjectStagingWindow : Window
{
    private readonly DataStore _store;
    private ProjectStagingPreview? _preview;
    private bool _busy, _closeAfter;
    public ProjectStagingWindow(DataStore store)
    {
        InitializeComponent(); _store = store;
        Loaded += (_,_) => { MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 32); Height = Math.Min(Height, MaxHeight); };
        Closing += (_,e) => { if (_busy) { e.Cancel = true; _closeAfter = true; } };
    }
    private async void Scan_Click(object sender, RoutedEventArgs e) => await Run(Scan);
    private async Task Scan()
    {
        var references = ProjectStagingService.Snapshot(_store); _preview = null; Files.ItemsSource = null;
        var preview = await ProjectStagingService.PreviewAsync(references); _preview = preview; Files.ItemsSource = preview.Files;
        string Size(long value) => Liuzhuan.Utils.FileClassifier.FormatSize(value);
        Summary.Text = $"{preview.Packages} 个项目 · 已引用 {Size(preview.ReferencedBytes)}\n未引用且准备完整 {Size(preview.PreparedBytes)} · 未完成准备 {Size(preview.IncompleteBytes)}\n受阻内容（可读取部分）{Size(preview.BlockedBytes)} · 项目回收区 {Size(preview.TrashBytes)}";
        Warnings.Text = string.Join("\n", preview.Warnings);
        Status.Text = preview.Complete ? $"可选择 {preview.Files.Length} 个未引用原件，每批最多200个。Ctrl／Shift可多选；回收后保留绑定供再次导入。" : "扫描不完整，已禁止回收。请保留原件并先检查提示。";
    }
    private void Selection_Changed(object sender, SelectionChangedEventArgs e) => UpdateButtons();
    private void Select_Click(object sender, RoutedEventArgs e)
    {
        Files.SelectedItems.Clear(); foreach (var file in Files.Items.Cast<ProjectStagingFile>().Take(200)) Files.SelectedItems.Add(file);
    }
    private async void Recycle_Click(object sender, RoutedEventArgs e)
    {
        if (_preview == null) return; var preview = _preview; var paths = Files.SelectedItems.Cast<ProjectStagingFile>().Select(x => x.Path).ToArray();
        if (paths.Length is <1 or >200) { Status.Text = "每批请选择1–200个文件。"; return; }
        if (MessageBox.Show(this, $"将所选 {paths.Length} 个未引用暂存原件移入项目回收区，可恢复最近一批。不会移动已引用素材，也不会删除项目绑定。\n\n是否继续？", "回收项目暂存", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await Run(async () => {
            // Bounded owner-thread commit prevents an intervening collection/Undo change from creating a new reference.
            var moved = ProjectStagingService.Recycle(_store, preview, paths); await Scan(); Status.Text = $"已将 {moved} 个原件移入项目回收区，可恢复最近一批。";
        });
    }
    private async void Restore_Click(object sender, RoutedEventArgs e) => await Run(async () => {
        var restored = await ProjectStagingService.RestoreLatestAsync(_store.DataRoot); await Scan();
        Status.Text = restored == 0 ? "没有可恢复文件，或原位置已存在文件。回收内容保留，不覆盖。" : $"已恢复 {restored} 个暂存原件，没有覆盖已有文件。";
    });
    private async Task Run(Func<Task> action)
    {
        if (_busy) return; _busy = true; Busy.Visibility = Visibility.Visible; UpdateButtons();
        try { await action(); } catch (Exception ex) { _preview = null; Status.Text = "未完成：" + ex.Message + " 已移动的文件仍保留在项目回收区，可恢复。"; }
        finally { _busy = false; Busy.Visibility = Visibility.Collapsed; UpdateButtons(); if (_closeAfter) Close(); }
    }
    private void UpdateButtons()
    {
        if (ScanButton == null) return;
        ScanButton.IsEnabled = RestoreButton.IsEnabled = !_busy;
        Files.IsEnabled = !_busy; SelectButton.IsEnabled = !_busy && _preview?.Complete == true && _preview.Files.Length > 0;
        RecycleButton.IsEnabled = !_busy && _preview?.Complete == true && Files.SelectedItems.Count is >0 and <=200;
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
