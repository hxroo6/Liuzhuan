using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Liuzhuan.Services.Audio;

public sealed record CaptureObservation(long At, bool Healthy, string[] ActiveOwners, int OtherActive, int UnknownActive, string Status);

/// <summary>Read-only MTA observer. Only S_OK ownership in the official Codex package is trusted.</summary>
public sealed class CaptureSessionObserver : IDisposable
{
    private readonly CancellationTokenSource _life = new();
    private readonly Thread _thread;
    private CaptureObservation _snapshot = new(0, false, Array.Empty<string>(), 0, 0, "正在观察所选录音端点…");
    public CaptureObservation Snapshot => Volatile.Read(ref _snapshot);
    public string Status => Snapshot.Status;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFamilyName(SafeProcessHandle process, ref uint length, StringBuilder? family);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint id);
    private sealed class Entry : IDisposable
    {
        public readonly IAudioSessionControl2 Control;
        private Process? _process;
        private uint _pid;
        public string Owner = "";
        public Entry(IAudioSessionControl control) => Control = (IAudioSessionControl2)control;
        public bool Verified(uint pid)
        {
            if (_process != null) return pid == _pid && !_process.HasExited && Owner.Length > 0;
            _pid = pid; _process = Process.GetProcessById(checked((int)pid));
            var path = _process.MainModule?.FileName ?? "";
            if (!Path.GetFileName(path).Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase)) return false;
            using var handle = OpenProcess(0x1000, false, pid);
            if (handle.IsInvalid) return false;
            uint length = 0;
            if (GetPackageFamilyName(handle, ref length, null) != 122 || length is 0 or > 512) return false;
            var family = new StringBuilder((int)length);
            if (GetPackageFamilyName(handle, ref length, family) != 0 || family.ToString() != "OpenAI.Codex_2p2nqsd0c76g0") return false;
            Owner = $"{pid}:{_process.StartTime.ToUniversalTime().Ticks}:{path}";
            return true;
        }
        public void Dispose() { _process?.Dispose(); RawCaptureSessions.Release(Control); }
    }
    private sealed class Notification : IAudioSessionNotification
    {
        private readonly ConcurrentQueue<IAudioSessionControl> _pending;
        public int Overflow;
        public Notification(ConcurrentQueue<IAudioSessionControl> pending) => _pending = pending;
        public int OnSessionCreated(IAudioSessionControl session)
        {
            try
            {
                if (_pending.Count < 128) _pending.Enqueue(session);
                else { Interlocked.Exchange(ref Overflow, 1); RawCaptureSessions.Release(session); }
            }
            catch { Interlocked.Exchange(ref Overflow, 1); }
            return 0;
        }
    }
    public CaptureSessionObserver(string endpointId)
    {
        _thread = new Thread(() => Observe(endpointId)) { IsBackground = true, Name = "liuzhuan-audio-observer" };
        _thread.SetApartmentState(ApartmentState.MTA); _thread.Start();
    }
    private void Observe(string id)
    {
        var sessions = new Dictionary<string, Entry>();
        var pending = new ConcurrentQueue<IAudioSessionControl>();
        var callback = new Notification(pending);
        RawCaptureSessions.Enumerator? enumerator = null;
        RawCaptureSessions.Device? device = null;
        RawCaptureSessions.Manager? manager = null;
        RawCaptureSessions.Sessions? initial = null;
        bool registered = false;
        bool initialized = false;
        void Add(IAudioSessionControl control)
        {
            Entry? entry = null;
            try
            {
                entry = new Entry(control);
                RawCaptureSessions.Check(entry.Control.GetSessionInstanceIdentifier(out var key));
                if (sessions.Count >= 256) throw new InvalidOperationException();
                if (sessions.TryAdd(key, entry)) return;
            }
            catch { if (entry == null) RawCaptureSessions.Release(control); else entry.Dispose(); throw; }
            entry?.Dispose();
        }
        try
        {
            Marshal.ThrowExceptionForHR(RawCaptureSessions.CoInitializeEx(IntPtr.Zero, 0)); initialized = true;
            enumerator = RawCaptureSessions.CreateEnumerator();
            RawCaptureSessions.Check(enumerator.GetDevice(id, out device));
            var iid = typeof(RawCaptureSessions.Manager).GUID;
            RawCaptureSessions.Check(device.Activate(ref iid, 23, IntPtr.Zero, out var activated));
            manager = (RawCaptureSessions.Manager)activated;
            RawCaptureSessions.Check(manager.RegisterSessionNotification(callback)); registered = true;
            RawCaptureSessions.Check(manager.GetSessionEnumerator(out initial));
            RawCaptureSessions.Check(initial.GetCount(out var count)); // Enables notifications.
            for (int i = 0; i < count; i++) { RawCaptureSessions.Check(initial.GetSession(i, out var control)); Add(control); }
            RawCaptureSessions.Release(initial); initial = null;
            while (!_life.IsCancellationRequested)
            {
                if (callback.Overflow != 0) throw new InvalidOperationException();
                while (pending.TryDequeue(out var control)) Add(control);
                RawCaptureSessions.Check(device.GetState(out var deviceState));
                if (deviceState != DeviceState.Active) throw new InvalidOperationException();
                var owners = new HashSet<string>(); int other = 0, unknown = 0;
                foreach (var entry in sessions.ToArray())
                {
                    try
                    {
                        RawCaptureSessions.Check(entry.Value.Control.GetState(out var state));
                        if (state == AudioSessionState.AudioSessionStateExpired)
                        { sessions.Remove(entry.Key); entry.Value.Dispose(); continue; }
                        if (state != AudioSessionState.AudioSessionStateActive) continue;
                        if (entry.Value.Control.GetProcessId(out var pid) != 0 || pid == 0) { unknown++; continue; }
                        if (entry.Value.Verified(pid)) owners.Add(entry.Value.Owner); else other++;
                    }
                    catch { unknown++; }
                }
                Volatile.Write(ref _snapshot, new(Stopwatch.GetTimestamp(), true, owners.ToArray(), other, unknown,
                    $"已验证 Codex 录音 {owners.Count} · 其他应用 {other} · 归属不明 {unknown}。只读取会话状态，不读取录音内容。"));
                _life.Token.WaitHandle.WaitOne(200);
            }
        }
        catch { Volatile.Write(ref _snapshot, new(Stopwatch.GetTimestamp(), false, Array.Empty<string>(), 0, 0, "录音端点或会话归属不可验证；自动跟随不可用，请使用手动转发。")); }
        finally
        {
            if (registered) try { manager?.UnregisterSessionNotification(callback); } catch { }
            foreach (var session in sessions.Values) session.Dispose();
            while (pending.TryDequeue(out var session)) RawCaptureSessions.Release(session);
            RawCaptureSessions.Release(initial); RawCaptureSessions.Release(manager);
            RawCaptureSessions.Release(device); RawCaptureSessions.Release(enumerator);
            if (initialized) RawCaptureSessions.CoUninitialize();
            _life.Dispose();
        }
    }
    public void Dispose() { try { _life.Cancel(); } catch (ObjectDisposedException) { } }
}
