using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using Liuzhuan.Utils;

namespace Liuzhuan.Services;

/// <summary>
/// 剪贴板监控（电脑端）：AddClipboardFormatListener 事件驱动
/// 电脑上任意应用复制（Ctrl+C）→ 自动读取剪贴板文本 → 回调给 MainWindow 加载进流转
/// 使用 AddClipboardFormatListener（Win7+）而非轮询，零开销、即时响应
/// </summary>
public class ClipboardMonitorService
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    private HwndSource? _source;
    private Action<string>? _onText;
    private string _lastText = "";
    private IntPtr _hwnd;

    /// <summary>启动监听（需要窗口句柄；开启时自动读取一次当前剪贴板）</summary>
    public void Start(IntPtr hwnd, Action<string> onText, bool readCurrent = true)
    {
        Stop();
        _hwnd = hwnd;
        _onText = onText;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
        if (AddClipboardFormatListener(hwnd))
        {
            Logger.Run("ClipboardMonitor: listening (WM_CLIPBOARDUPDATE)");
            // 开启瞬间读取一次当前剪贴板
            if (readCurrent) ReadClipboard("start");
        }
        else
        {
            Logger.Error("ClipboardMonitor: AddClipboardFormatListener failed");
        }
    }

    public void Stop()
    {
        if (_source != null)
        {
            RemoveClipboardFormatListener(_hwnd);
            _source.RemoveHook(WndProc);
            _source = null;
        }
        _onText = null;
        _hwnd = IntPtr.Zero;
        Logger.Run("ClipboardMonitor: stopped");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            ReadClipboard("update");
        }
        return IntPtr.Zero;
    }

    private void ReadClipboard(string trigger)
    {
        try
        {
            if (AppSettings.IsClipboardPaused || AppSettings.ClipboardPrivacyLoadError != null) return;
            var clipboardSequence = GetClipboardSequenceNumber();
            var owner = GetClipboardOwner();
            GetWindowThreadProcessId(owner, out var processId);
            // Do not recapture this app's explicit copy operations.
            if (processId == Environment.ProcessId) return;
            string? ownerApp = null;
            if (processId > 0)
            {
                try { using var process = Process.GetProcessById((int)processId); ownerApp = process.ProcessName + ".exe"; }
                catch { /* exited or inaccessible owner remains unknown */ }
            }
            if (!ClipboardPrivacyPolicy.ShouldCapture(DateTimeOffset.UtcNow, AppSettings.ClipboardPausedUntil,
                AppSettings.ExcludedClipboardApps, ownerApp)) return;
            var text = Clipboard.GetText().Trim();
            // Ownership and content must come from the same clipboard update.
            if (clipboardSequence != GetClipboardSequenceNumber()) return;
            // Delayed clipboard rendering can pump STA messages, including a privacy change.
            if (AppSettings.ClipboardPrivacyLoadError != null || !ClipboardPrivacyPolicy.ShouldCapture(
                DateTimeOffset.UtcNow, AppSettings.ClipboardPausedUntil, AppSettings.ExcludedClipboardApps, ownerApp)) return;
            if (text.Length < 2) return;
            // Suppress duplicate notifications, while retaining distinct rapid copies.
            if (text == _lastText) return;

            _lastText = text;
            Logger.Run("ClipboardMonitor: captured ({0}) len={1}", trigger, text.Length);
            _onText?.Invoke(text);
        }
        catch (Exception ex)
        {
            // 剪贴板被占用时 GetText 可能抛 COMException，忽略重试即可
            Logger.Error("ClipboardMonitor read failed: {0}", ex.Message);
        }
    }

    #region Win32
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
    #endregion
}
