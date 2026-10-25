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
    private bool _initializing = true, _refreshingChoices, _healthBusy, _closed, _loaded;
    private AudioRouteInventory? _inventory;
    private AudioRouteReport? _routeReport;
    private Task<AudioRouteInventory>? _scanTask;
    private string _selectedOutputId = "", _selectedCaptureId = "", _lastRouteConfiguration = "";
    private DateTime _nextHealthCheck = DateTime.MinValue;
    private readonly AudioConfigurationGuard _configurationGuard;
    private readonly Action? _configurationChanged;
    private bool _profileBusy, _profileAgain;
    private AudioProfileAssessment? _profileAssessment;
    private AudioSignalEvidence? _lastSignalEvidence;
    public MicrophoneWindow(MicrophoneHost host, AudioConfigurationGuard? configurationGuard = null, Action? configurationChanged = null)
    {
        _host = host;
        _configurationGuard = configurationGuard ?? new AudioConfigurationGuard(App.DataDir);
        _configurationChanged = configurationChanged;
        InitializeComponent();
        NetworkChoice.ItemsSource = LanNetUtil.GetLanAddresses(); NetworkChoice.SelectedIndex = 0;
        _initializing = false;
        _timer.Tick += async (_, _) =>
        {
            Update();
            if (_loaded && IsVisible && !_busy && DateTime.UtcNow >= _nextHealthCheck) await RefreshHealthAsync();
        };
        _timer.Start();
        Closed += (_, _) => { _closed = true; _timer.Stop(); };
        Loaded += async (_, _) =>
        {
            _loaded = true;
            MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 32);
            Height = Math.Min(Height, MaxHeight);
            await RunAsync(RefreshAsync);
        };
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true; ErrorText.Text = ""; Update();
        try { await action(); }
        catch (Exception ex) { ErrorText.Text = ex.Message; }
        finally { _busy = false; Update(); }
    }
    public void RefreshAfterExternalRepair()
    {
        if (!_closed) _ = RunAsync(RefreshAsync);
    }
    private async Task RefreshAsync()
    {
        var scan = ReadInventoryAsync();
        var preferences = await Task.Run(() => MicrophonePreferences.Load(ConfigService.GetEffectiveDataDir()));
        var inventory = await scan;
        if (_closed) return;
        _inventory = inventory;
        var outputs = inventory.Endpoints.Where(x => x.Flow == DataFlow.Render && x.Active).Select(x => new AudioEndpointChoice(x.Id, x.Name)).ToArray();
        var captures = inventory.Endpoints.Where(x => x.Flow == DataFlow.Capture && x.Active).Select(x => new AudioEndpointChoice(x.Id, x.Name)).ToArray();
        var outputId = string.IsNullOrEmpty(_host.OutputId) ? preferences.OutputId : _host.OutputId;
        var captureId = string.IsNullOrEmpty(_host.CaptureId) ? preferences.CaptureId : _host.CaptureId;
        _refreshingChoices = true;
        try
        {
            OutputChoice.ItemsSource = outputs; CaptureChoice.ItemsSource = captures;
            OutputChoice.SelectedItem = string.IsNullOrEmpty(outputId) ? outputs.FirstOrDefault(x => AudioRouteHealth.StandardOutput(x.Name)) : outputs.FirstOrDefault(x => x.Id == outputId);
            CaptureChoice.SelectedItem = string.IsNullOrEmpty(captureId) ? captures.FirstOrDefault(x => AudioRouteHealth.StandardCapture(x.Name)) : captures.FirstOrDefault(x => x.Id == captureId);
            // Preserve a missing saved ID for diagnosis; never replace it with a default or a different device.
            _selectedOutputId = (OutputChoice.SelectedItem as AudioEndpointChoice)?.Id ?? outputId;
            _selectedCaptureId = (CaptureChoice.SelectedItem as AudioEndpointChoice)?.Id ?? captureId;
        }
        finally { _refreshingChoices = false; }
        DeviceHint.Text = OutputChoice.SelectedItem == null || CaptureChoice.SelectedItem == null
            ? "原设备已不可用或未找到虚拟设备对，请重新选择。不会自动替换为系统扬声器。可先使用“仅诊断收音”。"
            : "已显示本机设备选择。标准方向为 CABLE Input → CABLE Output；先核对上方体检，再实际收音验证。";
        ApplyRouteReport();
        _nextHealthCheck = DateTime.UtcNow.AddSeconds(10);
    }
    private Task<AudioRouteInventory> ReadInventoryAsync() => _scanTask is { IsCompleted: false } ? _scanTask : _scanTask = Task.Run(AudioRouteScanner.Scan);
    private async Task RefreshHealthAsync()
    {
        if (_closed || _healthBusy) return;
        _healthBusy = true; HealthCheckButton.IsEnabled = false; RouteCheckTime.Text = "正在只读检查本机声音设置…";
        try
        {
            var inventory = await ReadInventoryAsync();
            if (_closed) return;
            _inventory = inventory; ApplyRouteReport();
        }
        catch
        {
            if (!_closed)
            {
                _inventory = null; _routeReport = null;
                RouteSummary.Text = "体检暂时无法完成，请重试";
                PlaybackRouteText.Text = OutputRouteText.Text = RecordingRouteText.Text = "当前状态未知";
                RouteFindings.ItemsSource = null;
                RouteCheckTime.Text = "无法读取不等于设备缺失；不会修改默认设备或启动采音。";
            }
        }
        finally
        {
            _healthBusy = false; _nextHealthCheck = DateTime.UtcNow.AddSeconds(10);
            if (!_closed) HealthCheckButton.IsEnabled = !_busy;
        }
    }
    private string RouteConfiguration => _host.Server != null
        ? $"active|{_host.OutputId}|{_host.CaptureId}|{_host.DiagnosticOnly}"
        : $"setup|{_selectedOutputId}|{_selectedCaptureId}|{DiagnosticChoice.IsChecked == true}";
    private void ApplyRouteReport()
    {
        if (_inventory == null || _closed) return;
        var active = _host.Server != null;
        _routeReport = AudioRouteHealth.Review(_inventory, active ? _host.OutputId : _selectedOutputId,
            active ? _host.CaptureId : _selectedCaptureId, active ? _host.DiagnosticOnly : DiagnosticChoice.IsChecked == true);
        _lastRouteConfiguration = RouteConfiguration;
        RouteSummary.Text = _routeReport.Summary;
        RouteSummary.Foreground = (Brush)FindResource(_routeReport.Findings.Any(x => x.Severity != AudioRouteSeverity.Information) ? "WarmBrush" : "AccentBrush");
        PlaybackRouteText.Text = _routeReport.Playback; OutputRouteText.Text = _routeReport.Output; RecordingRouteText.Text = _routeReport.Recording;
        RouteFindings.ItemsSource = _routeReport.Findings;
        RouteCheckTime.Text = $"最近体检 {DateTime.Now:HH:mm:ss} · 窗口可见时每 10 秒更新。仅核对配置，不代表虚拟线连通或听写成功。";
        _ = RefreshProfileAsync();
    }
    private MicrophonePreferences EffectivePreferences() => _host.Server != null
        ? new(_host.OutputId, _host.CaptureId) : new(_selectedOutputId, _selectedCaptureId);
    private async Task RefreshProfileAsync()
    {
        _profileAgain = true;
        if (_profileBusy || _inventory == null || _closed) return;
        _profileBusy = true;
        try
        {
            do
            {
                _profileAgain = false;
                var inventory = _inventory; var preferences = EffectivePreferences(); var route = RouteConfiguration;
                bool diagnostic = _host.Server != null ? _host.DiagnosticOnly : DiagnosticChoice.IsChecked == true;
                var result = await Task.Run(() => _configurationGuard.Evaluate(inventory, preferences, diagnostic));
                if (_closed) return;
                if (RouteConfiguration != route) { _profileAgain = true; continue; }
                _profileAssessment = result;
                ProfileSummary.Text = result.Summary;
                ProfileSavedTime.Text = result.ConfirmedAtUtc is { } at ? $"你确认可用的时间：{at.ToLocalTime():yyyy-MM-dd HH:mm:ss}" :
                    result.HasObservedRoute ? "已有音频读取验证记录；尚未确认实体外放和听写结果。" : "首次使用：先实际测试，再保存。";
                ProfileFindings.ItemsSource = result.Notices;
                RestoreGoodProfileButton.IsEnabled = !_busy && result.CanRestore;
            } while (_profileAgain && !_closed);
        }
        catch { if (!_closed) ProfileSummary.Text = "配置保护暂时无法读取，原文件会保留。"; }
        finally { _profileBusy = false; }
    }
    private void RememberSignalEvidence()
    {
        var state = _host.Server?.Snapshot;
        if (state is { ConnectionId: > 0, Requested: true, Receiving: true, DiagnosticOnly: false, TotalFrames: >= 25, ReadPcmBytes: >= 16000,
            Issue: MicrophoneIssue.None, Peak: > 0 } && state.HeartbeatAgeMs < 6000)
            _lastSignalEvidence = new(_host.OutputId, _host.CaptureId, state.ConnectionId, state.TotalFrames,
                state.ReadPcmBytes.Value, DateTimeOffset.UtcNow, SignalPeak: state.Peak);
    }
    private async void SaveGoodProfile_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        RememberSignalEvidence();
        var inventory = await ReadInventoryAsync();
        if (_closed) return;
        var state = _host.Server?.Snapshot;
        if (_lastSignalEvidence is not { } evidence || state?.ConnectionId != evidence.ConnectionId ||
            _host.OutputId != evidence.OutputId || _host.CaptureId != evidence.CaptureId || state.Issue != MicrophoneIssue.None)
        { ProfileActionText.Text = "请先开启转发并对手机说话，实际确认外放和听写正常后再保存。"; return; }
        var preferences = EffectivePreferences();
        var result = await Task.Run(() => _configurationGuard.ConfirmGood(inventory, preferences, evidence, userConfirmed: true));
        if (_closed) return;
        _inventory = inventory; ProfileActionText.Text = result.Message;
        await RefreshProfileAsync();
    });
    private async void RestoreGoodProfile_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var inventory = await ReadInventoryAsync();
        var restore = await Task.Run(() => _configurationGuard.PrepareRestore(inventory));
        if (_closed) return;
        if (restore.Preferences == null) { ProfileActionText.Text = restore.Message; return; }
        await _host.RestorePreferencesAsync(restore.Preferences);
        _configurationChanged?.Invoke();
        _lastSignalEvidence = null; QrImage.Visibility = Visibility.Collapsed;
        ProfileActionText.Text = "已恢复流转设备选择，音频服务已关闭。请核对 Windows 外放设置，按需重新开启服务。";
        await RefreshAsync();
    });
    private async void HealthCheck_Click(object sender, RoutedEventArgs e) => await RefreshHealthAsync();
    private void DeviceChoice_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_initializing || _refreshingChoices) return;
        _selectedOutputId = (OutputChoice?.SelectedItem as AudioEndpointChoice)?.Id ?? "";
        _selectedCaptureId = (CaptureChoice?.SelectedItem as AudioEndpointChoice)?.Id ?? "";
        ApplyRouteReport();
    }
    private void DeviceMode_Changed(object sender, RoutedEventArgs e) { if (!_initializing) ApplyRouteReport(); }
    private void OpenSoundSettings(AudioSettingsPage page)
    {
        var command = AudioSettingsCommand.For(page);
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(command.FileName, command.Arguments) { UseShellExecute = true }); }
        catch { ErrorText.Text = "未能打开系统声音面板，请到 Windows 设置 → 系统 → 声音手动检查。"; }
    }
    private void SoundSettings_Click(object sender, RoutedEventArgs e) => OpenSoundSettings(AudioSettingsPage.Sound);
    private void PlaybackSettings_Click(object sender, RoutedEventArgs e) => OpenSoundSettings(AudioSettingsPage.Playback);
    private void RecordingSettings_Click(object sender, RoutedEventArgs e) => OpenSoundSettings(AudioSettingsPage.Recording);
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
        _configurationChanged?.Invoke();
    });
    private async void Disable_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    { ObserveChoice.IsChecked = false; await _host.StopAsync(); QrImage.Visibility = Visibility.Collapsed; _configurationChanged?.Invoke(); });
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
    private async void RetryAudio_Click(object sender, RoutedEventArgs e) => await RunAsync(_host.RetrySavedDevicesAsync);
    private async void Stop_Click(object sender, RoutedEventArgs e) => await RunAsync(() => _host.ManualForwardAsync(false));
    private async void End_Click(object sender, RoutedEventArgs e) => await RunAsync(_host.EndPhoneAsync);
    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(DiagnosticsText());
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
        if (_closed) return;
        var server = _host.Server; var state = server?.Snapshot;
        RememberSignalEvidence();
        StatusText.Text = state?.Status ?? "音频服务已关闭";
        RecoveryText.Text = _host.RecoveryStatus;
        RetryAudioButton.IsEnabled = !_busy;
        CompatibilityText.Text = "自动跟随兼容性：" + _host.CompatibilityStatus + " · 校准最多 45 秒";
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
        if (_inventory != null && _lastRouteConfiguration != RouteConfiguration) ApplyRouteReport();
        DiagnosticText.Text = DiagnosticsText();
        bool enabled = server != null;
        EnableButton.IsEnabled = !enabled && !_busy; DisableButton.IsEnabled = enabled && !_busy;
        OutputChoice.IsEnabled = CaptureChoice.IsEnabled = RefreshButton.IsEnabled = !enabled && !_busy;
        DiagnosticChoice.IsEnabled = !enabled && !_busy;
        HealthCheckButton.IsEnabled = !_healthBusy && !_busy;
        SaveGoodProfileButton.IsEnabled = !_busy;
        RestoreGoodProfileButton.IsEnabled = !_busy && _profileAssessment?.CanRestore == true;
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
    private string DiagnosticsText() => MicrophoneDiagnostics.Format(_host.Server?.Snapshot, _host.Server?.LastSession) + "\n\n" +
        (_routeReport?.SanitizedText ?? "音频路由体检：尚未完成，当前状态未知。");
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
