using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Liuzhuan.Services.Audio;

namespace Liuzhuan.Views;

/// <summary>Visible-only history of received PCM peaks. Opening the entry never starts capture.</summary>
public partial class MicrophoneEntry : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly float[] _history = new float[15];
    private readonly Rectangle[] _bars = new Rectangle[15];
    private Window? _owner;
    private long _connection;
    public Func<AudioTransportServer?>? ServerProvider { get; set; }
    public event RoutedEventHandler? Click;
    public MicrophoneEntry()
    {
        InitializeComponent();
        for (int i = 0; i < _bars.Length; i++)
        {
            var bar = new Rectangle { Width = 2, Height = 2, RadiusX = 1, RadiusY = 1 };
            bar.SetResourceReference(Shape.FillProperty, "TextDimBrush");
            Canvas.SetLeft(bar, i * 4 + 1); Canvas.SetTop(bar, 8.5);
            WaveCanvas.Children.Add(bar); _bars[i] = bar;
        }
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { _owner = Window.GetWindow(this); if (_owner != null) _owner.StateChanged += OwnerChanged; SyncTimer(); };
        Unloaded += (_, _) => { _timer.Stop(); if (_owner != null) _owner.StateChanged -= OwnerChanged; _owner = null; };
        IsVisibleChanged += (_, _) => SyncTimer();
    }
    private void OwnerChanged(object? sender, EventArgs e) => SyncTimer();
    private void SyncTimer()
    {
        if (IsLoaded && IsVisible && _owner?.WindowState != WindowState.Minimized) { Refresh(); _timer.Start(); }
        else { _timer.Stop(); Array.Clear(_history); Draw(false); }
    }
    private void Entry_Click(object sender, RoutedEventArgs e) => Click?.Invoke(this, e);
    private void Refresh()
    {
        var server = ServerProvider?.Invoke();
        var state = server?.Snapshot;
        _timer.Interval = state?.ConnectionId > 0 ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(1);
        if (_connection != (state?.ConnectionId ?? 0)) { Array.Clear(_history); _connection = state?.ConnectionId ?? 0; }
        bool receiving = state?.Receiving == true && !state.Quiet;
        Array.Copy(_history, 1, _history, 0, _history.Length - 1);
        _history[^1] = receiving ? server!.LiveLevel.Peak : 0;
        Draw(receiving);
        var label = state == null ? "麦克风" : state.ConnectionId == 0 ? "待连接" : state.Quiet ? "安静待命" : receiving ? "正在收音" : state.Ready ? "采音待命" : "未准备";
        if (StateText.Text != label)
        {
            StateText.Text = label;
            EntryButton.ToolTip = $"手机麦克风 · {label}\n点击打开麦克风设置";
            System.Windows.Automation.AutomationProperties.SetName(EntryButton, $"手机麦克风，{label}，点击打开设置");
        }
    }
    private void Draw(bool receiving)
    {
        for (int i = 0; i < _bars.Length; i++)
        {
            double height = 2 + Math.Sqrt(Math.Clamp(_history[i], 0, 1)) * 17;
            _bars[i].Height = height; Canvas.SetTop(_bars[i], (19 - height) / 2);
            _bars[i].SetResourceReference(Shape.FillProperty, receiving || _history[i] > 0 ? "AccentBrush" : "TextDimBrush");
        }
    }
}
