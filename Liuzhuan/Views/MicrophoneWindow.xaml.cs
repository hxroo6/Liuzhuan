using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Liuzhuan.Services.Audio;
using Liuzhuan.Services;
using Liuzhuan.Services.Lan;
using Liuzhuan.Utils;
using NAudio.CoreAudioApi;
using ZXing;

namespace Liuzhuan.Views;

public partial class MicrophoneWindow : Window
{
    private readonly MicrophoneHost _host;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _busy;
    private bool _syncChoices;
    public MicrophoneWindow(MicrophoneHost host)
    {
        _host = host;
        InitializeComponent();
        NetworkChoice.ItemsSource = LanNetUtil.GetLanAddresses(); NetworkChoice.SelectedIndex = 0;
        _timer.Tick += (_, _) => Update(); _timer.Start();
        Closed += (_, _) => _timer.Stop();
        Loaded += async (_, _) => await RunAsync(RefreshAsync);
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true; ErrorText.Text = ""; Update();
        try { await action(); }
        catch (Exception ex) { ErrorText.Text = ex.Message; }
        finally { _busy = false; Update(); }
    }
    private async Task RefreshAsync()
    {
        var devices = await Task.Run(() => (VirtualAudioOutput.List(DataFlow.Render), VirtualAudioOutput.List(DataFlow.Capture),
            MicrophonePreferences.Load(ConfigService.GetEffectiveDataDir())));
        OutputChoice.ItemsSource = devices.Item1; CaptureChoice.ItemsSource = devices.Item2;
        var outputId = string.IsNullOrEmpty(_host.OutputId) ? devices.Item3.OutputId : _host.OutputId;
        var captureId = string.IsNullOrEmpty(_host.CaptureId) ? devices.Item3.CaptureId : _host.CaptureId;
        OutputChoice.SelectedItem = string.IsNullOrEmpty(outputId) ? devices.Item1.FirstOrDefault(x => x.Name.StartsWith("CABLE Input", StringComparison.OrdinalIgnoreCase)) : devices.Item1.FirstOrDefault(x => x.Id == outputId);
        CaptureChoice.SelectedItem = string.IsNullOrEmpty(captureId) ? devices.Item2.FirstOrDefault(x => x.Name.StartsWith("CABLE Output", StringComparison.OrdinalIgnoreCase)) : devices.Item2.FirstOrDefault(x => x.Id == captureId);
        DeviceHint.Text = OutputChoice.SelectedItem == null || CaptureChoice.SelectedItem == null
            ? "原设备已不可用或未找到虚拟设备对，请重新选择。不会自动替换为系统扬声器。可先使用“仅诊断收音”。"
            : "已找到候选虚拟设备对。流转写入 CABLE Input，Codex 应选择 CABLE Output；仍需实际收音验证。";
        if (devices.Item1.Length == 0) ErrorText.Text = "未发现播放设备。请安装虚拟音频驱动后刷新。";
        else if (OutputChoice.SelectedItem == null) ErrorText.Text = "请明确选择已安装的虚拟播放端；流转不会自动使用默认扬声器。";
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunAsync(RefreshAsync);
    private void DriverLink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        e.Handled = true;
        if (e.Uri.AbsoluteUri is not ("https://vb-audio.com/Cable/" or "https://vb-audio.com/Services/licensing.htm")) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { ErrorText.Text = "未能打开浏览器，请手动访问 vb-audio.com 查看下载与许可。"; }
    }
    private async void Enable_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (DiagnosticChoice.IsChecked == true)
        { await _host.StartAsync("", "", true); return; }
        if (OutputChoice.SelectedItem is not AudioEndpointChoice output || CaptureChoice.SelectedItem is not AudioEndpointChoice capture)
            throw new InvalidOperationException("请选择一对虚拟播放端和录音端。");
        if (MessageBox.Show(this, $"流转将写入：{output.Name}\nCodex 应读取：{capture.Name}\n\n请确认这是相连的虚拟音频设备，而不是扬声器。", "确认音频方向", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        await _host.StartAsync(output.Id, capture.Id);
        await Task.Run(() => new MicrophonePreferences(output.Id, capture.Id).Save(ConfigService.GetEffectiveDataDir()));
    });
    private async void Disable_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    { ObserveChoice.IsChecked = false; await _host.StopAsync(); QrImage.Visibility = Visibility.Collapsed; });
    private async void Qr_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_host.Server == null || NetworkChoice.SelectedItem is not LanNetUtil.LanAddress address)
            throw new InvalidOperationException("请选择可用局域网网卡并开启音频服务。");
        bool integrated = LanConfig.Enabled && !string.IsNullOrEmpty(LanConfig.Password);
        var payload = integrated ? CombinedPairing.Create(address.Address, LanConfig.Port, LanConfig.Password, null)
            : await _host.Server.PairingPayloadAsync(address.Address);
        var writer = new BarcodeWriterPixelData { Format = BarcodeFormat.QR_CODE,
            Options = new ZXing.QrCode.QrCodeEncodingOptions { Width = 280, Height = 280, Margin = 2 } };
        var pixels = writer.Write(payload);
        var image = new WriteableBitmap(pixels.Width, pixels.Height, 96, 96, PixelFormats.Bgra32, null);
        image.WritePixels(new Int32Rect(0, 0, pixels.Width, pixels.Height), pixels.Pixels, pixels.Width * 4, 0);
        QrImage.Source = image; QrImage.Visibility = Visibility.Visible;
        PairingText.Text = integrated ? "手机从连接页扫码一次：素材鉴权后自动握手麦克风，首次允许系统录音权限即可。"
            : $"素材服务未开启，本码仅供旧版手动音频配对，{DateTime.Now.AddMinutes(2):HH:mm:ss}到期。建议先开启素材服务，再用统一连接码。";
    });
    private void Network_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    { if (QrImage?.Visibility == Visibility.Visible) { QrImage.Visibility = Visibility.Collapsed; PairingText.Text = "网卡已更换，请重新生成二维码。"; } }
    private async void Start_Click(object sender, RoutedEventArgs e) => await RunAsync(() => _host.ManualForwardAsync(true));
    private async void Stop_Click(object sender, RoutedEventArgs e) => await RunAsync(() => _host.ManualForwardAsync(false));
    private async void End_Click(object sender, RoutedEventArgs e) => await RunAsync(_host.EndPhoneAsync);
    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(MicrophoneDiagnostics.Format(_host.Server?.Snapshot, _host.Server?.LastSession));
            DiagnosticHint.Text = "已复制脱敏诊断，可粘贴用于排查。";
        }
        catch { DiagnosticHint.Text = "剪贴板暂时被占用，请稍后重试。"; }
    }
    private async void Observe_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncChoices) return;
        bool requested = ObserveChoice.IsChecked == true;
        await RunAsync(() => requested ? _host.StartObservingAsync() : _host.StopObservingAsync());
    }
    private async void Calibrate_Click(object sender, RoutedEventArgs e) => await RunAsync(_host.BeginCalibrationAsync);
    private async void Automatic_Changed(object sender, RoutedEventArgs e)
    { if (!_syncChoices) { bool requested = AutomaticChoice.IsChecked == true; await RunAsync(() => _host.SetAutomaticAsync(requested)); } }
    private void Update()
    {
        var server = _host.Server; var state = server?.Snapshot;
        StatusText.Text = state?.Status ?? "音频服务已关闭";
        StatsText.Text = state?.ConnectionId > 0
            ? $"本次连接 {TimeSpan.FromSeconds(state.SessionSeconds):hh\\:mm\\:ss} · 已收到 {state.TotalFrames} 帧 · 缓冲 {state.BufferedMs}ms"
            : server?.LastSession != null ? $"上次会话已收到 {server.LastSession.TotalFrames} 帧；可展开链路诊断查看。" : "尚未收到手机音频";
        ReceiveLevel.Value = Math.Sqrt(state?.Peak ?? 0) * 100;
        LevelText.Text = state?.Quiet == true ? "安静待命 · 音频数据已暂停，心跳仍连接" : state?.Receiving == true ? $"电脑已收到音频 · {state.Dbfs:F0} dBFS · 峰值 {state.Peak:P0}" : "等待手机音频（连接或请求成功不代表已经收音）";
        OutputText.Text = state == null ? "音频服务尚未开启；素材连接不等于麦克风连接。" : state.DiagnosticOnly
            ? "当前仅验证手机→电脑，不向虚拟设备输出，Codex 无法从此模式收音。"
            : state.Quiet ? "安静省流量正常工作；说话后会恢复发送，无需再次扫码。"
            : state.Receiving ? state.ReadPcmBytes > 0
                ? $"虚拟播放端本次已读取 {state.ReadPcmBytes / (AudioProtocol.Rate * 2.0):F2} 秒音频；Codex 应选择对应录音端。"
                : "电脑已收到音频，等待虚拟播放端读取；连接成功不代表听写已收到。"
            : state.Phone.Length == 0 ? "在手机点左上音波图连接当前电脑；素材已连接时无需再扫码。"
            : state.Ready && !state.Requested ? "手机仍在采音待命；点“开始转发”恢复，或在手机结束会话。"
            : "输出等待手机准备和有效音频；暂停后需先在手机允许再次请求。";
        DiagnosticText.Text = MicrophoneDiagnostics.Format(state, server?.LastSession);
        bool enabled = server != null;
        EnableButton.IsEnabled = !enabled && !_busy; DisableButton.IsEnabled = enabled && !_busy;
        OutputChoice.IsEnabled = CaptureChoice.IsEnabled = RefreshButton.IsEnabled = !enabled && !_busy;
        DiagnosticChoice.IsEnabled = !enabled && !_busy;
        QrButton.IsEnabled = enabled && !_busy && string.IsNullOrEmpty(state?.Phone);
        StartButton.IsEnabled = enabled && !_busy && state?.Ready == true && state.Requested == false;
        StopButton.IsEnabled = enabled && !_busy && (state?.Requested == true || _host.Automatic);
        EndButton.IsEnabled = enabled && !_busy && !string.IsNullOrEmpty(state?.Phone);
        ObserveChoice.IsEnabled = enabled && !_busy && !_host.DiagnosticOnly;
        CalibrateButton.IsEnabled = ObserveChoice.IsEnabled;
        AutomaticChoice.IsEnabled = ObserveChoice.IsEnabled && _host.Calibrated && state?.Ready == true;
        _syncChoices = true;
        ObserveChoice.IsChecked = _host.Observing; AutomaticChoice.IsChecked = _host.Automatic;
        _syncChoices = false;
        ObserverText.Text = _host.ObserverStatus;
        FollowText.Text = _host.FollowStatus;
        if (!string.IsNullOrEmpty(state?.Phone)) QrImage.Visibility = Visibility.Collapsed;
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
