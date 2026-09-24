using System.IO;
using System.Windows;
using Liuzhuan.Services;
using Microsoft.Win32;

namespace Liuzhuan.Views;

public partial class MigrationWindow : Window
{
    private readonly Func<MigrationService.Manifest> _snapshot;
    private readonly Action _exit;
    private CancellationTokenSource? _cancellation;
    private bool _pending;
    public MigrationWindow(Func<MigrationService.Manifest> snapshot, Action exit)
    {
        InitializeComponent(); _snapshot = snapshot; _exit = exit;
        Closing += (_, e) => { if (_cancellation != null) { e.Cancel = true; _cancellation.Cancel(); Status.Text = "正在取消，请稍候…"; } };
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "导出配置与素材", Filter = "流转迁移包 (*.liuzhuan.zip)|*.liuzhuan.zip", FileName = "流转迁移-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".liuzhuan.zip", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        await Run(async (progress, ct) =>
        {
            var snapshot = _snapshot();
            await Task.Run(() => MigrationService.ExportAsync(dialog.FileName, snapshot, progress, ct), ct);
            Status.Text = $"已导出 {snapshot.Items.Count} 项素材和配置。\n{dialog.FileName}";
        });
    }
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择迁移包", Filter = "流转迁移包 (*.zip)|*.zip", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        await Run(async (progress, ct) =>
        {
            var summary = await Task.Run(() => MigrationService.ReadManifest(dialog.FileName), ct);
            ct.ThrowIfCancellationRequested();
            var message = $"迁移包包含 {summary.Items.Count} 项素材，文件约 {summary.Files.Sum(f => f.Size) / 1048576.0:F1} MB。\n\n将恢复配对口令、局域网端口、HEIC 转换、剪贴板监控和开机自启设置。\n\n重启后切换到导入的素材库，原库保留，不合并。是否继续？";
            if (MessageBox.Show(this, message, "确认导入内容", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { Status.Text = "已取消导入。"; return; }
            var destination = await Task.Run(() => MigrationService.PrepareImportAsync(dialog.FileName, App.DataDir, progress, ct, summary), ct);
            _pending = true;
            Status.Text = "导入已校验完成。请退出并重新打开流转以应用配置和素材；当前仍是原素材库。\n导入目录：" + destination;
            FinishButton.Visibility = Visibility.Visible;
        });
    }
    private async Task Run(Func<IProgress<string>, CancellationToken, Task> action)
    {
        if (_cancellation != null) return;
        using var cancellation = new CancellationTokenSource(); _cancellation = cancellation;
        ExportButton.IsEnabled = ImportButton.IsEnabled = CloseButton.IsEnabled = false;
        CancelButton.Visibility = Progress.Visibility = Visibility.Visible;
        Status.Text = "正在准备，请稍候…";
        var progress = new Progress<string>(message => { if (ReferenceEquals(_cancellation, cancellation) && !cancellation.IsCancellationRequested) Status.Text = message; });
        try { await action(progress, cancellation.Token); }
        catch (OperationCanceledException) { Status.Text = "已取消，原素材库未改变。"; }
        catch (Exception ex) { Status.Text = "未完成：" + ex.Message + "\n原素材库未改变，可重试。"; }
        finally
        {
            _cancellation = null;
            ExportButton.IsEnabled = ImportButton.IsEnabled = !_pending;
            CloseButton.IsEnabled = true;
            CancelButton.Visibility = Progress.Visibility = Visibility.Collapsed;
        }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) { _cancellation?.Cancel(); Status.Text = "正在取消，请稍候…"; }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Finish_Click(object sender, RoutedEventArgs e) { Close(); _exit(); }
}
