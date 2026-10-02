using System.Diagnostics;
using System.Windows;
using Liuzhuan.Services;
using Liuzhuan.Utils;

namespace Liuzhuan.Views;

public partial class MaterialExportWindow : Window
{
    private readonly IReadOnlyList<MaterialExportSource> _items;
    private CancellationTokenSource? _cancellation;
    private string? _savedDirectory;
    private bool _closeWhenFinished;

    public MaterialExportWindow(IReadOnlyList<MaterialExportSource> items)
    {
        InitializeComponent();
        _items = items;
        Summary.Text = $"已选择 {items.Count} 项素材，文件和文字可以一起另存。";
        Destination.Text = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        Closing += (_, e) =>
        {
            if (_cancellation == null) return;
            e.Cancel = true;
            _closeWhenFinished = true;
            _cancellation.Cancel();
            Status.Text = "正在取消，请稍候…";
        };
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        try { var path = FolderPicker.Show(this); if (path != null) Destination.Text = path; }
        catch (Exception ex) { Status.Text = "无法打开选择器，可直接输入文件夹路径：" + ex.Message; }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_cancellation != null) return;
        var parent = Destination.Text.Trim();
        if (string.IsNullOrWhiteSpace(parent)) { Status.Text = "请选择或输入保存位置。"; Destination.Focus(); return; }
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        _savedDirectory = null;
        SaveButton.IsEnabled = BrowseButton.IsEnabled = Destination.IsEnabled = false;
        OpenButton.Visibility = Visibility.Collapsed;
        CloseButton.Content = "取消操作";
        Progress.Visibility = Visibility.Visible;
        Progress.Value = 0;
        Status.Text = "正在准备素材…";
        var progress = new Progress<MaterialExportProgress>(p =>
        {
            if (!ReferenceEquals(_cancellation, cancellation) || cancellation.IsCancellationRequested) return;
            Progress.Value = p.TotalBytes > 0 ? 100.0 * p.CopiedBytes / p.TotalBytes : 100.0 * p.CompletedItems / Math.Max(1, p.TotalItems);
            Status.Text = $"{p.CompletedItems} / {p.TotalItems} 项 · {p.CurrentName}";
        });
        try
        {
            var result = await MaterialExportService.ExportAsync(parent, _items, progress, cancellation.Token);
            _savedDirectory = result.DirectoryPath;
            Status.Text = $"已另存 {result.ItemCount} 项素材。\n{result.DirectoryPath}";
            Progress.Value = 100;
            OpenButton.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) { Status.Text = "已取消另存。"; }
        catch (Exception ex) { Status.Text = "另存未完成：" + ex.Message; }
        finally
        {
            _cancellation = null;
            SaveButton.IsEnabled = BrowseButton.IsEnabled = Destination.IsEnabled = true;
            CloseButton.Content = "关闭";
            Progress.Visibility = Visibility.Collapsed;
            if (_closeWhenFinished) Close();
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        try { if (_savedDirectory != null) Process.Start(new ProcessStartInfo(_savedDirectory) { UseShellExecute = true }); }
        catch (Exception ex) { Status.Text = "无法打开文件夹：" + ex.Message; }
    }
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_cancellation == null) Close();
        else { _cancellation.Cancel(); Status.Text = "正在取消，请稍候…"; }
    }
}
