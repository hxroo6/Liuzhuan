using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Liuzhuan.Services.Audio;

/// <summary>
/// Event-driven, read-only observer of Windows default and selected endpoints and their volume settings.
/// Does not open an audio stream, alter devices or decide whether a change is a mistake.
/// The callback runs on a background MTA thread and must dispatch UI work without waiting.
/// </summary>
public sealed class AudioConfigurationMonitor : IDisposable
{
    private const int CoalesceMilliseconds = 750;
    private readonly Action _changed;
    private readonly Func<MicrophonePreferences>? _selectionProvider;
    private readonly object _lifecycle = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Timer _timer;
    private Thread? _worker;
    private int _started, _disposed, _pending, _rebind;

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    public AudioConfigurationMonitor(Action changed, Func<MicrophonePreferences>? selectionProvider = null)
    {
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
        _selectionProvider = selectionProvider;
        // One shot only: an idle monitor has no periodic timer or device polling.
        _timer = new Timer(_ => SignalWorker(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Rebind after a saved selection changes. The provider runs on the observer thread;
    /// this method never reads devices synchronously and does not start background polling.
    /// </summary>
    public void RequestRefresh() => QueueChange(true);

    public void Start()
    {
        lock (_lifecycle)
        {
            if (_started != 0 || Volatile.Read(ref _disposed) != 0) return;
            _started = 1;
            try
            {
                _worker = new Thread(Observe) { IsBackground = true, Name = "liuzhuan-audio-configuration" };
                _worker.SetApartmentState(ApartmentState.MTA);
                _worker.Start();
            }
            catch
            {
                // Monitoring is advisory. Failure must not affect sound or normal application startup.
                Interlocked.Exchange(ref _disposed, 1);
                _timer.Dispose();
                _wake.Dispose();
                _worker = null;
            }
        }
    }

    private void QueueChange(bool rebind)
    {
        // Core Audio may call on its own system threads: no COM access, user callback or waits here.
        if (Volatile.Read(ref _disposed) != 0) return;
        if (rebind) Interlocked.Exchange(ref _rebind, 1);
        if (Interlocked.Exchange(ref _pending, 1) != 0) return;
        try { _timer.Change(CoalesceMilliseconds, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    private void SignalWorker()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try { _wake.Set(); } catch (ObjectDisposedException) { }
    }

    private sealed class DeviceNotifications(AudioConfigurationMonitor owner) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => owner.QueueChange(true);
        public void OnDeviceAdded(string deviceId) => owner.QueueChange(true);
        public void OnDeviceRemoved(string deviceId) => owner.QueueChange(true);
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => owner.QueueChange(true);
        public void OnPropertyValueChanged(string deviceId, PropertyKey key) => owner.QueueChange(true);
    }

    private sealed class VolumeBinding(MMDevice device, AudioEndpointVolume volume,
        AudioEndpointVolumeNotificationDelegate callback) : IDisposable
    {
        public void Dispose()
        {
            try { volume.OnVolumeNotification -= callback; } catch { }
            try { device.Dispose(); } catch { }
        }
    }

    private void RebindVolumes(MMDeviceEnumerator enumerator, List<VolumeBinding> bindings)
    {
        foreach (var binding in bindings) binding.Dispose();
        bindings.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Bind(Func<MMDevice> resolve, DataFlow expectedFlow)
        {
            if (Volatile.Read(ref _disposed) != 0 || bindings.Count >= 8) return;
            MMDevice? device = null;
            try
            {
                device = resolve();
                if (device.State != DeviceState.Active || device.DataFlow != expectedFlow || !seen.Add(device.ID)) return;
                var volume = device.AudioEndpointVolume;
                AudioEndpointVolumeNotificationDelegate callback = _ => QueueChange(false);
                volume.OnVolumeNotification += callback;
                bindings.Add(new(device, volume, callback));
                device = null; // The binding owns this endpoint until the next device event or disposal.
            }
            catch { /* Missing/unreadable endpoints remain unknown to the separate health scanner. */ }
            finally { try { device?.Dispose(); } catch { } }
        }
        foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
        foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            Bind(() => enumerator.GetDefaultAudioEndpoint(flow, role), flow);
        }

        // A virtual microphone route should not be the system default speaker. Observe it by
        // its exact saved ID so muting CABLE alone still triggers the separate health scanner.
        MicrophonePreferences? selected = null;
        try { selected = _selectionProvider?.Invoke(); } catch { /* Advisory settings reads may fail. */ }
        if (!string.IsNullOrWhiteSpace(selected?.OutputId))
            Bind(() => enumerator.GetDevice(selected.OutputId), DataFlow.Render);
        if (!string.IsNullOrWhiteSpace(selected?.CaptureId))
            Bind(() => enumerator.GetDevice(selected.CaptureId), DataFlow.Capture);
    }

    private void NotifyChanged()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try { _changed(); } catch { /* Advisory callbacks must never terminate the observer. */ }
    }

    private void Observe()
    {
        MMDeviceEnumerator? enumerator = null;
        var notifications = new DeviceNotifications(this);
        var bindings = new List<VolumeBinding>(8);
        bool initialized = false, registered = false;
        try
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            Marshal.ThrowExceptionForHR(CoInitializeEx(IntPtr.Zero, 0));
            initialized = true;
            enumerator = new MMDeviceEnumerator();
            Marshal.ThrowExceptionForHR(enumerator.RegisterEndpointNotificationCallback(notifications));
            registered = true;
            RebindVolumes(enumerator, bindings);
            NotifyChanged();
            while (Volatile.Read(ref _disposed) == 0)
            {
                _wake.WaitOne();
                if (Volatile.Read(ref _disposed) != 0) break;
                Interlocked.Exchange(ref _pending, 0);
                if (Interlocked.Exchange(ref _rebind, 0) != 0) RebindVolumes(enumerator, bindings);
                NotifyChanged();
            }
        }
        catch
        {
            // Let the caller perform one ordinary read-only check, reporting unreadable state honestly.
            NotifyChanged();
        }
        finally
        {
            if (registered) try { enumerator?.UnregisterEndpointNotificationCallback(notifications); } catch { }
            foreach (var binding in bindings) binding.Dispose();
            try { enumerator?.Dispose(); } catch { }
            if (initialized) CoUninitialize();
            _timer.Dispose();
            _wake.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _timer.Dispose();
        // Signal, do not join: disposal never waits for Core Audio or a UI dispatch callback.
        try { _wake.Set(); } catch (ObjectDisposedException) { }
        lock (_lifecycle) { if (_worker == null) _wake.Dispose(); }
    }
}
