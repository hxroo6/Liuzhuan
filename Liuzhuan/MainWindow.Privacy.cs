using System.Windows;
using System.Windows.Threading;
using Liuzhuan.Services;

namespace Liuzhuan;

public partial class MainWindow
{
    private DispatcherTimer? _privacyDeadlineTimer;
    private void RestoreClipboardMonitor()
    {
        if (!AppSettings.ClipboardMonitorEnabled) return;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        _clipMonitor.Start(hwnd, text =>
        {
            _dataStore.Add(DragDropService.CreateTextItem(text));
            RefreshView();
        }, readCurrent: false);
    }
    private void ClipboardPrivacy_Click(object sender, RoutedEventArgs e)
    {
        ShowToolDialog(new Views.ClipboardPrivacyWindow { Owner = this });
        UpdatePrivacyStatus();
    }
    private void UpdatePrivacyStatus()
    {
        _privacyDeadlineTimer?.Stop();
        if (ClipboardPrivacyBanner == null) return;
        var blocked = AppSettings.ClipboardPrivacyLoadError != null;
        var paused = AppSettings.IsClipboardPaused;
        ClipboardPrivacyBanner.Visibility = AppSettings.ClipboardMonitorEnabled && (blocked || paused) ? Visibility.Visible : Visibility.Collapsed;
        ClipboardPrivacyBanner.Content = blocked ? "自动采集已阻止 · 检查隐私设置" : paused ?
            $"自动采集暂停至 {AppSettings.ClipboardPausedUntil!.Value.LocalDateTime:HH:mm} · 点击管理" : "";
        if (paused)
        {
            var remaining = AppSettings.ClipboardPausedUntil!.Value - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(50);
            if (remaining <= TimeSpan.Zero) return;
            _privacyDeadlineTimer ??= new DispatcherTimer();
            _privacyDeadlineTimer.Interval = remaining > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : remaining;
            _privacyDeadlineTimer.Tick -= PrivacyDeadline_Tick;
            _privacyDeadlineTimer.Tick += PrivacyDeadline_Tick;
            _privacyDeadlineTimer.Start();
        }
    }
    private void PrivacyDeadline_Tick(object? sender, EventArgs e) => UpdatePrivacyStatus();
}
