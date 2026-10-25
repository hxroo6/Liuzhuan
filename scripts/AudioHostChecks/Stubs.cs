using NAudio.CoreAudioApi;

namespace NAudio.CoreAudioApi { public enum DataFlow { Render, Capture } }
namespace Microsoft.Win32
{
    public enum SessionSwitchReason { SessionLock, Other }
    public sealed class SessionSwitchEventArgs(SessionSwitchReason reason) : EventArgs { public SessionSwitchReason Reason => reason; }
    public enum PowerModes { Suspend, Other }
    public sealed class PowerModeChangedEventArgs(PowerModes mode) : EventArgs { public PowerModes Mode => mode; }
    public delegate void SessionSwitchEventHandler(object sender, SessionSwitchEventArgs args);
    public delegate void PowerModeChangedEventHandler(object sender, PowerModeChangedEventArgs args);
    public static class SystemEvents
    {
        public static event SessionSwitchEventHandler? SessionSwitch;
        public static event PowerModeChangedEventHandler? PowerModeChanged;
        public static void Lock() => SessionSwitch?.Invoke(typeof(SystemEvents), new(SessionSwitchReason.SessionLock));
        public static void Suspend() => PowerModeChanged?.Invoke(typeof(SystemEvents), new(PowerModes.Suspend));
    }
}
namespace Liuzhuan.Services
{
    public static class ConfigService { public static string Directory = ""; public static string GetEffectiveDataDir() => Directory; }
}
namespace Liuzhuan.Utils { public static class Logger { public static void Run(string format, params object[] args) { } } }
namespace Liuzhuan.Services.Audio
{
    public sealed record AudioSignalEvidence(string OutputId, string CaptureId, long ConnectionId, long TotalFrames,
        long ReadPcmBytes, DateTimeOffset At, bool Receiving, bool Requested, bool DiagnosticOnly, bool Failed, float SignalPeak);
    public enum MicrophoneIssue { None, Driver, Sleep }
    public static class MicrophoneRecovery { public static string Describe(MicrophoneIssue issue) => issue.ToString(); }
    public static class AudioProtocol { public const string Kind = "test-audio"; public const int Version = 1; }
    public sealed record AudioEndpointChoice(string Id, string Name);
    public sealed record AudioRouteEndpoint(string Id, DataFlow Flow, bool Active = true);
    public sealed record AudioRouteInventory(AudioRouteEndpoint[] Endpoints, bool RenderReadable = true, bool CaptureReadable = true);
    public static class AudioRouteScanner
    {
        public static AudioRouteInventory Inventory = new([]);
        public static Action? BeforeScan;
        public static AudioRouteInventory Scan() { BeforeScan?.Invoke(); return Inventory; }
    }
    public sealed class VirtualAudioOutput(string outputId)
    {
        public string OutputId => outputId;
        public static AudioEndpointChoice[] Outputs = [];
        public static AudioEndpointChoice[] Captures = [];
        public static Action<DataFlow>? BeforeList;
        public static AudioEndpointChoice[] List(DataFlow flow)
        { BeforeList?.Invoke(flow); return flow == DataFlow.Render ? Outputs : Captures; }
    }
    public sealed class DiagnosticAudioSink { }
    public sealed record MicSnapshot(string Phone = "", long ConnectionId = 0, long TotalFrames = 0, long? ReadPcmBytes = 0,
        bool Receiving = false, bool Requested = false, bool DiagnosticOnly = false, MicrophoneIssue Issue = MicrophoneIssue.None,
        float Peak = 0, bool Ready = false, long StopRevision = 0);
    public sealed class AudioTransportServer(object sink, Action<string>? log = null) : IAsyncDisposable
    {
        public static Func<Task>? BeforeStart;
        public static int Created, Starts, Disposals, Invitations;
        public static bool FailStart;
        public bool Disposed { get; private set; }
        public string OutputId => (sink as VirtualAudioOutput)?.OutputId ?? "";
        public int Port => 12345;
        public MicSnapshot Snapshot { get; set; } = new(DiagnosticOnly: sink is DiagnosticAudioSink);
        public event Action<MicSnapshot>? SignalVerified;
        public async Task StartAsync()
        { _ = log; Interlocked.Increment(ref Created); if (BeforeStart != null) await BeforeStart(); if (FailStart) throw new InvalidOperationException("start-failure"); Interlocked.Increment(ref Starts); }
        public Task<string> RotateInvitationAsync(bool startWhenReady)
        { Interlocked.Increment(ref Invitations); return Task.FromResult(Guid.NewGuid().ToString("N")); }
        public Task SetForwardingAsync(bool value, long connectionId = 0, long stopRevision = 0)
        { Snapshot = Snapshot with { Requested = value }; return Task.CompletedTask; }
        public Task EndPhoneAsync() { Snapshot = Snapshot with { Phone = "", Requested = false }; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { if (!Disposed) Interlocked.Increment(ref Disposals); Disposed = true; Snapshot = Snapshot with { Requested = false }; return ValueTask.CompletedTask; }
        public void Emit() => SignalVerified?.Invoke(Snapshot);
    }
    public sealed record CaptureObservation(bool Healthy, int UnknownActive, string[] ActiveOwners, long At);
    public sealed class CaptureSessionObserver(string id) : IDisposable
    {
        public CaptureObservation Snapshot => new(true, 0, [], System.Diagnostics.Stopwatch.GetTimestamp());
        public string Status => "test-observer:" + id;
        public void Dispose() { }
    }
}
