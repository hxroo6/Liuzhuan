using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Liuzhuan.Services;
using Liuzhuan.Utils;

namespace Liuzhuan.Views;

public partial class ImageProcessingWindow : Window
{
    public sealed record ResultRow(string Name, string Detail, string Mark, Brush Color);
    private readonly IReadOnlyList<ImageProcessingSource> _sources;
    private readonly ObservableCollection<ResultRow> _rows = new();
    private CancellationTokenSource? _cancellation;
    private string? _savedDirectory;
    private bool _closeWhenFinished;
    public ImageProcessingResult? LastResult { get; private set; }

    public ImageProcessingWindow(IReadOnlyList<ImageProcessingSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = Array.AsReadOnly(sources.ToArray());
        InitializeComponent();
        SelectionCount.Text = $"{_sources.Count} 张图片";
        Results.ItemsSource = _rows;
        Destination.Text = DefaultDestination();
        UpdateFormat();
        Closing += (_, eventArgs) =>
        {
            if (_cancellation == null) return;
            eventArgs.Cancel = true;
            _closeWhenFinished = true;
            CancelProcessing();
        };
    }

    private string DefaultDestination()
    {
        try
        {
            var directory = _sources.FirstOrDefault()?.FilePath is string path ? Path.GetDirectoryName(Path.GetFullPath(path)) : null;
            if (directory != null && Directory.Exists(directory)) return directory;
        }
        catch { }
        return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    }

    private void Format_Changed(object sender, RoutedEventArgs eventArgs) { if (Quality != null) UpdateFormat(); }
    private void UpdateFormat()
    {
        var jpeg = JpegFormat.IsChecked == true;
        Quality.IsEnabled = jpeg && _cancellation == null;
        QualityValue.Opacity = jpeg ? 1 : 0.45;
        QualityHint.Text = jpeg ? "通常 80–90 可兼顾细节和大小，最终体积以处理结果为准。" : "PNG 保留透明度，使用无损压缩，质量滑块不参与处理。";
        FormatHint.Text = jpeg ? "JPEG 适合照片，质量可调；透明区域铺白底。" : "PNG 适合透明图片、截图与需要清晰边缘的素材。";
    }
    private void Resize_Changed(object sender, RoutedEventArgs eventArgs) { if (MaxEdge != null) MaxEdge.IsEnabled = ResizeEnabled.IsChecked == true; }
    private void Quality_Changed(object sender, RoutedPropertyChangedEventArgs<double> eventArgs) { if (QualityValue != null) QualityValue.Text = ((int)Math.Round(eventArgs.NewValue)).ToString(); }
    private void Preset_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is Button button) { ResizeEnabled.IsChecked = true; MaxEdge.Text = button.Tag?.ToString() ?? "1920"; }
    }
    private void Browse_Click(object sender, RoutedEventArgs eventArgs)
    {
        try { var folder = FolderPicker.Show(this); if (folder != null) Destination.Text = folder; }
        catch (Exception exception) { Status.Text = "无法打开选择器，可直接输入文件夹路径：" + exception.Message; }
    }

    private async void Process_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (_cancellation != null) return;
        int? maxEdge = null;
        if (ResizeEnabled.IsChecked == true)
        {
            if (!int.TryParse(MaxEdge.Text.Trim(), out var parsed) || parsed is < 1 or > 32768)
            { Status.Text = "最长边请输入 1–32768 之间的整数。"; MaxEdge.Focus(); return; }
            maxEdge = parsed;
        }
        var parent = Destination.Text.Trim();
        if (parent.Length == 0) { Status.Text = "请选择或输入保存位置。"; Destination.Focus(); return; }
        var options = new ImageProcessingOptions(JpegFormat.IsChecked == true ? ImageOutputFormat.Jpeg : ImageOutputFormat.Png,
            maxEdge, (int)Math.Round(Quality.Value));
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        LastResult = null;
        _savedDirectory = null;
        _rows.Clear();
        ResultsPanel.Visibility = Visibility.Collapsed;
        OpenButton.Visibility = Visibility.Collapsed;
        SetBusy(true);
        Progress.Value = 0;
        Status.Text = "正在准备图片…";
        var progress = new Progress<ImageProcessingProgress>(value =>
        {
            if (!ReferenceEquals(_cancellation, cancellation) || cancellation.IsCancellationRequested) return;
            Progress.Value = 100.0 * value.CompletedItems / Math.Max(1, value.TotalItems);
            Status.Text = $"{value.CompletedItems} / {value.TotalItems} · {value.Stage} · {value.CurrentName}";
            if (value.ItemResult != null) { ResultsPanel.Visibility = Visibility.Visible; _rows.Add(ToRow(value.ItemResult)); }
        });
        try
        {
            LastResult = await ImageProcessingService.ProcessAsync(parent, _sources, options, progress, cancellation.Token);
            _savedDirectory = LastResult.DirectoryPath;
            // 后台进度回调可能仍在排队，以最终结果重建一次，避免缺行或误报成功。
            _rows.Clear();
            foreach (var item in LastResult.Items) _rows.Add(ToRow(item));
            ResultsPanel.Visibility = Visibility.Visible;
            Status.Text = $"已生成 {LastResult.SuccessCount} 张图片" + (LastResult.FailureCount > 0 ? $"，{LastResult.FailureCount} 张失败，请查看结果。" : "，原图已保留。")
                + (_savedDirectory != null ? "\n" + _savedDirectory : "");
            Progress.Value = 100;
            OpenButton.Visibility = _savedDirectory != null ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException) { _rows.Clear(); ResultsPanel.Visibility = Visibility.Collapsed; Status.Text = "已取消；本次生成的图片已尽力清理，原图保持完整。"; }
        catch (Exception exception) { Status.Text = "处理未完成：" + exception.Message; }
        finally
        {
            _cancellation = null;
            SetBusy(false);
            if (_closeWhenFinished) Close();
        }
    }

    private ResultRow ToRow(ImageProcessingItemResult item)
    {
        if (!item.Succeeded) return new(item.DisplayName, item.Error ?? "图片处理失败。", "!", (Brush)FindResource("ErrorBrush"));
        var difference = item.InputBytes > 0 ? 100.0 * (item.OutputBytes - item.InputBytes) / item.InputBytes : 0;
        var change = difference < 0 ? $"减小 {-difference:F0}%" : difference > 0 ? $"增加 {difference:F0}%" : "体积相近";
        return new(item.DisplayName, $"{item.SourceWidth} × {item.SourceHeight} → {item.OutputWidth} × {item.OutputHeight} · {FileClassifier.FormatSize(item.OutputBytes)} · {change}", "✓", (Brush)FindResource("AccentBrush"));
    }
    private void SetBusy(bool busy)
    {
        ProcessButton.IsEnabled = BrowseButton.IsEnabled = Destination.IsEnabled = OptionsPanel.IsEnabled = JpegFormat.IsEnabled = PngFormat.IsEnabled = !busy;
        CloseButton.IsEnabled = true;
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.Content = busy ? "取消处理" : "关闭";
        UpdateFormat();
    }
    private void CancelProcessing()
    {
        _cancellation?.Cancel();
        CloseButton.IsEnabled = false;
        Status.Text = "正在取消，等待当前图片编解码结束…";
    }
    private void Close_Click(object sender, RoutedEventArgs eventArgs) { if (_cancellation == null) Close(); else CancelProcessing(); }
    private void Open_Click(object sender, RoutedEventArgs eventArgs)
    {
        try { if (_savedDirectory != null) Process.Start(new ProcessStartInfo(_savedDirectory) { UseShellExecute = true }); }
        catch (Exception exception) { Status.Text = "无法打开文件夹：" + exception.Message; }
    }
}
