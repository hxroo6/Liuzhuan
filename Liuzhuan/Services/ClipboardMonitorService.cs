using System.Runtime.InteropServices;
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
    private long _lastTime;

    /// <summary>启动监听（需要窗口句柄；开启时自动读取一次当前剪贴板）</summary>
    public void Start(IntPtr hwnd, Action<string> onText)
    {
        Stop();
        _onText = onText;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
        if (AddClipboardFormatListener(hwnd))
        {
            Logger.Run("ClipboardMonitor: listening (WM_CLIPBOARDUPDATE)");
            // 开启瞬间读取一次当前剪贴板
            ReadClipboard("start");
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
            _source.RemoveHook(WndProc);
            _source = null;
        }
        _onText = null;
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
            var text = Clipboard.GetText().Trim();
            if (text.Length < 2) return;

            var now = Environment.TickCount64;
            // 去重：内容相同（流转自身复制也会触发）或 600ms 内重复 → 忽略
            if (text == _lastText) return;
            if (now - _lastTime < 600) return;

            _lastText = text;
            _lastTime = now;
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
    #endregion
}
