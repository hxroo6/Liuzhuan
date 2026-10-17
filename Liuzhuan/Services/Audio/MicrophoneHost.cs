using System.Diagnostics;

namespace Liuzhuan.Services.Audio;

/// <summary>Composition root owned by MainWindow; settings visibility never grants capture consent.</summary>
public sealed class MicrophoneHost
{
    private readonly SemaphoreSlim _control = new(1);
    private readonly FollowDemandPolicy _follow = new();
    private readonly object _policyGate = new();
    private CaptureSessionObserver? _observer;
    private CancellationTokenSource? _observeLife;
    private Task? _observeLoop;
    public AudioTransportServer? Server { get; private set; }
    public string OutputId { get; private set; } = "";
    public string CaptureId { get; private set; } = "";
    public bool DiagnosticOnly { get; private set; }
    public bool Observing => _observer != null;
    public bool Automatic { get { lock (_policyGate) return _follow.Enabled; } }
    public bool Calibrated { get { lock (_policyGate) return _follow.Calibrated; } }
    public string ObserverStatus => _observer?.Status ?? "会话观察已关闭。";
    public string FollowStatus { get { lock (_policyGate) return _follow.Status; } }
    public async Task<Dictionary<string, object?>> MaterialInvitationAsync()
    {
        if (Server == null)
        {
            var devices = await Task.Run(() =>
            {
                var saved = MicrophonePreferences.Load(ConfigService.GetEffectiveDataDir());
                var outputs = VirtualAudioOutput.List(NAudio.CoreAudioApi.DataFlow.Render);
                var captures = VirtualAudioOutput.List(NAudio.CoreAudioApi.DataFlow.Capture);
                // Exact saved IDs take precedence. Only the known virtual CABLE pair is suggested on first setup.
                var output = saved.OutputId.Length > 0 ? outputs.FirstOrDefault(x => x.Id == saved.OutputId)
                    : outputs.FirstOrDefault(x => x.Name.StartsWith("CABLE Input (", StringComparison.OrdinalIgnoreCase));
                var capture = saved.CaptureId.Length > 0 ? captures.FirstOrDefault(x => x.Id == saved.CaptureId)
                    : captures.FirstOrDefault(x => x.Name.StartsWith("CABLE Output (", StringComparison.OrdinalIgnoreCase));
                return (output, capture);
            });
            if (devices.output == null || devices.capture == null) return new() { ["status"] = "setup_required" };
            await StartAsync(devices.output.Id, devices.capture.Id);
            await Task.Run(() => new MicrophonePreferences(devices.output.Id, devices.capture.Id).Save(ConfigService.GetEffectiveDataDir()));
        }
        await _control.WaitAsync();
        try
        {
            if (Server == null) return new() { ["status"] = "unavailable" };
            if (DiagnosticOnly) return new() { ["status"] = "diagnostic_only" };
            if (Server.Snapshot.Phone.Length > 0) return new() { ["status"] = "busy" };
            var token = await Server.RotateInvitationAsync(startWhenReady: true);
            return new() { ["status"] = "ready", ["kind"] = AudioProtocol.Kind, ["v"] = AudioProtocol.Version,
                ["port"] = Server.Port, ["token"] = token };
        }
        finally { _control.Release(); }
    }
    public async Task StartAsync(string outputId, string captureId, bool diagnosticOnly = false)
    {
        await _control.WaitAsync();
        try
        {
            if (Server != null) return;
            var server = await Task.Run(() => new AudioTransportServer(diagnosticOnly ? new DiagnosticAudioSink() : new VirtualAudioOutput(outputId),
                message => Liuzhuan.Utils.Logger.Run("[MIC] {0}", message)));
            try
            {
                await server.StartAsync(); Server = server; OutputId = outputId; CaptureId = captureId; DiagnosticOnly = diagnosticOnly;
                Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
                Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerChanged;
            }
            catch { await server.DisposeAsync(); throw; }
        }
        finally { _control.Release(); }
    }
    public async Task StartObservingAsync()
    {
        await _control.WaitAsync();
        try
        {
            if (_observer != null || Server == null || DiagnosticOnly) return;
            _observer = new CaptureSessionObserver(CaptureId);
            _observeLife = new(); var token = _observeLife.Token;
            _observeLoop = Task.Run(() => ObserveAsync(token), token);
        }
        finally { _control.Release(); }
    }
    public async Task BeginCalibrationAsync()
    {
        await StartObservingAsync();
        await _control.WaitAsync();
        try
        {
            lock (_policyGate) _follow.BeginCalibration();
            if (Server?.Snapshot.Requested == true) await Server.SetForwardingAsync(false);
        }
        finally { _control.Release(); }
    }
    public async Task SetAutomaticAsync(bool enabled)
    {
        await _control.WaitAsync();
        try
        {
            if (enabled)
            {
                if (Server == null || _observer == null || !_observer.Snapshot.Healthy ||
                    Stopwatch.GetElapsedTime(_observer.Snapshot.At).TotalSeconds > 1)
                    throw new InvalidOperationException("请先开启并校准录音会话观察。");
                lock (_policyGate) _follow.Enable(Server.Snapshot);
            }
            else
            {
                bool wasAutomatic; lock (_policyGate) { wasAutomatic = _follow.Enabled; _follow.Disable(); }
                if (wasAutomatic && Server?.Snapshot.Requested == true) await Server.SetForwardingAsync(false);
            }
        }
        finally { _control.Release(); }
    }
    public async Task ManualForwardAsync(bool value)
    {
        await _control.WaitAsync();
        try
        {
            lock (_policyGate) _follow.Disable("已切换到手动转发；自动跟随不会重新开启。");
            if (Server != null) await Server.SetForwardingAsync(value);
        }
        finally { _control.Release(); }
    }
    public async Task EndPhoneAsync()
    {
        await _control.WaitAsync();
        try { lock (_policyGate) _follow.Disable("手机会话已结束。"); if (Server != null) await Server.EndPhoneAsync(); }
        finally { _control.Release(); }
    }
    private async Task ObserveAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            while (await timer.WaitForNextTickAsync(token))
            {
                await _control.WaitAsync(token);
                try
                {
                    if (_observer == null || Server == null) continue;
                    var observation = _observer.Snapshot; var phone = Server.Snapshot;
                    bool? demand;
                    lock (_policyGate)
                    {
                        bool wasEnabled = _follow.Enabled;
                        _follow.Observe(observation, Environment.TickCount64);
                        demand = wasEnabled && !_follow.Enabled ? false : _follow.Evaluate(observation, phone, Environment.TickCount64,
                            observation.At != 0 && Stopwatch.GetElapsedTime(observation.At).TotalMilliseconds <= 1000);
                    }
                    if (demand.HasValue && demand.Value != phone.Requested)
                        await Server.SetForwardingAsync(demand.Value, phone.ConnectionId, phone.StopRevision);
                }
                catch
                {
                    lock (_policyGate) _follow.Disable("会话变化或设备异常，自动跟随已关闭。");
                    if (Server?.Snapshot.Requested == true) try { await Server.SetForwardingAsync(false); } catch { }
                }
                finally { _control.Release(); }
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task StopObservingLockedAsync()
    {
        bool wasAutomatic; lock (_policyGate) { wasAutomatic = _follow.Enabled; _follow.Reset(); }
        _observeLife?.Cancel();
        if (_observeLoop != null) try { await _observeLoop; } catch (OperationCanceledException) { }
        _observer?.Dispose(); _observer = null; _observeLoop = null;
        _observeLife?.Dispose(); _observeLife = null;
        if (wasAutomatic && Server?.Snapshot.Requested == true) await Server.SetForwardingAsync(false);
    }
    public async Task StopObservingAsync()
    {
        await _control.WaitAsync(); try { await StopObservingLockedAsync(); } finally { _control.Release(); }
    }
    public async Task StopAsync()
    {
        await _control.WaitAsync();
        try
        {
            await StopObservingLockedAsync(); var server = Server; Server = null;
            Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
            Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerChanged;
            if (server != null) await Task.Run(async () => await server.DisposeAsync());
        }
        finally { _control.Release(); }
    }
    private async void OnSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs args)
    { if (args.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock) try { await StopAsync(); } catch { } }
    private async void OnPowerChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs args)
    { if (args.Mode == Microsoft.Win32.PowerModes.Suspend) try { await StopAsync(); } catch { } }
}
