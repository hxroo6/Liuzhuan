using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Liuzhuan.Models;
using Liuzhuan.Services;
using Liuzhuan.Utils;

namespace Liuzhuan.Views;

public partial class ImageProcessingWindow : Window
{
    public sealed record ResultRow(string Name, string Detail, string Mark, Brush Color);
    private readonly IReadOnlyList<ImageProcessingSource> _sources;
    private readonly ObservableCollection<ResultRow> _rows = new();
    private readonly ImageProcessingPresetStore _presetStore;
    private string? _selectedPresetName;
    private Action<string>? _savePresetName;
    private Action? _confirmPresetAction;
    private bool _presetEditing;
    private CancellationTokenSource? _cancellation;
    private string? _savedDirectory;
    private bool _closeWhenFinished;
    public ImageProcessingResult? LastResult { get; private set; }

    public ImageProcessingWindow(IReadOnlyList<ImageProcessingSource> sources, ImageProcessingPresetStore? presetStore = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = Array.AsReadOnly(sources.ToArray());
        _presetStore = presetStore ?? new ImageProcessingPresetStore();
        InitializeComponent();
        SelectionCount.Text = $"{_sources.Count} 张图片";
        Results.ItemsSource = _rows;
        Destination.Text = DefaultDestination();
        UpdateFormat();
        UpdatePresetHeader();
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
        UpdatePresetHeader();
    }
    private void Resize_Changed(object sender, RoutedEventArgs eventArgs) { if (MaxEdge != null) MaxEdge.IsEnabled = ResizeEnabled.IsChecked == true; UpdatePresetHeader(); }
    private void Quality_Changed(object sender, RoutedPropertyChangedEventArgs<double> eventArgs) { if (QualityValue != null) QualityValue.Text = ((int)Math.Round(eventArgs.NewValue)).ToString(); UpdatePresetHeader(); }
    private void Parameters_Changed(object sender, TextChangedEventArgs eventArgs) => UpdatePresetHeader();
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
        if (_cancellation != null || _presetEditing) return;
        ImageProcessingOptions options;
        try { options = ReadOptions(); }
        catch (ArgumentException exception) { Status.Text = exception.Message; MaxEdge.Focus(); return; }
        var parent = Destination.Text.Trim();
        if (parent.Length == 0) { Status.Text = "请选择或输入保存位置。"; Destination.Focus(); return; }
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

    private ImageProcessingOptions ReadOptions()
    {
        int? maxEdge = null;
        if (ResizeEnabled.IsChecked == true)
        {
            if (!int.TryParse(MaxEdge.Text.Trim(), out var parsed) || parsed is < 1 or > 32768)
                throw new ArgumentException("最长边请输入 1–32768 之间的整数。");
            maxEdge = parsed;
        }
        return new ImageProcessingOptions(JpegFormat.IsChecked == true ? ImageOutputFormat.Jpeg : ImageOutputFormat.Png,
            maxEdge, (int)Math.Round(Quality.Value));
    }

    private void UpdatePresetHeader()
    {
        if (PresetHint == null || Quality == null || MaxEdge == null) return;
        var selected = _presetStore.Presets.FirstOrDefault(preset => preset.Name.Equals(_selectedPresetName, StringComparison.OrdinalIgnoreCase));
        var readable = _presetStore.LoadError == null;
        PresetLabel.Text = selected?.Name ?? "选择常用预设";
        PresetChooseButton.IsEnabled = readable && _presetStore.Presets.Count > 0;
        SavePresetButton.IsEnabled = readable && _presetStore.Presets.Count < ImageProcessingPresetStore.MaxPresets;
        ManagePresetButton.IsEnabled = readable && selected != null;
        RepairPresetButton.Visibility = readable ? Visibility.Collapsed : Visibility.Visible;
        PresetHint.Foreground = (Brush)FindResource(readable ? "TextDimBrush" : "ErrorBrush");
        if (!readable) { PresetHint.Text = "预设读取失败，原文件已保留；仍可手动处理图片。"; PresetHint.ToolTip = _presetStore.LoadError; return; }
        if (selected == null) PresetHint.Text = $"已保存 {_presetStore.Presets.Count} / {ImageProcessingPresetStore.MaxPresets} 个。选择只填充参数，不会立即处理。";
        else
        {
            bool matches;
            try { matches = selected.Options == ReadOptions(); }
            catch (ArgumentException) { matches = false; }
            PresetHint.Text = selected.Summary + (matches ? " · 已填充" : " · 当前参数已修改");
        }
    }
    private void ApplyPreset(ImageProcessingPreset preset)
    {
        _selectedPresetName = preset.Name;
        JpegFormat.IsChecked = preset.Format == ImageOutputFormat.Jpeg;
        PngFormat.IsChecked = preset.Format == ImageOutputFormat.Png;
        ResizeEnabled.IsChecked = preset.MaxEdge.HasValue;
        if (preset.MaxEdge.HasValue) MaxEdge.Text = preset.MaxEdge.Value.ToString();
        Quality.Value = preset.JpegQuality;
        UpdatePresetHeader();
        Status.Text = $"已填充「{preset.Name}」；点击生成图片副本开始处理。";
    }
    private ContextMenu NewPresetMenu(UIElement target) => new() { PlacementTarget = target, Placement = PlacementMode.Bottom };
    private ContextMenu BuildPresetMenu()
    {
        var menu = NewPresetMenu(PresetChooseButton);
        foreach (var preset in _presetStore.Presets)
        {
            var item = new MenuItem { Header = preset.Name + "\n" + preset.Summary, IsCheckable = true,
                IsChecked = preset.Name.Equals(_selectedPresetName, StringComparison.OrdinalIgnoreCase) };
            item.Click += (_, _) => ApplyPreset(preset);
            menu.Items.Add(item);
        }
        return menu;
    }
    private void ChoosePreset_Click(object sender, RoutedEventArgs eventArgs)
    {
        var menu = BuildPresetMenu(); PresetChooseButton.ContextMenu = menu; menu.IsOpen = true;
    }
    private void SavePreset_Click(object sender, RoutedEventArgs eventArgs)
    {
        try
        {
            var options = ReadOptions();
            BeginPresetName("给这组参数命名", "", ImageProcessingPresetStore.Create("预览", options).Summary,
                name => { var preset = ImageProcessingPresetStore.Create(name, options); _presetStore.Add(preset); _selectedPresetName = preset.Name; Status.Text = $"已保存「{preset.Name}」。"; });
        }
        catch (Exception exception) { Status.Text = "未保存预设：" + exception.Message; }
    }
    private ContextMenu BuildManagePresetMenu()
    {
        var name = _selectedPresetName;
        if (name == null) throw new InvalidOperationException("请先选择图片处理预设。");
        var menu = NewPresetMenu(ManagePresetButton);
        var update = new MenuItem { Header = "用当前参数更新此预设" };
        update.Click += (_, _) => { try { _presetStore.Update(name, ReadOptions()); UpdatePresetHeader(); Status.Text = $"已更新「{name}」。"; } catch (Exception exception) { Status.Text = "未更新预设：" + exception.Message; } };
        var rename = new MenuItem { Header = "重命名…" };
        rename.Click += (_, _) => BeginPresetName("给预设换个名字", name, "仅修改名称，保留原来的处理参数。", newName =>
        { _presetStore.Rename(name, newName); _selectedPresetName = newName.Trim(); Status.Text = "预设已重命名。"; });
        var remove = new MenuItem { Header = "删除此预设…", Foreground = (Brush)FindResource("ErrorBrush") };
        remove.Click += (_, _) => BeginPresetConfirm("删除「" + name + "」？", "只删除这组常用参数，当前参数和图片保持不变。", "删除预设",
            () => { _presetStore.Remove(name); _selectedPresetName = null; Status.Text = "已删除预设，当前处理参数保持不变。"; });
        menu.Items.Add(update); menu.Items.Add(rename); menu.Items.Add(new Separator()); menu.Items.Add(remove);
        return menu;
    }
    private void ManagePreset_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (_selectedPresetName == null) return;
        var menu = BuildManagePresetMenu(); ManagePresetButton.ContextMenu = menu; menu.IsOpen = true;
    }
    private void RepairPreset_Click(object sender, RoutedEventArgs eventArgs) => BeginPresetConfirm("保留副本并重建预设？",
        "先保存并校验原预设文件的副本，再建立空预设。已有图片、手动参数和处理结果保持不变。", "保留副本并重建",
        () => { var backup = _presetStore.RebuildAfterLoadError(); _selectedPresetName = null; Status.Text = "预设已重建，原文件副本：" + backup; });
    private void BeginPresetName(string title, string name, string hint, Action<string> save)
    {
        _savePresetName = save; _confirmPresetAction = null; _presetEditing = true;
        PresetNamePanel.Visibility = Visibility.Visible; PresetConfirmPanel.Visibility = Visibility.Collapsed;
        PresetEditorTitle.Text = title; PresetNameInput.Text = name; PresetEditorHint.Text = hint;
        PresetEditorHint.Foreground = (Brush)FindResource("TextDimBrush");
        Status.Text = "填写名称保存这组参数，或取消编辑。";
        SetBusy(false); PresetNameInput.Focus(); PresetNameInput.SelectAll();
    }
    private void BeginPresetConfirm(string title, string hint, string label, Action action)
    {
        _confirmPresetAction = action; _savePresetName = null; _presetEditing = true;
        PresetNamePanel.Visibility = Visibility.Collapsed; PresetConfirmPanel.Visibility = Visibility.Visible;
        PresetConfirmTitle.Text = title; PresetConfirmHint.Text = hint; PresetConfirmButton.Content = label;
        PresetConfirmHint.Foreground = (Brush)FindResource("TextDimBrush");
        Status.Text = "请确认预设操作，或取消返回。";
        SetBusy(false);
    }
    private void EndPresetEdit()
    {
        _presetEditing = false; _savePresetName = null; _confirmPresetAction = null;
        PresetNamePanel.Visibility = PresetConfirmPanel.Visibility = Visibility.Collapsed;
        SetBusy(false); UpdatePresetHeader();
    }
    private void CancelPresetEdit_Click(object sender, RoutedEventArgs eventArgs) { EndPresetEdit(); Status.Text = "已取消预设编辑，处理参数保持不变。"; }
    private void SavePresetName_Click(object sender, RoutedEventArgs eventArgs)
    {
        try { _savePresetName?.Invoke(PresetNameInput.Text); EndPresetEdit(); }
        catch (Exception exception) { PresetEditorHint.Text = "未保存：" + exception.Message; PresetEditorHint.Foreground = (Brush)FindResource("ErrorBrush"); }
    }
    private void ConfirmPreset_Click(object sender, RoutedEventArgs eventArgs)
    {
        try { _confirmPresetAction?.Invoke(); EndPresetEdit(); }
        catch (Exception exception) { PresetConfirmHint.Text = "未完成：" + exception.Message; PresetConfirmHint.Foreground = (Brush)FindResource("ErrorBrush"); }
    }
    private void PresetName_KeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Enter) { SavePresetName_Click(sender, new RoutedEventArgs()); eventArgs.Handled = true; }
        else if (eventArgs.Key == Key.Escape) { CancelPresetEdit_Click(sender, new RoutedEventArgs()); eventArgs.Handled = true; }
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
        ProcessButton.IsEnabled = OptionsPanel.IsEnabled = JpegFormat.IsEnabled = PngFormat.IsEnabled = !busy && !_presetEditing;
        BrowseButton.IsEnabled = Destination.IsEnabled = !busy;
        PresetTools.IsEnabled = !busy && !_presetEditing;
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
