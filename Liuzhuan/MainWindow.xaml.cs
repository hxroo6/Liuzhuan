using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using Liuzhuan.Models;
using Liuzhuan.Services;
using Liuzhuan.Services.Lan;
using Liuzhuan.Utils;

namespace Liuzhuan;

public partial class MainWindow : Window
{
    private readonly DataStore _dataStore;
    private readonly ObservableCollection<MaterialItem> _currentView = new();
    private readonly HotkeyService _hotkeyService = new();
    private readonly LanServer _lanServer = new();
    private readonly ClipboardMonitorService _clipMonitor = new();

    private string _currentTab = "Recent";
    private string _searchKeyword = "";
    private bool _isExpanded = true;
    private DispatcherTimer? _collapseTimer;
    private DispatcherTimer? _hotspotTimer;

    // 拖出相关
    private Point _dragStartPoint;
    private bool _isDragging;
    private MaterialItem? _dragItem;
    private DateTime _clickStartTime;
    private DateTime _lastClickTime;
    private MaterialItem? _lastClickItem;

    // 面板参数
    private const double PanelWidth = 340;
    private const double CollapsedVisible = 8;
    private const double CollapseDelayMs = 1500;

    private double _expandedLeft;
    private double _collapsedLeft;
    private bool _isLeftAnchored; // 左吸附模式：不自动收起、不跳右
    private HwndSource? _mainSource;
    private const int WM_DISPLAYCHANGE = 0x007E;

    public MainWindow()
    {
        InitializeComponent();
        _dataStore = new DataStore();
        GridView.ItemsSource = _currentView;
        TextView.ItemsSource = _currentView;
        ListView.ItemsSource = _currentView;
        RefreshView();
    }

    #region 窗口初始化与定位

    private H.NotifyIcon.TaskbarIcon? _trayIcon;

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false
        });

        PositionWindowRightEdge();
        SetupCollapseTimer();
        StartHotspotTimer();
        SetupDragSnap();
        UpdateStatusText();
        UpdateUndoButton();
        SetupTrayIcon();
        SetupGlobalHotkey();
        SetupLanServer();
        try
        {
            Icon = new System.Windows.Media.Imaging.BitmapImage(
                new Uri("pack://application:,,,/app.ico"));
        }
        catch { }
        Logger.Run("MainWindow loaded, positioned at right edge");
    }

    /// <summary>初始化局域网服务器（自动启动 + 启动自检重试）
    /// 场景：杀进程后端口短暂 TIME_WAIT 占用 → Fleck/Http bind 静默失败 → 端口空了服务器却没起。
    /// 方案：启动后 2s 自检端口监听，失败则 Stop→等 3s→重启，最多 5 轮。</summary>
    private void SetupLanServer()
    {
        try
        {
            // 首次使用自动生成口令
            if (string.IsNullOrEmpty(LanConfig.Password))
            {
                LanConfig.Password = LanConfig.GeneratePassword();
                LanConfig.Save();
                Logger.Run("LAN: initial password generated");
            }
            _lanServer.TextReceived += OnLanTextReceived;
            _lanServer.ClientCountChanged += OnLanClientCountChanged;
            _lanServer.FileUploaded += OnLanFileUploaded;
            _lanServer.ListProvider = () => _dataStore.GetRecentSummaries(50);
            _lanServer.ItemLookup = id => _dataStore.Items.FirstOrDefault(x => x.Id == id);
            _dataStore.ItemAdded += OnDataStoreItemAdded;
            if (!LanConfig.Enabled)
            {
                // lan.json 被系统锁占用读取失败 → 后台重试加载，成功后再启动服务器
                if (LanConfig.LoadFailed)
                {
                    Logger.Error("LAN: config load failed (file locked?), retrying in background...");
                    _ = Task.Run(async () =>
                    {
                        for (int attempt = 1; attempt <= 10; attempt++)
                        {
                            await Task.Delay(3000);
                            LanConfig.Load();
                            if (LanConfig.Enabled) break;
                        }
                        if (!LanConfig.Enabled)
                        {
                            Logger.Error("LAN: config still unavailable after retries");
                            return;
                        }
                        Dispatcher.Invoke(() =>
                        {
                            try { _lanServer.Start(); Logger.Run("LAN: delayed start (config recovered)"); }
                            catch (Exception ex) { Logger.Error("LAN: delayed start failed: {0}", ex.Message); }
                        });
                    });
                }
                return;
            }

            _lanServer.Start();
            // 自检 + 重试（后台，不阻塞 UI）
            _ = Task.Run(async () =>
            {
                for (int attempt = 1; attempt <= 5; attempt++)
                {
                    await Task.Delay(2000);
                    if (_lanServer.IsListening)
                    {
                        Logger.Run("LAN self-check OK (attempt {0})", attempt);
                        return;
                    }
                    Logger.Error("LAN self-check FAILED (attempt {0}), restarting...", attempt);
                    try
                    {
                        _lanServer.Stop();
                        await Task.Delay(3000);
                        _lanServer.Start();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("LAN restart failed (attempt {0}): {1}", attempt, ex.Message);
                        await Task.Delay(3000);
                    }
                }
                Logger.Error("LAN self-check: all attempts exhausted");
            });
        }
        catch (Exception ex)
        {
            Logger.Error("SetupLanServer failed: {0}", ex.Message);
        }
    }

    /// <summary>手机文字素材 → 以本地新增方式进入流转</summary>
    private void OnLanTextReceived(string content, string device)
    {
        Dispatcher.Invoke(() =>
        {
            var item = DragDropService.CreateTextItem(content);
            _dataStore.Add(item);
            DragDropService.LoadDeferredPropertiesAsync(item, () => RefreshView());
            RefreshView();
            Logger.Run("LAN: text received from {0} ({1} chars) -> added", device, content.Length);
        });
    }

    /// <summary>手机上传文件 → 以本地新增方式进入流转</summary>
    private void OnLanFileUploaded(string filePath, string displayName)
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                var type = Utils.FileClassifier.ClassifyByExtension(filePath);
                var item = new Models.MaterialItem
                {
                    Type = type,
                    FilePath = filePath,
                    DisplayName = displayName,
                    Size = new System.IO.FileInfo(filePath).Length,
                    AddedTime = DateTime.Now
                };
                _dataStore.Add(item);
                DragDropService.LoadDeferredPropertiesAsync(item, () => RefreshView());
                RefreshView();
                Logger.Run("LAN: file uploaded from phone -> added: {0}", displayName);
            }
            catch (Exception ex)
            {
                Logger.Error("LAN file upload handling failed: {0}", ex.Message);
            }
        });
    }

    /// <summary>电脑端新增素材 → 广播摘要给已连接手机（接收页实时同步）</summary>
    private void OnDataStoreItemAdded(Models.MaterialItem item)
    {
        try
        {
            _lanServer.BroadcastItemAdded(new LanItemSummary
            {
                Id = item.Id,
                Type = item.Type.ToString(),
                Name = item.DisplayName,
                Size = item.Size,
                AddedTime = new DateTimeOffset(item.AddedTime).ToUnixTimeSeconds()
            });
        }
        catch (Exception ex)
        {
            Logger.Error("Broadcast item failed: {0}", ex.Message);
        }
    }

    private void OnLanClientCountChanged(int count)
    {
        Dispatcher.Invoke(() =>
        {
            UpdateStatusText();
            Logger.Run("LAN: connected devices now {0}", count);
        });
    }

    /// <summary>注册全局热键（默认 Ctrl+` 切换面板）</summary>
    private void SetupGlobalHotkey()
    {
        try
        {
            // 默认热键：Ctrl + `（反引号）
            var ok = _hotkeyService.Register(this, Key.Oem3, ModifierKeys.Control, () =>
            {
                if (_isExpanded) CollapsePanel(); else ExpandPanel();
            });
            if (ok)
                Logger.Run("Global hotkey registered: Ctrl+`");
        }
        catch (Exception ex)
        {
            Logger.Error("SetupGlobalHotkey failed: {0}", ex.Message);
        }
    }

    /// <summary>初始化系统托盘图标</summary>
    private void SetupTrayIcon()
    {
        try
        {
            var iconUri = new Uri("pack://application:,,,/app.ico", UriKind.Absolute);
            _trayIcon = new H.NotifyIcon.TaskbarIcon
            {
                ToolTipText = "流转 - 素材中转站",
                Visibility = Visibility.Visible
            };
            // 嵌入式 icon：强制立即加载（懒加载会导致托盘图标不显示）
            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri("pack://application:,,,/app.ico");
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                _trayIcon.IconSource = bmp;
                Logger.Run("TrayIcon: icon loaded from embedded resource");
            }
            catch (Exception iconEx)
            {
                Logger.Error("TrayIcon: embedded icon failed: {0}, trying exe icon", iconEx.Message);
                try
                {
                    // 回退：从 exe 文件提取图标
                    var exeIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "");
                    if (exeIcon != null)
                    {
                        _trayIcon.IconSource = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                            exeIcon.Handle, System.Windows.Int32Rect.Empty,
                            System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                        Logger.Run("TrayIcon: icon loaded from exe");
                    }
                }
                catch (Exception ex2)
                {
                    Logger.Error("TrayIcon: exe icon also failed: {0}", ex2.Message);
                }
            }
            _trayIcon.TrayLeftMouseDoubleClick += (s, e) =>
            {
                if (_isExpanded) CollapsePanel(); else ExpandPanel();
            };

            var menu = new ContextMenu
            {
                Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x35)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xEC)),
                BorderBrush = (Brush)FindResource("AccentBrush"),
                BorderThickness = new Thickness(1)
            };
            var toggleItem = new MenuItem { Header = "显示/隐藏面板", Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xEC)) };
            toggleItem.Click += (s, e) => { if (_isExpanded) CollapsePanel(); else ExpandPanel(); };
            menu.Items.Add(toggleItem);
            menu.Items.Add(new Separator());
            var exitItem = new MenuItem { Header = "退出流转", Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)) };
            exitItem.Click += (s, e) => { Logger.Run("User clicked exit from tray"); QuickExit(); };
            menu.Items.Add(exitItem);
            _trayIcon.ContextMenu = menu;

            Logger.Run("Tray icon initialized");
        }
        catch (Exception ex)
        {
            Logger.Error("SetupTrayIcon failed: {0}", ex.Message);
        }
    }

    private void PositionWindowRightEdge()
    {
        var workArea = SystemParameters.WorkArea;
        Top = workArea.Top + (workArea.Height - Height) / 2;
        Width = PanelWidth;
        _expandedLeft = workArea.Right - PanelWidth;
        _collapsedLeft = workArea.Right - CollapsedVisible;
        Left = _expandedLeft;
        Logger.Run("Window positioned: Left={0:F0} Top={1:F0} Width={2:F0} Height={3:F0}", Left, Top, Width, Height);
    }

    private void SetupCollapseTimer()
    {
        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CollapseDelayMs) };
        _collapseTimer.Tick += (s, e) => { _collapseTimer.Stop(); if (!_isLeftAnchored) CollapsePanel(); };
    }

    /// <summary>
    /// 热区轮询：每 150ms 检测鼠标是否贴近屏幕右缘（不依赖窄条 MouseEnter，
    /// 兼容透明区域 / 收起态 8px 细条命中难 / 多屏切换窗口位置跑偏等情况）
    /// </summary>
    private void StartHotspotTimer()
    {
        _hotspotTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _hotspotTimer.Tick += (s, e) =>
        {
            if (!GetCursorPos(out var pt)) return;
            var hMon = MonitorFromPoint(pt, 2 /* MONITOR_DEFAULTTONEAREST */);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(hMon, ref mi)) return;
            var work = mi.rcWork;

            // 窗口完全跑出该屏幕（多屏拔插/分辨率变化）→ 自动归位
            if (Left < work.Left - 50 || Left > work.Right + 50)
            {
                var top = work.Top + (work.Bottom - work.Top - Height) / 2;
                _expandedLeft = work.Right - PanelWidth;
                _collapsedLeft = work.Right - CollapsedVisible;
                Left = _isExpanded ? _expandedLeft : _collapsedLeft;
                Top = top;
                Logger.Run("Hotspot: window repositioned to screen ({0})", Left);
            }

            // 鼠标贴近屏幕右缘（12px 内）且纵向在工作区内 → 展开
            if (pt.X >= work.Right - 12 && pt.Y >= work.Top && pt.Y <= work.Bottom)
            {
                ExpandPanel();
            }
        };
        _hotspotTimer.Start();
    }

    /// <summary>
    /// 拖动 + 贴边吸附：窗口可自由拖动，松开时靠近屏幕边缘自动吸附。
    /// 右缘吸附 → 贴右并缩进（收起态，鼠标靠近展开）；左缘吸附 → 贴左展开。
    /// 另挂 WM_DISPLAYCHANGE：分辨率/多屏变化时自动重新吸附到当前右侧。
    /// </summary>
    private void SetupDragSnap()
    {
        // 显示变化监听（分辨率/多屏拔插）
        _mainSource = HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        _mainSource?.AddHook(MainWndProc);

        MouseLeftButtonDown += (s, e) =>
        {
            // 排除交互控件（按钮/输入框/素材项），空白区域才能拖动
            if (e.OriginalSource is System.Windows.Controls.Button or
                System.Windows.Controls.Primitives.ToggleButton or
                System.Windows.Controls.TextBox or
                System.Windows.Controls.ListBoxItem or
                System.Windows.Controls.CheckBox) return;
            try { DragMove(); }
            catch { /* 非左键或系统限制时忽略 */ }
        };

        MouseLeftButtonUp += (s, e) => SnapToEdge();
        Logger.Run("DragSnap: enabled (drag to edges to snap)");
    }

    private IntPtr MainWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DISPLAYCHANGE)
        {
            Dispatcher.BeginInvoke(() =>
            {
                PositionWindowRightEdge();
                Left = _isExpanded ? _expandedLeft : _collapsedLeft;
                Logger.Run("Display changed, repositioned: Left={0}", Left);
            });
        }
        return IntPtr.Zero;
    }

    /// <summary>拖动释放后贴边吸附（左右各 80px 触发）</summary>
    private void SnapToEdge()
    {
        try
        {
            if (!GetCursorPos(out var pt)) return;
            var hMon = MonitorFromPoint(pt, 2 /* MONITOR_DEFAULTTONEAREST */);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(hMon, ref mi)) return;
            var work = mi.rcWork;
            var w = ActualWidth;

            // 窗口右边界贴近屏幕右缘 → 贴右 + 缩进
            if (Left + w >= work.Right - 80)
            {
                _isLeftAnchored = false;
                _expandedLeft = work.Right - PanelWidth;
                _collapsedLeft = work.Right - CollapsedVisible;
                Left = _expandedLeft;
                Top = Math.Max(work.Top, Math.Min(work.Bottom - Height, Top));
                CollapsePanel();
                Logger.Run("Snap: right edge (collapsed), Left={0}", Left);
            }
            // 窗口贴近屏幕左缘 → 贴左展开（不自动收起）
            else if (Left <= work.Left + 80)
            {
                _isLeftAnchored = true;
                Left = work.Left;
                _expandedLeft = work.Left;
                Top = Math.Max(work.Top, Math.Min(work.Bottom - Height, Top));
                if (!_isExpanded)
                {
                    _isExpanded = true;
                    Width = PanelWidth;
                    MainPanel.RenderTransform = null;
                    TriggerBar.Opacity = 0.4;
                }
                Logger.Run("Snap: left edge (anchored, no auto-collapse), Left={0}", Left);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("SnapToEdge failed: {0}", ex.Message);
        }
    }

    #endregion

    #region 侧边栏伸缩动画

    private void ExpandPanel()
    {
        if (_isExpanded) return;
        _isExpanded = true;
        _collapseTimer?.Stop();
        Width = PanelWidth;
        Left = _expandedLeft;
        var translate = new TranslateTransform();
        MainPanel.RenderTransform = translate;
        var anim = new DoubleAnimation
        {
            From = PanelWidth - CollapsedVisible,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(250),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        translate.BeginAnimation(TranslateTransform.XProperty, anim);
        TriggerBar.Opacity = 0.4;
        Logger.Run("Panel expanded");
    }

    private void CollapsePanel()
    {
        if (!_isExpanded) return;
        if (_isLeftAnchored) return; // 左锚定：保持展开，不收起不跳右
        _isExpanded = false;
        var translate = new TranslateTransform();
        MainPanel.RenderTransform = translate;
        var anim = new DoubleAnimation
        {
            From = 0,
            To = PanelWidth - CollapsedVisible,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        anim.Completed += (s, e) =>
        {
            Width = CollapsedVisible;
            Left = _collapsedLeft;
            MainPanel.RenderTransform = null;
        };
        translate.BeginAnimation(TranslateTransform.XProperty, anim);
        Logger.Run("Panel collapsed");
    }

    private void Window_MouseEnter(object sender, MouseEventArgs e) => ExpandPanel();
    private void Window_MouseLeave(object sender, MouseEventArgs e) => _collapseTimer?.Start();
    private void TriggerBar_MouseEnter(object sender, MouseEventArgs e) { TriggerBar.Opacity = 0.7; ExpandPanel(); }
    private void CollapseBtn_Click(object sender, RoutedEventArgs e) => CollapsePanel();

    #endregion

    #region 标签页切换 + 搜索

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tag)
        {
            _currentTab = tag;
            RefreshView();
            Logger.Run("Tab switched to: {0}", tag);
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchKeyword = SearchBox.Text?.Trim() ?? "";
        // 有内容时显示清除按钮
        SearchClearBtn.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Collapsed : Visibility.Visible;
        RefreshView();
    }

    private void SearchClearBtn_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        _searchKeyword = "";
        SearchClearBtn.Visibility = Visibility.Collapsed;
        RefreshView();
        SearchBox.Focus();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SearchBox.Clear();
            _searchKeyword = "";
            RefreshView();
        }
    }

    private void RefreshView()
    {
        _currentView.Clear();

        IEnumerable<MaterialItem> items;
        if (!string.IsNullOrEmpty(_searchKeyword))
            items = _dataStore.Search(_searchKeyword, _currentTab);
        else
            items = _dataStore.GetFiltered(_currentTab);

        foreach (var item in items)
            _currentView.Add(item);

        // 决定使用哪个视图
        bool useTextOnly = _currentTab == "Text";
        bool useAudio = _currentTab == "Audio";
        bool useGrid = !useTextOnly && !useAudio; // Recent, Favorites, Image, Video, Other

        GridView.Visibility = useGrid ? Visibility.Visible : Visibility.Collapsed;
        TextView.Visibility = useTextOnly ? Visibility.Visible : Visibility.Collapsed;
        ListView.Visibility = useAudio ? Visibility.Visible : Visibility.Collapsed;

        // 网格视图用混合模板选择器（文字类用文字卡片，其他用网格卡片）
        GridView.ItemTemplateSelector = new MaterialTemplateSelector
        {
            GridTemplate = (DataTemplate)FindResource("GridCardTemplate"),
            TextTemplate = (DataTemplate)FindResource("TextCardTemplate")
        };

        EmptyState.Visibility = _currentView.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateStatusText();
    }

    private void UpdateStatusText()
    {
        var count = _currentView.Count;
        var typeName = _currentTab switch
        {
            "Recent" => "全部",
            "Favorites" => "收藏",
            "Video" => "视频",
            "Text" => "文字",
            "Image" => "图片",
            "Audio" => "音频",
            "Other" => "其他",
            _ => ""
        };
        var searchHint = !string.IsNullOrEmpty(_searchKeyword) ? $" 搜索「{_searchKeyword}」" : "";
        StatusText.Text = $"{typeName} {count} 项{searchHint}";
    }

    #endregion

    #region 拖入处理

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        DropHighlight.Visibility = Visibility.Visible;
        // 拖入时自动展开面板
        ExpandPanel();
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        // 拖出窗口区域时隐藏高亮提示（拖入后移开不添加的情况）
        DropHighlight.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        DropHighlight.Visibility = Visibility.Collapsed;
        var items = DragDropService.ExtractItems(e);
        if (items.Count > 0)
        {
            foreach (var item in items)
            {
                _dataStore.Add(item);
                DragDropService.LoadDeferredPropertiesAsync(item, () => RefreshView());
            }
            RefreshView();
            Logger.Run("Drop: added {0} items", items.Count);
        }
        e.Handled = true;
    }

    #endregion

    #region 拖出处理

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is MaterialItem item)
        {
            _dragStartPoint = e.GetPosition(null);
            _dragItem = item;
            _isDragging = false;
            _clickStartTime = DateTime.Now;

            // 检测双击：同一项目 500ms 内再次按下
            var now = DateTime.Now;
            if (_lastClickItem == item && (now - _lastClickTime).TotalMilliseconds < 500)
            {
                // 双击：文字类弹出悬浮选择框
                if (item.Type == MaterialType.Text && !string.IsNullOrEmpty(item.TextContent))
                {
                    ShowTextPopup(item);
                    e.Handled = true;
                }
                _lastClickItem = null;
            }
            else
            {
                _lastClickItem = item;
                _lastClickTime = now;
            }
        }
    }

    /// <summary>单击释放：如果没有拖动，复制资产到剪贴板</summary>
    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragItem != null && !_isDragging)
        {
            // 单击复制到剪贴板
            CopyItemToClipboard(_dragItem);
        }
        _dragItem = null;
        _isDragging = false;
    }

    /// <summary>复制资产到剪贴板</summary>
    private void CopyItemToClipboard(MaterialItem item)
    {
        try
        {
            if (item.Type == MaterialType.Text && !string.IsNullOrEmpty(item.TextContent))
            {
                Clipboard.SetText(item.TextContent);
                Logger.Run("Click copy: text -> clipboard ({0} chars)", item.TextContent.Length);
            }
            else if (item.IsFile && File.Exists(item.FilePath))
            {
                var files = new System.Collections.Specialized.StringCollection { item.FilePath };
                Clipboard.SetFileDropList(files);
                Logger.Run("Click copy: file -> clipboard ({0})", item.DisplayName);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("CopyItemToClipboard failed: {0}", ex.Message);
        }
    }

    /// <summary>文字类双击弹出悬浮选择框</summary>
    private void ShowTextPopup(MaterialItem item)
    {
        var textBox = new TextBox
        {
            Text = item.TextContent,
            IsReadOnly = false,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinWidth = 300,
            MaxWidth = 500,
            MaxHeight = 300,
            Margin = new Thickness(8),
            FontSize = 12,
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x26)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xEC)),
            BorderThickness = new Thickness(0)
        };
        textBox.SelectAll();
        textBox.Focus();

        var copyBtn = new Button
        {
            Content = "复制选中",
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(8, 0, 4, 8),
            Background = (Brush)FindResource("AccentBrush"),
            Foreground = Brushes.White
        };
        copyBtn.Click += (s, e) =>
        {
            var selected = textBox.SelectedText;
            if (!string.IsNullOrEmpty(selected))
            {
                Clipboard.SetText(selected);
                Logger.Run("TextPopup: copied selected ({0} chars)", selected.Length);
            }
            else
            {
                Clipboard.SetText(item.TextContent);
                Logger.Run("TextPopup: copied all ({0} chars)", item.TextContent.Length);
            }
        };

        var copyAllBtn = new Button
        {
            Content = "复制全部",
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(4, 0, 8, 8),
            Background = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x48)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xEC))
        };
        copyAllBtn.Click += (s, e) =>
        {
            Clipboard.SetText(item.TextContent);
            Logger.Run("TextPopup: copied all ({0} chars)", item.TextContent.Length);
        };

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        btnPanel.Children.Add(copyBtn);
        btnPanel.Children.Add(copyAllBtn);

        var panel = new StackPanel();
        panel.Children.Add(textBox);
        panel.Children.Add(btnPanel);

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x35)),
            BorderBrush = (Brush)FindResource("AccentBrush"),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Child = panel,
            IsHitTestVisible = true
        };

        var popup = new Popup
        {
            Child = border,
            Placement = PlacementMode.Mouse,
            AllowsTransparency = true,
            StaysOpen = false,
            IsOpen = true
        };
        Logger.Run("TextPopup: opened for {0}", item.DisplayName);
    }

    private void Card_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem == null || e.LeftButton != MouseButtonState.Pressed)
        {
            _dragItem = null;
            return;
        }
        var pos = e.GetPosition(null);
        var diff = _dragStartPoint - pos;
        if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            if (!_isDragging)
            {
                _isDragging = true;
                StartDragOut(_dragItem);
                _dragItem = null;
                _isDragging = false;
            }
        }
    }

    private void StartDragOut(MaterialItem item)
    {
        try
        {
            var data = DragDropService.CreateDragOutData(item);
            var effects = DragDropService.GetDragOutEffects(item);
            if (effects == DragDropEffects.None) return;
            Logger.Run("DragOut: Type={0} Name={1}", item.Type, item.DisplayName);
            DragDrop.DoDragDrop((DependencyObject)this, data, effects);
        }
        catch (Exception ex)
        {
            Logger.Error("StartDragOut failed: {0}", ex.Message);
        }
    }

    #endregion

    #region 右键菜单 + 删除 + 多选

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.RightButton == MouseButtonState.Pressed)
        {
            var hit = VisualHitTest<Border>(e.OriginalSource as DependencyObject);
            if (hit?.DataContext is MaterialItem item)
            {
                ShowContextMenu(item);
                e.Handled = true;
            }
        }
    }

    private void ShowContextMenu(MaterialItem item)
    {
        var menu = new ContextMenu
        {
            Background = (Brush)FindResource("PanelLightBrush"),
            Foreground = (Brush)FindResource("TextBrush"),
            BorderBrush = (Brush)FindResource("AccentBrush"),
            BorderThickness = new Thickness(1),
        };

        // 收集选中的项
        var selectedItems = GetSelectedItems();
        var targetItems = selectedItems.Count > 1 ? selectedItems : new List<MaterialItem> { item };

        // 删除
        var deleteItem = new MenuItem { Header = $"删除选中（{targetItems.Count} 项）", Foreground = (Brush)FindResource("TextBrush") };
        deleteItem.Click += (s, e) => DeleteItems(targetItems);
        menu.Items.Add(deleteItem);

        // 收藏/取消收藏
        var allFavorited = targetItems.All(x => x.IsFavorite);
        var favItem = new MenuItem
        {
            Header = allFavorited ? "取消收藏" : $"收藏（{targetItems.Count} 项）",
            Foreground = (Brush)FindResource("TextBrush")
        };
        favItem.Click += (s, e) =>
        {
            var ids = targetItems.Select(x => x.Id).ToList();
            _dataStore.SetFavoriteMultiple(ids, !allFavorited);
            RefreshView();
            Logger.Run("Context: set favorite={0} for {1} items", !allFavorited, ids.Count);
        };
        menu.Items.Add(favItem);

        menu.Items.Add(new Separator());

        // 打开所在文件夹
        if (item.IsFile && File.Exists(item.FilePath))
        {
            var openFolderItem = new MenuItem { Header = "打开所在文件夹", Foreground = (Brush)FindResource("TextBrush") };
            openFolderItem.Click += (s, e) =>
            {
                try { Process.Start("explorer.exe", $"/select,\"{item.FilePath}\""); }
                catch (Exception ex) { Logger.Error("Open folder failed: {0}", ex.Message); }
            };
            menu.Items.Add(openFolderItem);
        }

        // 复制路径
        if (item.IsFile)
        {
            var copyPathItem = new MenuItem { Header = "复制文件路径", Foreground = (Brush)FindResource("TextBrush") };
            copyPathItem.Click += (s, e) => { try { Clipboard.SetText(item.FilePath); } catch { } };
            menu.Items.Add(copyPathItem);
        }

        // 复制文字
        if (!item.IsFile)
        {
            var copyTextItem = new MenuItem { Header = "复制文字内容", Foreground = (Brush)FindResource("TextBrush") };
            copyTextItem.Click += (s, e) => { try { Clipboard.SetText(item.TextContent); } catch { } };
            menu.Items.Add(copyTextItem);
        }

        menu.IsOpen = true;
    }

    private List<MaterialItem> GetSelectedItems()
    {
        var result = new List<MaterialItem>();
        var activeList = GetActiveListView();
        if (activeList?.SelectedItems != null)
        {
            foreach (var sel in activeList.SelectedItems)
                if (sel is MaterialItem mi)
                    result.Add(mi);
        }
        return result;
    }

    private ListView? GetActiveListView()
    {
        if (GridView.Visibility == Visibility.Visible) return GridView;
        if (TextView.Visibility == Visibility.Visible) return TextView;
        if (ListView.Visibility == Visibility.Visible) return ListView;
        return null;
    }

    private void DeleteItems(List<MaterialItem> items)
    {
        var ids = items.Select(x => x.Id).ToList();
        _dataStore.RemoveMultiple(ids);
        RefreshView();
        UpdateUndoButton();
        Logger.Run("Deleted {0} items", items.Count);
    }

    private void FavoriteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            // 查找素材项，切换收藏状态
            var item = _dataStore.Items.FirstOrDefault(x => x.Id == id);
            if (item != null)
            {
                _dataStore.SetFavorite(id, !item.IsFavorite);
                // INotifyPropertyChanged 会自动更新星星 UI
                Logger.Run("Favorite toggled: {0} -> {1}", item.DisplayName, !item.IsFavorite);
            }
        }
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Del 键删除选中项（焦点不在搜索框时）
        if (e.Key == Key.Delete && !SearchBox.IsKeyboardFocusWithin)
        {
            var selected = GetSelectedItems();
            if (selected.Count > 0)
            {
                DeleteItems(selected);
                e.Handled = true;
            }
        }

        // Ctrl+Z 撤回删除（焦点不在搜索框时，让搜索框自己处理编辑撤销）
        if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
            && !SearchBox.IsKeyboardFocusWithin)
        {
            UndoDelete();
            e.Handled = true;
        }

        // Ctrl+F 搜索
        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            SearchBox.Focus();
            e.Handled = true;
        }

        // Ctrl+V 粘贴剪贴板内容（图片/文件/文字）
        if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
            && !SearchBox.IsKeyboardFocusWithin)
        {
            PasteFromClipboard();
            e.Handled = true;
        }
    }

    private void PasteFromClipboard()
    {
        var (item, _) = DragDropService.CreateFromClipboard();
        if (item != null)
        {
            // 图片类型：temp PNG 已由 GDI+ 保存为有效文件，直接加载为缩略图
            if (item.IsFile && item.Type == MaterialType.Image && File.Exists(item.FilePath))
            {
                try
                {
                    item.ThumbnailPath = item.FilePath;
                    item.TryLoadThumbnailSync();
                    Logger.Run("Paste: thumbnail set via file, {0}", item.FilePath);
                }
                catch (Exception ex)
                {
                    Logger.Error("Paste: failed to create thumbnail: {0}", ex.Message);
                }
            }

            _dataStore.Add(item);
            DragDropService.LoadDeferredPropertiesAsync(item, () => RefreshView());
            RefreshView();
            Logger.Run("Paste: added item Type={0} Name={1}", item.Type, item.DisplayName);
        }
        else
        {
            Logger.Run("Paste: clipboard has no recognized content");
        }
    }

    private void UndoBtn_Click(object sender, RoutedEventArgs e) => UndoDelete();

    private void UndoDelete()
    {
        if (_dataStore.Undo())
        {
            RefreshView();
            UpdateUndoButton();
            Logger.Run("Undo performed, remaining steps: {0}", _dataStore.UndoCount);
        }
    }

    private void UpdateUndoButton()
    {
        UndoBtn.IsEnabled = _dataStore.CanUndo;
        UndoBtn.Content = _dataStore.CanUndo ? $"撤回({_dataStore.UndoCount})" : "撤回";
    }

    #endregion

    #region 预览

    private void Card_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is MaterialItem item)
        {
            string tooltip;
            if (item.IsFile)
            {
                var sizeStr = FileClassifier.FormatSize(item.Size);
                var timeStr = item.AddedTime.ToString("MM-dd HH:mm");
                tooltip = $"{item.DisplayName}\n{item.FilePath}\n大小: {sizeStr}  |  拖入: {timeStr}";
            }
            else
            {
                var timeStr = item.AddedTime.ToString("MM-dd HH:mm");
                var preview = item.TextContent.Length > 200 ? item.TextContent[..200] + "…" : item.TextContent;
                tooltip = $"{preview}\n\n字数: {item.TextContent.Length}  |  拖入: {timeStr}";
            }
            fe.ToolTip = tooltip;

            // 图片类素材：hover 时弹出原图预览
            if (item.IsFile && item.Type == MaterialType.Image && File.Exists(item.FilePath))
            {
                ShowHoverPreview(item, fe);
            }
        }
    }

    private void Card_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            fe.ToolTip = null;
        }
        // 关闭预览弹窗
        CloseHoverPreview();
    }

    #endregion

    #region 清空操作

    private void ClearCurrentBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_currentView.Count == 0) return;
        var typeName = _currentTab switch
        {
            "Recent" => "全部素材", "Favorites" => "收藏", "Video" => "视频", "Text" => "文字",
            "Image" => "图片", "Audio" => "音频", "Other" => "其他",
            _ => "当前分类"
        };
        var result = MessageBox.Show($"确定清空「{typeName}」吗？\n（仅删除软件内索引，不删除本地源文件。收藏项不会被清空）",
            "确认清空", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (result == MessageBoxResult.OK)
        {
            if (_currentTab == "Recent") _dataStore.ClearAll();
            else if (_currentTab == "Favorites")
            {
                // 清空收藏 = 批量取消所有收藏项的收藏标记
                var favIds = _dataStore.Items.Where(x => x.IsFavorite).Select(x => x.Id).ToList();
                if (favIds.Count > 0) _dataStore.SetFavoriteMultiple(favIds, false);
            }
            else if (Enum.TryParse<MaterialType>(_currentTab, out var type)) _dataStore.ClearByType(type);
            RefreshView();
            UpdateUndoButton();
        }
    }

    private void ClearAllBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_dataStore.Items.Count == 0) return;
        var result = MessageBox.Show("确定清空所有素材吗？\n（仅删除软件内索引，不删除本地源文件）",
            "确认全部清空", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result == MessageBoxResult.OK)
        {
            _dataStore.ClearAll();
            RefreshView();
            UpdateUndoButton();
        }
    }

    #endregion

    #region 高度调整

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var newHeight = Height + e.VerticalChange;
        var workArea = SystemParameters.WorkArea;
        newHeight = Math.Max(300, Math.Min(workArea.Height, newHeight));
        Height = newHeight;
        if (Top + Height > workArea.Bottom) Top = workArea.Bottom - Height;
    }

    #endregion

    #region 设置 + 退出

    private void SettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            Background = (Brush)FindResource("PanelLightBrush"),
            Foreground = (Brush)FindResource("TextBrush"),
            BorderBrush = (Brush)FindResource("AccentBrush"),
            BorderThickness = new Thickness(1),
        };

        var autoStartItem = new MenuItem
        {
            Header = "开机自启动", Foreground = (Brush)FindResource("TextBrush"),
            IsCheckable = true, IsChecked = StartupService.IsEnabled()
        };
        autoStartItem.Click += (s, e) => StartupService.SetEnabled(autoStartItem.IsChecked);
        menu.Items.Add(autoStartItem);

        // 剪贴板监控：电脑上任意复制 → 自动加载进流转（可选开关）
        var clipMonitorItem = new MenuItem
        {
            Header = "剪贴板监控（复制自动收藏）",
            Foreground = (Brush)FindResource("TextBrush"),
            IsCheckable = true,
            IsChecked = AppSettings.ClipboardMonitorEnabled,
        };
        clipMonitorItem.Click += (s, e) =>
        {
            AppSettings.ClipboardMonitorEnabled = clipMonitorItem.IsChecked;
            AppSettings.Save();
            if (clipMonitorItem.IsChecked)
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                _clipMonitor.Start(hwnd, text =>
                {
                    var item = DragDropService.CreateTextItem(text);
                    _dataStore.Add(item);
                    DragDropService.LoadDeferredPropertiesAsync(item, () => RefreshView());
                    RefreshView();
                    Logger.Run("ClipboardMonitor: auto-added ({0} chars)", text.Length);
                });
                Logger.Run("Settings: clipboard monitor ON");
            }
            else
            {
                _clipMonitor.Stop();
                Logger.Run("Settings: clipboard monitor OFF");
            }
        };
        menu.Items.Add(clipMonitorItem);

        menu.Items.Add(new Separator());

        // 缓存路径
        var dataDirItem = new MenuItem
        {
            Header = $"缓存路径: {ConfigService.GetEffectiveDataDir()}",
            Foreground = (Brush)FindResource("TextBrush"),
            FontSize = 10,
        };
        menu.Items.Add(dataDirItem);

        var resetDataDirItem = new MenuItem
        {
            Header = "重置缓存路径为默认",
            Foreground = (Brush)FindResource("TextDimBrush"),
            FontSize = 10,
        };
        resetDataDirItem.Click += (s, ee) =>
        {
            ConfigService.SetDataDir("");
            Logger.Run("Settings: data dir reset to default: {0}", ConfigService.GetEffectiveDataDir());
        };
        menu.Items.Add(resetDataDirItem);

        menu.Items.Add(new Separator());

        // ========== 局域网服务器 ==========
        var lanToggleItem = new MenuItem
        {
            Header = "局域网服务器（安卓联动）",
            Foreground = (Brush)FindResource("TextBrush"),
            IsCheckable = true,
            IsChecked = LanConfig.Enabled
        };
        lanToggleItem.Click += (s, e) =>
        {
            LanConfig.Enabled = lanToggleItem.IsChecked;
            LanConfig.Save();
            if (LanConfig.Enabled) { _lanServer.Start(); }
            else { _lanServer.Stop(); }
            Logger.Run("Settings: LAN server {0}", LanConfig.Enabled ? "enabled" : "disabled");
        };
        menu.Items.Add(lanToggleItem);

        var lanPortItem = new MenuItem
        {
            Header = $"端口: {LanConfig.Port}（数据端口 {LanConfig.Port + 1}）",
            Foreground = (Brush)FindResource("TextDimBrush"),
            FontSize = 10
        };
        menu.Items.Add(lanPortItem);

        // 本机 IP（显示 LAN 段 IPv4，含可点击刷新）
        var lanIpItem = new MenuItem
        {
            Header = $"本机 IP: {GetLocalLanIp()}    [点击刷新]",
            Foreground = (Brush)FindResource("TextDimBrush"),
            FontSize = 10
        };
        lanIpItem.Click += (s, e) =>
        {
            var ip = GetLocalLanIp();
            lanIpItem.Header = $"本机 IP: {ip}    [点击刷新]";
            Logger.Run("Settings: local IP refreshed: {0}", ip);
        };
        menu.Items.Add(lanIpItem);

        var lanPwdItem = new MenuItem
        {
            Header = $"口令: {(LanConfig.Password.Length > 0 ? LanConfig.Password : "（未设置）")}",
            Foreground = (Brush)FindResource("TextDimBrush"),
            FontSize = 10
        };
        menu.Items.Add(lanPwdItem);

        var lanRegenItem = new MenuItem
        {
            Header = "生成新口令",
            Foreground = (Brush)FindResource("TextBrush"),
            FontSize = 10
        };
        lanRegenItem.Click += (s, e) =>
        {
            LanConfig.Password = LanConfig.GeneratePassword();
            LanConfig.Save();
            lanPwdItem.Header = $"口令: {LanConfig.Password}";
            Logger.Run("Settings: new LAN password generated");
        };
        menu.Items.Add(lanRegenItem);

        // 已连接设备列表（点击刷新）
        var lanDevItem = new MenuItem
        {
            Header = "已连接设备: 无",
            Foreground = (Brush)FindResource("TextDimBrush"),
            FontSize = 10
        };
        Action refreshDevices = () =>
        {
            var devices = _lanServer.GetDevices();
            lanDevItem.Header = devices.Count == 0
                ? "已连接设备: 无"
                : "已连接设备: " + string.Join(" | ", devices.Select(d => $"{d.Device}"));
            lanDevItem.ToolTip = string.Join("\n", devices.Select(d => $"{d.Device} ({d.Ip})"));
        };
        lanDevItem.Click += (s, e) =>
        {
            refreshDevices();
            Logger.Run("Settings: device list refreshed: {0} devices", _lanServer.ClientCount);
        };
        menu.Items.Add(lanDevItem);

        // 配对二维码（手机扫码自动连接）
        var lanQrItem = new MenuItem
        {
            Header = "配对二维码（手机扫码连接）",
            Foreground = (Brush)FindResource("TextBrush"),
            FontSize = 10
        };
        lanQrItem.Click += (s, e) =>
        {
            try
            {
                var win = new QrCodeWindow(GetLocalLanIp(), LanConfig.Port, LanConfig.Password)
                {
                    Owner = this
                };
                win.Show();
                Logger.Run("Settings: pairing QR shown");
            }
            catch (Exception ex)
            {
                Logger.Error("Settings: QR failed: {0}", ex.Message);
            }
        };
        menu.Items.Add(lanQrItem);
        refreshDevices();

        menu.Items.Add(new Separator());

        var aboutItem = new MenuItem { Header = "关于流转 v1.0", Foreground = (Brush)FindResource("TextBrush") };
        aboutItem.Click += (s, e) => MessageBox.Show(
            "流转 v1.0\n跨应用素材暂存中转站\n\n核心：拖拽暂存 · 即用即拖 · 不改动源文件\n\n快捷键：\nDel — 删除选中\nCtrl+Z — 撤回删除\nCtrl+V — 粘贴剪贴板\nCtrl+F — 搜索",
            "关于", MessageBoxButton.OK, MessageBoxImage.Information);
        menu.Items.Add(aboutItem);

        menu.Items.Add(new Separator());

        // 退出
        var exitItem = new MenuItem { Header = "退出流转", Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)) };
        exitItem.Click += (s, e) =>
        {
            Logger.Run("User clicked exit");
            QuickExit();
        };
        menu.Items.Add(exitItem);

        menu.IsOpen = true;
    }

    #endregion

    /// <summary>
    /// 缩略图 Image 加载时直接设 Source（绕过 WPF Binding，解决多次尝试均失败的问题）
    /// </summary>
    private void ThumbnailImage_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Image img && img.DataContext is MaterialItem item)
        {
            var src = item.ThumbnailSource;
            img.Source = src;
            img.UpdateLayout();
        }
    }

    private Popup? _previewPopup;

    /// <summary>hover 时弹出原图预览弹窗（鼠标穿透，不闪烁）</summary>
    private void ShowHoverPreview(MaterialItem item, FrameworkElement anchor)
    {
        try
        {
            var previewBmp = new System.Windows.Media.Imaging.BitmapImage();
            previewBmp.BeginInit();
            previewBmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            previewBmp.UriSource = new Uri(item.FilePath, UriKind.Absolute);
            previewBmp.EndInit();
            previewBmp.Freeze();

            CloseHoverPreview();

            var previewImg = new Image
            {
                Source = previewBmp,
                MaxWidth = 500,
                MaxHeight = 400,
                Stretch = Stretch.Uniform
            };
            RenderOptions.SetBitmapScalingMode(previewImg, BitmapScalingMode.HighQuality);

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x26)),
                BorderBrush = (Brush)FindResource("AccentBrush"),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4),
                Child = previewImg,
                IsHitTestVisible = false  // 关键：鼠标穿透，不抢焦点 → 不闪烁
            };

            _previewPopup = new Popup
            {
                Child = border,
                PlacementTarget = anchor,
                Placement = PlacementMode.Left,
                HorizontalOffset = -10,
                AllowsTransparency = true,
                StaysOpen = true,
                IsOpen = true
            };
        }
        catch (Exception ex)
        {
            Logger.Error("Hover preview failed: {0}", ex.Message);
        }
    }

    private void CloseHoverPreview()
    {
        if (_previewPopup != null)
        {
            _previewPopup.IsOpen = false;
            _previewPopup = null;
        }
    }

    #region 辅助方法

    private static T? VisualHitTest<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element != null)
        {
            if (element is T target) return target;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        _collapseTimer?.Stop();
        _hotkeyService.Dispose();
        _lanServer.Dispose();
        _dataStore.Dispose();
        _trayIcon?.Dispose();
        Logger.Run("MainWindow closing, data saved");
        Application.Current.Shutdown();
    }

    /// <summary>
    /// 快速退出：看门狗 1.5s 强制结束进程，防止长时间运行后清理（LAN/托盘）卡死。
    /// 正常路径：同步保存数据 → 停止服务器 → 清理托盘 → Shutdown；看门狗兜底。
    /// </summary>
    private void QuickExit()
    {
        var watchdog = new System.Threading.Thread(() =>
        {
            System.Threading.Thread.Sleep(1500);
            Environment.Exit(0);
        }) { IsBackground = true };
        watchdog.Start();

        try { _lanServer.Stop(); } catch (Exception ex) { Logger.Error("QuickExit: lan stop {0}", ex.Message); }
        try { _dataStore.Dispose(); } catch (Exception ex) { Logger.Error("QuickExit: datastore {0}", ex.Message); }
        try { _trayIcon?.Dispose(); } catch (Exception ex) { Logger.Error("QuickExit: tray {0}", ex.Message); }
        Application.Current.Shutdown();
    }

    /// <summary>获取本机 LAN 段 IPv4（统一走 LanNetUtil）</summary>
    public static string GetLocalLanIp()
    {
        var ip = Utils.LanNetUtil.GetLanIp();
        return ip == "127.0.0.1" ? "（未检测到）" : ip;
    }

    #endregion

    #region Win32 P/Invoke（热区检测）

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    #endregion
}

/// <summary>
/// 模板选择器 — 文字类用文字卡片，其他用网格卡片
/// </summary>
public class MaterialTemplateSelector : DataTemplateSelector
{
    public DataTemplate? GridTemplate { get; set; }
    public DataTemplate? TextTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is MaterialItem mi && mi.Type == MaterialType.Text)
            return TextTemplate;
        return GridTemplate;
    }
}
