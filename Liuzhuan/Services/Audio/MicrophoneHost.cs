using System.Diagnostics;

namespace Liuzhuan.Services.Audio;

/// <summary>Composition root owned by MainWindow; settings visibility never grants capture consent.</summary>
public sealed class MicrophoneHost
{
    public event Action<AudioSignalEvidence>? SignalVerified;
    private readonly SemaphoreSlim _control = new(1);
    private long _invitationRevision;
    private bool _materialColdStartAllowed = true;
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
    public string CompatibilityStatus { get { lock (_policyGate) return _follow.CompatibilityLabel; } }
    private MicrophoneIssue _lastIssue;
    public string RecoveryStatus => MicrophoneRecovery.Describe(Server?.Snapshot.Issue is { } issue && issue != MicrophoneIssue.None ? issue : _lastIssue);
    public string ObserverStatus => _observer?.Status ?? "会话观察已关闭。";
    public string FollowStatus { get { lock (_policyGate) return _follow.Status; } }
    public async Task<Dictionary<string, object?>> MaterialInvitationAsync()
    {
        // Capture before waiting: stop/recovery revoke invitations already queued, while a later
        // explicit scan can still work after a normal stop. Recovery additionally requires Enable/Retry.
        var revision = Interlocked.Read(ref _invitationRevision);
        await _control.WaitAsync();
        try
        {
            if (revision != Interlocked.Read(ref _invitationRevision)) return new() { ["status"] = "unavailable" };
            if (Server == null)
            {
                if (!_materialColdStartAllowed) return new() { ["status"] = "setup_required" };
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
                if (revision != Interlocked.Read(ref _invitationRevision)) return new() { ["status"] = "unavailable" };
                if (devices.output == null || devices.capture == null) return new() { ["status"] = "setup_required" };
                await StartLockedAsync(devices.output.Id, devices.capture.Id);
            }
            if (revision != Interlocked.Read(ref _invitationRevision)) return new() { ["status"] = "unavailable" };
            if (Server == null) return new() { ["status"] = "unavailable" };
            if (DiagnosticOnly) return new() { ["status"] = "diagnostic_only" };
            if (Server.Snapshot.Phone.Length > 0) return new() { ["status"] = "busy" };
            var token = await Server.RotateInvitationAsync(startWhenReady: true);
            if (revision != Interlocked.Read(ref _invitationRevision)) return new() { ["status"] = "unavailable" };
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
            await StartLockedAsync(outputId, captureId, diagnosticOnly);
            _materialColdStartAllowed = true;
        }
        finally { _control.Release(); }
    }
    private async Task StartLockedAsync(string outputId, string captureId, bool diagnosticOnly = false)
    {
        if (Server != null) return;
        var server = await Task.Run(() => new AudioTransportServer(diagnosticOnly ? new DiagnosticAudioSink() : new VirtualAudioOutput(outputId),
            message => Liuzhuan.Utils.Logger.Run("[MIC] {0}", message)));
        try
        {
            await server.StartAsync();
            Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
            Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerChanged;
            // Device selection is part of the same transaction as startup. A UI continuation must
            // not save old selections after a later recovery has already written a new pair.
            if (!diagnosticOnly)
                await Task.Run(() => new MicrophonePreferences(outputId, captureId).Save(ConfigService.GetEffectiveDataDir()));
            Server = server; OutputId = outputId; CaptureId = captureId; DiagnosticOnly = diagnosticOnly; _lastIssue = MicrophoneIssue.None;
            server.SignalVerified += state =>
            {
                var evidence = new AudioSignalEvidence(outputId, captureId, state.ConnectionId, state.TotalFrames,
                    state.ReadPcmBytes ?? 0, DateTimeOffset.UtcNow, state.Receiving, state.Requested, state.DiagnosticOnly,
                    state.Issue != MicrophoneIssue.None, SignalPeak: state.Peak);
                try { SignalVerified?.Invoke(evidence); } catch { }
            };
        }
        catch
        {
            Server = null;
            Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
            Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerChanged;
            await server.DisposeAsync(); throw;
        }
    }
    public async Task RetrySavedDevicesAsync()
    {
        Interlocked.Increment(ref _invitationRevision);
        await _control.WaitAsync();
        try
        {
            // User action only; resolve, restart and save the exact pair without a recovery interleaving.
            var saved = OutputId.Length > 0 && CaptureId.Length > 0 ? new MicrophonePreferences()
                : await Task.Run(() => MicrophonePreferences.Load(ConfigService.GetEffectiveDataDir()));
            var output = OutputId.Length > 0 ? OutputId : saved.OutputId;
            var capture = CaptureId.Length > 0 ? CaptureId : saved.CaptureId;
            if (output.Length == 0 || capture.Length == 0) throw new InvalidOperationException("尚未保存虚拟音频设备，请先选择设备并开启服务。");
            var present = await Task.Run(() => VirtualAudioOutput.List(NAudio.CoreAudioApi.DataFlow.Render).Any(x => x.Id == output) &&
                VirtualAudioOutput.List(NAudio.CoreAudioApi.DataFlow.Capture).Any(x => x.Id == capture));
            if (!present) { _lastIssue = MicrophoneIssue.Driver; throw new InvalidOperationException(MicrophoneRecovery.Describe(_lastIssue)); }
            await StopLockedAsync();
            await StartLockedAsync(output, capture);
            _materialColdStartAllowed = true;
        }
        finally { _control.Release(); }
    }
    public async Task RestorePreferencesAsync(MicrophonePreferences preferences)
    {
        Interlocked.Increment(ref _invitationRevision);
        await _control.WaitAsync();
        try
        {
            // Explicit settings recovery ends audio and revokes following; it never reopens capture or forwarding.
            _materialColdStartAllowed = false;
            await StopLockedAsync();
            await Task.Run(() =>
            {
                var inventory = AudioRouteScanner.Scan();
                if (!inventory.RenderReadable || !inventory.CaptureReadable ||
                    !inventory.Endpoints.Any(x => x.Id == preferences.OutputId && x.Flow == NAudio.CoreAudioApi.DataFlow.Render && x.Active) ||
                    !inventory.Endpoints.Any(x => x.Id == preferences.CaptureId && x.Flow == NAudio.CoreAudioApi.DataFlow.Capture && x.Active))
                    throw new InvalidOperationException("原虚拟音频设备已变化，未恢复。请刷新设备列表后重试。");
                preferences.Save(ConfigService.GetEffectiveDataDir());
            });
            OutputId = preferences.OutputId; CaptureId = preferences.CaptureId; DiagnosticOnly = false;
        }
        finally { _control.Release(); }
    }
    public async Task<bool> RepairConfigurationAsync(MicrophonePreferences preferences)
    {
        // Explicit action by an authenticated client only. Recheck under the same lock used by
        // ManualForwardAsync so a newly started dictation session cannot be interrupted by repair.
        Interlocked.Increment(ref _invitationRevision);
        await _control.WaitAsync();
        try
        {
            if (Server?.Snapshot.Requested == true) return false;
            var present = await Task.Run(() =>
            {
                var inventory = AudioRouteScanner.Scan();
                return inventory.RenderReadable && inventory.CaptureReadable &&
                    inventory.Endpoints.Any(x => x.Id == preferences.OutputId && x.Flow == NAudio.CoreAudioApi.DataFlow.Render && x.Active) &&
                    inventory.Endpoints.Any(x => x.Id == preferences.CaptureId && x.Flow == NAudio.CoreAudioApi.DataFlow.Capture && x.Active);
            });
            if (!present) throw new InvalidOperationException("虚拟音频设备已变化，请重新检查后再修复。");
            _materialColdStartAllowed = false;
            await StopLockedAsync();
            await StartLockedAsync(preferences.OutputId, preferences.CaptureId);
            _materialColdStartAllowed = true;
            return true;
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
                lock (_policyGate)
                {
                    _follow.Observe(_observer.Snapshot, Environment.TickCount64);
                    _follow.Enable(Server.Snapshot);
                }
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
        Interlocked.Increment(ref _invitationRevision);
        await _control.WaitAsync();
        try { await StopLockedAsync(); }
        finally { _control.Release(); }
    }
    private async Task StopLockedAsync()
    {
        // Detach the service first so observer cleanup failures cannot leave a live host published.
        var server = Server; Server = null;
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerChanged;
        try { await StopObservingLockedAsync(); }
        finally { if (server != null) await Task.Run(async () => await server.DisposeAsync()); }
    }
    private async void OnSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs args)
    { if (args.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock) try { _lastIssue = MicrophoneIssue.Sleep; await StopAsync(); } catch { } }
    private async void OnPowerChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs args)
    { if (args.Mode == Microsoft.Win32.PowerModes.Suspend) try { _lastIssue = MicrophoneIssue.Sleep; await StopAsync(); } catch { } }
}
