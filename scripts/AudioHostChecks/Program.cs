using System.Reflection;
using Liuzhuan.Services;
using Liuzhuan.Services.Audio;
using NAudio.CoreAudioApi;

internal static class Program
{
    private static int _checks;
    private static void Check(bool pass, string name)
    { if (!pass) throw new InvalidOperationException(name); _checks++; Console.WriteLine("PASS " + name); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string Status(Dictionary<string, object?> invitation) => (string)invitation["status"]!;
    private static readonly MicrophonePreferences Old = new("old-out", "old-in"), Good = new("good-out", "good-in");
    private static void Reset(string root)
    {
        ConfigService.Directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ConfigService.Directory);
        AudioTransportServer.BeforeStart = null; AudioTransportServer.FailStart = false;
        AudioRouteScanner.BeforeScan = null;
        VirtualAudioOutput.BeforeList = null;
        VirtualAudioOutput.Outputs = [new(Old.OutputId, "CABLE Input (old)"), new(Good.OutputId, "CABLE Input (good)")];
        VirtualAudioOutput.Captures = [new(Old.CaptureId, "CABLE Output (old)"), new(Good.CaptureId, "CABLE Output (good)")];
        AudioRouteScanner.Inventory = new([new(Old.OutputId, DataFlow.Render), new(Old.CaptureId, DataFlow.Capture),
            new(Good.OutputId, DataFlow.Render), new(Good.CaptureId, DataFlow.Capture)]);
    }
    private static void Saved(MicrophonePreferences expected, string name) => Check(MicrophonePreferences.Load(ConfigService.Directory) == expected, name);
    private static async Task Failure(Task work, string name)
    { bool failed = false; try { await work; } catch { failed = true; } Check(failed, name); }

    public static async Task Main(string[] args)
    {
        var root = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "liuzhuan-audio-host-checks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await FirstScanAndStop(root);
        await EnumerationAgainstRestore(root);
        await QueuedInvitations(root);
        await StartupAgainstRestore(root);
        await FailedRestore(root);
        await FailedSave(root);
        await RetryAgainstRestore(root);
        await FailedRestart(root);
        await ExplicitRepair(root);
        await RepairForwardingRace(root);
        await FailedRepair(root);
        Console.WriteLine($"Audio host checks passed: {_checks}; synthetic devices only; no recording/playback/default changes.");
    }
    private static async Task FirstScanAndStop(string root)
    {
        Reset(root); var host = new MicrophoneHost();
        try
        {
            var invitation = await host.MaterialInvitationAsync();
            Check(Status(invitation) == "ready" && host.Server != null && host.OutputId == Old.OutputId, "first material scan retains single-scan virtual-pair startup");
            Saved(Old, "first-scan selected pair is saved inside the host transaction");
            var previous = host.Server; await host.StopAsync();
            Check(previous!.Disposed && host.Server == null, "normal stop disposes current server");
            Check(Status(await host.MaterialInvitationAsync()) == "ready", "new explicit scan after completed normal stop may reopen saved pair");
            var active = host.Server; await host.StartAsync(Good.OutputId, Good.CaptureId);
            Saved(Old, "no-op enable on an active service cannot persist a pair that was never started");
            Check(host.Server == active && host.OutputId == Old.OutputId, "active service retains its actual device pair");
        }
        finally { await host.StopAsync(); }
    }
    private static async Task EnumerationAgainstRestore(string root)
    {
        Reset(root); Old.Save(ConfigService.Directory); var host = new MicrophoneHost();
        using var listRelease = new ManualResetEventSlim(); var listed = Gate();
        VirtualAudioOutput.BeforeList = flow => { if (flow == DataFlow.Render) { listed.TrySetResult(); if (!listRelease.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); } };
        var invitation = host.MaterialInvitationAsync(); await listed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var restore = host.RestorePreferencesAsync(Good); listRelease.Set();
        await restore.WaitAsync(TimeSpan.FromSeconds(5));
        Check(Status(await invitation) == "unavailable", "restore request revokes an invitation resolving the old pair");
        Check(host.Server == null && !host.Automatic && !host.Observing && !host.Calibrated, "successful restore leaves service and observing/follow consent off");
        Saved(Good, "old invitation cannot overwrite restored preferences");
        VirtualAudioOutput.BeforeList = null;
        Check(Status(await host.MaterialInvitationAsync()) == "setup_required" && host.Server == null, "fresh invitation cannot cold-start immediately after configuration restoration");
        await host.StartAsync(Good.OutputId, Good.CaptureId);
        Check(Status(await host.MaterialInvitationAsync()) == "ready", "explicit enable grants material invitation use after restore");
        await host.StopAsync();
    }
    private static async Task QueuedInvitations(string root)
    {
        Reset(root); var host = new MicrophoneHost();
        var control = (SemaphoreSlim)typeof(MicrophoneHost).GetField("_control", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        await control.WaitAsync(); var before = AudioTransportServer.Starts;
        var first = host.MaterialInvitationAsync(); var second = host.MaterialInvitationAsync(); var stop = host.StopAsync();
        control.Release(); await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Check(Status(await first) == "unavailable" && Status(await second) == "unavailable", "stop revokes all invitations queued before stop intent");
        Check(host.Server == null && AudioTransportServer.Starts == before, "revoked queued invitations never instantiate a server");
        Check(Status(await host.MaterialInvitationAsync()) == "ready", "stop revision does not permanently block a later scan");
        await host.StopAsync();
    }
    private static async Task StartupAgainstRestore(string root)
    {
        Reset(root); var host = new MicrophoneHost(); var started = Gate(); var release = Gate();
        AudioTransportServer.BeforeStart = () => { started.TrySetResult(); return release.Task; };
        var enable = host.StartAsync(Old.OutputId, Old.CaptureId); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var restore = host.RestorePreferencesAsync(Good); release.SetResult(); await Task.WhenAll(enable, restore).WaitAsync(TimeSpan.FromSeconds(5));
        Check(host.Server == null, "restore queued behind active startup finishes with service stopped");
        Saved(Good, "startup and preference save cannot straddle restore transaction");
        Check(Status(await host.MaterialInvitationAsync()) == "setup_required", "completed enable before restore does not undo restore cold-start block");
        AudioTransportServer.BeforeStart = null;
    }
    private static async Task FailedRestore(string root)
    {
        Reset(root); var host = new MicrophoneHost(); await host.StartAsync(Old.OutputId, Old.CaptureId);
        await host.StartObservingAsync(); await host.BeginCalibrationAsync(); await host.ManualForwardAsync(true);
        var oldServer = host.Server; AudioRouteScanner.Inventory = new([]);
        await Failure(host.RestorePreferencesAsync(Good), "missing exact endpoint makes restore fail");
        Saved(Old, "failed endpoint validation leaves previous preferences intact");
        Check(oldServer!.Disposed && host.Server == null && !host.Observing && !host.Automatic && !host.Calibrated, "failed restore never leaves audio or follow consent running");
        Check(Status(await host.MaterialInvitationAsync()) == "setup_required", "failed restore cannot be undone by a later material invitation");
    }
    private static async Task FailedSave(string root)
    {
        Reset(root); var host = new MicrophoneHost(); await host.StartAsync(Old.OutputId, Old.CaptureId); var oldServer = host.Server;
        // Atomic move destination is deliberately blocked by a directory, without changing real user files.
        var file = Path.Combine(ConfigService.Directory, "microphone.json"); var original = File.ReadAllBytes(file);
        File.Move(file, file + ".preserved"); Directory.CreateDirectory(file);
        try { await Failure(host.RestorePreferencesAsync(Good), "preference write failure is reported"); }
        finally { Directory.Delete(file); File.Move(file + ".preserved", file); }
        Check(File.ReadAllBytes(file).SequenceEqual(original), "failed save leaves previous preference bytes intact");
        Check(oldServer!.Disposed && host.Server == null, "failed preference save does not revive audio service");
        Check(Status(await host.MaterialInvitationAsync()) == "setup_required", "save failure retains explicit-enable recovery requirement");
    }
    private static async Task RetryAgainstRestore(string root)
    {
        Reset(root); var host = new MicrophoneHost(); await host.StartAsync(Old.OutputId, Old.CaptureId);
        using var listRelease = new ManualResetEventSlim(); var listed = Gate();
        VirtualAudioOutput.BeforeList = flow => { if (flow == DataFlow.Render) { listed.TrySetResult(); if (!listRelease.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); } };
        var retry = host.RetrySavedDevicesAsync(); await listed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var restore = host.RestorePreferencesAsync(Good); listRelease.Set(); await Task.WhenAll(retry, restore).WaitAsync(TimeSpan.FromSeconds(5));
        Saved(Good, "retry resolve/stop/start/save cannot interleave with restore");
        Check(host.Server == null && Status(await host.MaterialInvitationAsync()) == "setup_required", "restore ordered after retry remains final and service stays off");
        VirtualAudioOutput.BeforeList = null; await host.RetrySavedDevicesAsync();
        Check(host.Server != null && host.OutputId == Good.OutputId && !host.Observing && !host.Automatic && !host.Server.Snapshot.Requested,
            "explicit retry reopens restored exact pair without restoring forwarding or automatic following");
        Check(Status(await host.MaterialInvitationAsync()) == "ready", "explicit retry re-enables future material invitations");
        await host.StopAsync();
    }
    private static async Task FailedRestart(string root)
    {
        Reset(root); Old.Save(ConfigService.Directory); var host = new MicrophoneHost(); await host.RestorePreferencesAsync(Good);
        var disposed = AudioTransportServer.Disposals; AudioTransportServer.FailStart = true;
        await Failure(host.StartAsync(Old.OutputId, Old.CaptureId), "explicit startup failure is reported");
        Saved(Good, "failed explicit startup does not persist unstarted device choices");
        Check(host.Server == null && AudioTransportServer.Disposals == disposed + 1, "failed explicit startup disposes the incomplete server");
        Check(Status(await host.MaterialInvitationAsync()) == "setup_required", "failed enable cannot remove the restoration cold-start block");
        AudioTransportServer.FailStart = false;
    }
    private static async Task ExplicitRepair(string root)
    {
        Reset(root); var host = new MicrophoneHost(); await host.StartAsync(Old.OutputId, Old.CaptureId);
        await host.ManualForwardAsync(true); var previous = host.Server;
        Check(!await host.RepairConfigurationAsync(Good), "repair refuses an active forwarding request while holding host control lock");
        Check(host.Server == previous && !previous!.Disposed && host.Server!.Snapshot.Requested, "refused repair preserves ongoing dictation service");
        Saved(Old, "refused repair does not change saved device choice");
        await host.ManualForwardAsync(false); await host.StartObservingAsync();
        Check(await host.RepairConfigurationAsync(Good), "explicit repair may replace an idle service with validated active endpoints");
        Saved(Good, "explicit repair atomically saves the actual newly started pair");
        Check(previous!.Disposed && host.Server != null && !host.Server.Snapshot.Requested && !host.Observing && !host.Automatic,
            "repair revokes observation and follow state without starting forwarding");
        Check(Status(await host.MaterialInvitationAsync()) == "ready", "successful explicit repair permits a fresh authenticated invitation");
        await host.StopAsync();
    }
    private static async Task RepairForwardingRace(string root)
    {
        Reset(root); var host = new MicrophoneHost(); await host.StartAsync(Old.OutputId, Old.CaptureId);
        using var release = new ManualResetEventSlim(); var entered = Gate();
        AudioRouteScanner.BeforeScan = () => { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); };
        var repair = host.RepairConfigurationAsync(Good); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var forward = host.ManualForwardAsync(true);
        Check(!forward.IsCompleted, "manual forwarding cannot begin between repair's idle check and replacement");
        release.Set(); Check(await repair.WaitAsync(TimeSpan.FromSeconds(5)), "repair completes the locked transaction before queued forwarding");
        await forward.WaitAsync(TimeSpan.FromSeconds(5));
        Check(host.OutputId == Good.OutputId && host.Server!.Snapshot.Requested, "later explicit manual forwarding targets the replacement service");
        AudioRouteScanner.BeforeScan = null; await host.StopAsync();
    }
    private static async Task FailedRepair(string root)
    {
        Reset(root); var host = new MicrophoneHost(); await host.StartAsync(Old.OutputId, Old.CaptureId); var previous = host.Server;
        AudioRouteScanner.Inventory = new([]);
        await Failure(host.RepairConfigurationAsync(Good), "repair validates active endpoints again immediately before mutation");
        Check(host.Server == previous && !previous!.Disposed, "failed revalidation does not replace idle current service");
        Saved(Old, "failed revalidation retains original preferences");
        AudioRouteScanner.Inventory = new([new(Good.OutputId, DataFlow.Render), new(Good.CaptureId, DataFlow.Capture)]);
        AudioTransportServer.FailStart = true;
        await Failure(host.RepairConfigurationAsync(Good), "repair reports replacement startup failure");
        Check(host.Server == null && previous!.Disposed, "failed replacement never revives old idle service");
        Saved(Old, "failed replacement startup preserves previous preference file");
        Check(Status(await host.MaterialInvitationAsync()) == "setup_required", "failed replacement requires another explicit user action");
        AudioTransportServer.FailStart = false;
    }
}
