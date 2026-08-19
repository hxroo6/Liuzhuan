using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Liuzhuan.Services;

/// <summary>
/// 全局热键服务 — 使用 Win32 RegisterHotKey API 注册系统级快捷键
/// </summary>
public class HotkeyService : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // 修饰键
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    private IntPtr _hwnd;
    private int _currentId = 9000;
    private Action? _callback;

    /// <summary>注册全局热键</summary>
    /// <param name="window">WPF 窗口（需要通过 HWND 消息接收热键）</param>
    /// <param name="key">主键（如 Key.Q）</param>
    /// <param name="modifiers">修饰键组合（如 Ctrl+Alt）</param>
    /// <param name="callback">触发时的回调</param>
    public bool Register(Window window, Key key, ModifierKeys modifiers, Action callback)
    {
        Unregister();

        var helper = new System.Windows.Interop.WindowInteropHelper(window);
        _hwnd = helper.Handle;
        _callback = callback;
        _currentId = 9000;

        uint mod = MOD_NOREPEAT;
        if ((modifiers & ModifierKeys.Alt) != 0) mod |= MOD_ALT;
        if ((modifiers & ModifierKeys.Control) != 0) mod |= MOD_CONTROL;
        if ((modifiers & ModifierKeys.Shift) != 0) mod |= MOD_SHIFT;
        if ((modifiers & ModifierKeys.Windows) != 0) mod |= MOD_WIN;

        var vk = KeyInterop.VirtualKeyFromKey(key);
        var ok = RegisterHotKey(_hwnd, _currentId, mod, (uint)vk);

        if (ok)
        {
            // 安装消息过滤器拦截 WM_HOTKEY
            ComponentDispatcher.ThreadFilterMessage += OnThreadFilterMessage;
            Utils.Logger.Run("HotkeyService: registered {0}+{1} (id={2})", modifiers, key, _currentId);
        }
        else
        {
            Utils.Logger.Error("HotkeyService: failed to register {0}+{1}", modifiers, key);
        }

        return ok;
    }

    private void OnThreadFilterMessage(ref MSG msg, ref bool handled)
    {
        const int WM_HOTKEY = 0x0312;
        if (msg.message == WM_HOTKEY && msg.wParam == (IntPtr)_currentId)
        {
            _callback?.Invoke();
            handled = true;
        }
    }

    public void Unregister()
    {
        if (_hwnd != IntPtr.Zero && _currentId != 9000)
        {
            UnregisterHotKey(_hwnd, _currentId);
            _currentId = 9000;
        }
        ComponentDispatcher.ThreadFilterMessage -= OnThreadFilterMessage;
    }

    public void Dispose()
    {
        Unregister();
    }
}
