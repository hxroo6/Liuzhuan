using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Liuzhuan.Services.Audio;
using Liuzhuan.Views;

internal static class MicrophoneUiChecks
{
    public static void Run(string output)
    {
        var notice = new Liuzhuan.Services.Lan.ConnectionNoticePolicy();
        if (!notice.ShouldNotify(0) || notice.ShouldNotify(1000) || notice.ShouldNotify(14999) || !notice.ShouldNotify(15000))
            throw new Exception("authenticated reconnect notifications must be limited to once per 15 seconds");
        Console.WriteLine("PASS authenticated connection notice cooldown prevents reconnect popup storms");
        var host = new MicrophoneHost(); var window = new MicrophoneWindow(host);
        foreach (var name in new[] { "OutputChoice", "CaptureChoice" })
        {
            var choice = (ComboBox)window.FindName(name);
            choice.ItemsSource = new[] { new AudioEndpointChoice("test-only", name == "OutputChoice" ? "CABLE Input（离屏示例）" : "CABLE Output（离屏示例）") };
            choice.SelectedIndex = 0;
        }
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(620, 740)); root.Arrange(new Rect(0, 0, 620, 740)); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(620, 740, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
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
        Check(((TextBlock)window.FindName("StatsText")).Text.Length == 0, "settings construction never invents session statistics");
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
        var preview = new RenderTargetBitmap(380, 680, 96, 96, PixelFormats.Pbgra32); preview.Render(mainRoot);
        var previewPng = new PngBitmapEncoder(); previewPng.Frames.Add(BitmapFrame.Create(preview));
        using (var file = File.Create(Path.Combine(output, "microphone-header.png"))) previewPng.Save(file);
        // Do not close MainWindow: its real Closing handler shuts down the test application.
    }
}
