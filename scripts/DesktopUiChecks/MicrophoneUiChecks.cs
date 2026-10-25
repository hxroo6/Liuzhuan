using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Liuzhuan.Services.Audio;
using Liuzhuan.Views;
using NAudio.CoreAudioApi;

internal static class MicrophoneUiChecks
{
    public static void Run(string output)
    {
        var notice = new Liuzhuan.Services.Lan.ConnectionNoticePolicy();
        if (!notice.ShouldNotify(0) || notice.ShouldNotify(1000) || notice.ShouldNotify(14999) || !notice.ShouldNotify(15000))
            throw new Exception("authenticated reconnect notifications must be limited to once per 15 seconds");
        Console.WriteLine("PASS authenticated connection notice cooldown prevents reconnect popup storms");
        var host = new MicrophoneHost(); var window = new MicrophoneWindow(host);
        var network = (ComboBox)window.FindName("NetworkChoice");
        network.ItemsSource = new[] { new { Label = "示例局域网 · 地址已隐藏" } }; network.SelectedIndex = 0;
        foreach (var name in new[] { "OutputChoice", "CaptureChoice" })
        {
            var choice = (ComboBox)window.FindName(name);
            choice.ItemsSource = new[] { new AudioEndpointChoice("test-only", name == "OutputChoice" ? "CABLE Input（离屏示例）" : "CABLE Output（离屏示例）") };
            choice.SelectedIndex = 0;
        }
        var speaker = new AudioRouteEndpoint("fixture-private-speaker-id", "Fixture private speakers", DataFlow.Render, Muted: false, Volume: .6f);
        var virtualOutput = new AudioRouteEndpoint("test-only", "CABLE Input (VB-Audio Virtual Cable)", DataFlow.Render, Muted: false, Volume: 1);
        var virtualInput = new AudioRouteEndpoint("test-only", "CABLE Output (VB-Audio Virtual Cable)", DataFlow.Capture, Muted: false, Volume: 1);
        var defaults = new[] { Role.Console, Role.Multimedia, Role.Communications }.SelectMany(role => new[]
        {
            new AudioDefaultRoute(DataFlow.Render, role, role == Role.Communications ? virtualOutput : speaker),
            new AudioDefaultRoute(DataFlow.Capture, role, virtualInput)
        }).ToArray();
        typeof(MicrophoneWindow).GetField("_inventory", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window,
            new AudioRouteInventory(new[] { speaker, virtualOutput, virtualInput }, defaults));
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try { typeof(MicrophoneWindow).GetMethod("ApplyRouteReport", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null); }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        var profileSummary = (TextBlock)window.FindName("ProfileSummary");
        var frame = new DispatcherFrame(); var deadline = DateTime.UtcNow.AddSeconds(5);
        var dispatchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        dispatchTimer.Tick += (_, _) => { if (!profileSummary.Text.StartsWith("正在读取") || DateTime.UtcNow > deadline) { dispatchTimer.Stop(); frame.Continue = false; } };
        dispatchTimer.Start(); Dispatcher.PushFrame(frame);
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(780, 840)); root.Arrange(new Rect(0, 0, 780, 840)); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(780, 840, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(output, "phone-microphone.png"))) png.Save(stream);
        void Check(bool value, string text) { if (!value) throw new Exception(text); Console.WriteLine("PASS " + text); }
        Check(host.Server == null, "opening microphone settings does not start server or capture");
        Check(!((Button)window.FindName("StartButton")).IsEnabled, "forwarding disabled before phone is prepared");
        Check(((Image)window.FindName("QrImage")).Visibility == Visibility.Collapsed, "no pairing token before explicit generation");
        Check(((CheckBox)window.FindName("ObserveChoice")).Content.ToString()!.Contains("不自动开麦"), "observer is explicitly read-only");
        var combo = (ComboBox)window.FindName("OutputChoice");
        Check(combo.Foreground is SolidColorBrush brush && brush.Color.R < 80, "device chooser uses readable dark text on light background");
        Check(((CheckBox)window.FindName("DiagnosticChoice")).Content.ToString()!.Contains("不输出"), "receive-only diagnostics cannot be mistaken for virtual microphone output");
        Check(((ProgressBar)window.FindName("ReceiveLevel")).Value == 0, "PC audio level remains zero before real samples");
        Check(!((CheckBox)window.FindName("AutomaticChoice")).IsEnabled && !host.Automatic && !host.Calibrated,
            "automatic following is off and unavailable before calibration and phone consent");
        Check(!((Button)window.FindName("CalibrateButton")).IsEnabled, "calibration is unavailable while audio service is off");
        Check(window.FindName("CopyDiagnosticsButton") is Button, "sanitized diagnostics have an explicit copy action");
        Check(window.FindName("RetryAudioButton") is Button && window.FindName("RecoveryText") is TextBlock && window.FindName("CompatibilityText") is TextBlock,
            "recovery and compatibility have dedicated actionable UI entries");
        Check(window.FindName("SaveGoodProfileButton") is Button save && save.Content.ToString()!.Contains("外放和听写正常") &&
            window.FindName("RestoreGoodProfileButton") is Button restore && !restore.IsEnabled && window.FindName("ProfileSummary") is TextBlock,
            "configuration protection exposes explicit success confirmation and disables restoration without a validated profile");
        Check(!profileSummary.Text.StartsWith("正在读取") && ((ItemsControl)window.FindName("ProfileFindings")).Items.Count > 0,
            "background protection inspection returns to the actual dispatcher with persistent findings");
        Check(((TextBlock)window.FindName("StatsText")).Text.Length == 0, "settings construction never invents session statistics");
        Check(((TextBlock)window.FindName("PlaybackRouteText")).Text.Contains("Fixture private speakers") &&
            ((TextBlock)window.FindName("OutputRouteText")).Text.Contains("CABLE Input") &&
            ((TextBlock)window.FindName("RecordingRouteText")).Text.Contains("CABLE Output"), "sound guide separates physical output, bridge output and dictation input");
        Check(((ItemsControl)window.FindName("RouteFindings")).Items.Cast<AudioRouteFinding>().Any(x => x.Code == "playback-virtual-Communications"),
            "production UI exposes communications playback misrouting without starting a stream");
        Check(new[] { "HealthCheckButton", "PlaybackSettingsButton", "RecordingSettingsButton", "SoundSettingsButton" }
            .All(name => window.FindName(name) is Button button && button.IsEnabled), "read-only diagnosis and explicit Windows repair entries are accessible before enabling audio");
        string diagnostic = (string)typeof(MicrophoneWindow).GetMethod("DiagnosticsText", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null)!;
        Check(diagnostic.Contains("playback-virtual-Communications") && !diagnostic.Contains("fixture-private") && !diagnostic.Contains("Fixture private"),
            "UI diagnostic copy omits private endpoint identifiers and friendly device names");
        var scroll = (ScrollViewer)window.FindName("SettingsScroll"); scroll.ScrollToVerticalOffset(650); root.UpdateLayout();
        var deviceBitmap = new RenderTargetBitmap(780, 840, 96, 96, PixelFormats.Pbgra32); deviceBitmap.Render(root);
        var devicePng = new PngBitmapEncoder(); devicePng.Frames.Add(BitmapFrame.Create(deviceBitmap));
        using (var stream = File.Create(Path.Combine(output, "phone-microphone-devices.png"))) devicePng.Save(stream);
        window.Close();

        var main = new Liuzhuan.MainWindow();
        var loaded = typeof(Liuzhuan.MainWindow).GetMethod("Window_Loaded", BindingFlags.NonPublic | BindingFlags.Instance)!;
        main.Loaded -= (RoutedEventHandler)loaded.CreateDelegate(typeof(RoutedEventHandler), main);
        var mainRoot = (FrameworkElement)main.Content;
        mainRoot.Measure(new Size(380, 680)); mainRoot.Arrange(new Rect(0, 0, 380, 680)); mainRoot.UpdateLayout();
        var entry = (MicrophoneEntry)main.FindName("HeaderMicrophone");
        var pin = (Button)main.FindName("PinBtn");
        var entryOrigin = entry.TransformToAncestor(mainRoot).Transform(new Point());
        var pinOrigin = pin.TransformToAncestor(mainRoot).Transform(new Point());
        Check(entry.ActualWidth >= 64 && entryOrigin.X + entry.ActualWidth <= pinOrigin.X + 1,
            "header microphone fits between branding and fixed controls without overlap at 380 DIP");
        var bars = (Canvas)entry.FindName("WaveCanvas");
        Check(bars.Children.Count == 15 && bars.Children.OfType<System.Windows.Shapes.Rectangle>().All(x => x.Height == 2),
            "header starts with a flat waveform instead of synthetic audio");
        bool opened = false; entry.Click += (_, _) => opened = true;
        // Detach only the production window opener for this offscreen input-routing check.
        var open = typeof(Liuzhuan.MainWindow).GetMethod("MicrophoneEntry_Click", BindingFlags.NonPublic | BindingFlags.Instance)!;
        entry.Click -= (RoutedEventHandler)open.CreateDelegate(typeof(RoutedEventHandler), main);
        ((Button)entry.FindName("EntryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(opened && entry.ServerProvider?.Invoke() == null,
            "header button routes settings clicks without enabling the microphone service");
        entry.ConfigurationWarning = "示例：播放进入虚拟线";
        Check(((TextBlock)entry.FindName("StateText")).Text == "检查声音" && entry.ServerProvider?.Invoke() == null &&
            ((Button)entry.FindName("EntryButton")).ToolTip.ToString()!.Contains("示例"),
            "persistent header warning opens settings without starting capture or inventing waveform samples");
        entry.ConfigurationWarning = "";
        var preview = new RenderTargetBitmap(380, 680, 96, 96, PixelFormats.Pbgra32); preview.Render(mainRoot);
        var previewPng = new PngBitmapEncoder(); previewPng.Frames.Add(BitmapFrame.Create(preview));
        using (var file = File.Create(Path.Combine(output, "microphone-header.png"))) previewPng.Save(file);
        // Do not close MainWindow: its real Closing handler shuts down the test application.
    }
}
