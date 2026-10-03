using System.Windows;
using System.Windows.Threading;
using Liuzhuan.Services;

namespace Liuzhuan.Views;

public partial class ClipboardPrivacyWindow : Window
{
    private readonly DispatcherTimer _deadline = new();
    public ClipboardPrivacyWindow()
    {
        InitializeComponent();
        AppsText.Text = string.Join(Environment.NewLine, AppSettings.ExcludedClipboardApps);
        ErrorText.Text = AppSettings.ClipboardPrivacyLoadError ?? "";
        _deadline.Tick += (_, _) => { _deadline.Stop(); UpdateState(); };
        Closed += (_, _) => _deadline.Stop();
        UpdateState();
    }
    private void UpdateState()
    {
        _deadline.Stop();
        StateText.Text = AppSettings.ClipboardPrivacyLoadError != null ? "配置需要检查 · 自动采集已阻止" :
            !AppSettings.ClipboardMonitorEnabled ? "自动采集开关已关闭（可在设置菜单开启）" :
            AppSettings.IsClipboardPaused ? $"已暂停 · {AppSettings.ClipboardPausedUntil!.Value.LocalDateTime:HH:mm} 自动恢复" : "自动采集运行中";
        if (AppSettings.IsClipboardPaused)
        {
            var remaining = AppSettings.ClipboardPausedUntil!.Value - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(50);
            if (remaining > TimeSpan.Zero) { _deadline.Interval = remaining > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : remaining; _deadline.Start(); }
        }
    }
    private bool Save(DateTimeOffset? deadline)
    {
        try
        {
            AppSettings.ConfigurePrivacy(AppsText.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries), deadline);
            ErrorText.Text = ""; UpdateState(); return true;
        }
        catch (Exception ex) { ErrorText.Text = ex.Message; return false; }
    }
    private void Pause15_Click(object sender, RoutedEventArgs e) => Save(DateTimeOffset.UtcNow.AddMinutes(15));
    private void PauseHour_Click(object sender, RoutedEventArgs e) => Save(DateTimeOffset.UtcNow.AddHours(1));
    private void Resume_Click(object sender, RoutedEventArgs e) => Save(null);
    private void Save_Click(object sender, RoutedEventArgs e) { if (Save(AppSettings.ClipboardPausedUntil)) Close(); }
}
