using System.Diagnostics;
using Liuzhuan.Services.Audio;
using NAudio.CoreAudioApi;

internal static class Program
{
    private static int _passed;

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        Console.WriteLine("PASS " + name);
        _passed++;
    }

    private static async Task Wait(Func<bool> predicate)
    {
        var elapsed = Stopwatch.StartNew();
        while (!predicate())
        {
            if (elapsed.ElapsedMilliseconds > 10000) throw new TimeoutException("Audio monitor check timed out.");
            await Task.Delay(20);
        }
    }

    private static async Task Main()
    {
        int calls = 0, callbackThread = 0;
        using var monitor = new AudioConfigurationMonitor(() =>
        {
            callbackThread = Environment.CurrentManagedThreadId;
            Interlocked.Increment(ref calls);
        });

        var elapsed = Stopwatch.StartNew();
        monitor.Start();
        monitor.Start();
        Check(elapsed.ElapsedMilliseconds < 1500, "Start is nonblocking and repeated Start is accepted");
        await Wait(() => Volatile.Read(ref calls) == 1);
        Check(MMDeviceEnumerator.Created == 1, "repeated Start creates one observer");
        Check(MMDeviceEnumerator.Registered == 1 && AudioEndpointVolume.Subscribers == 2,
            "six role choices share two distinct active endpoint volume subscriptions");

        int reads = MMDeviceEnumerator.Reads;
        await Task.Delay(1200);
        Check(calls == 1 && MMDeviceEnumerator.Reads == reads,
            "idle observer performs no scans or periodic callbacks");

        int sourceThread = Environment.CurrentManagedThreadId;
        elapsed.Restart();
        for (int i = 0; i < 1000; i++) MMDeviceEnumerator.Client!.OnPropertyValueChanged("fixture-id", default);
        Check(elapsed.ElapsedMilliseconds < 1500,
            "device callback storm queues without device reads or callback blocking");
        Check(calls == 1, "notification does not synchronously invoke callback");
        await Wait(() => calls == 2);
        await Task.Delay(150);
        Check(calls == 2, "1000 device events coalesce into one callback");
        Check(MMDeviceEnumerator.Reads == reads + 6 && AudioEndpointVolume.Subscribers == 2,
            "coalesced device event rebinds bounded subscriptions without a leak");
        Check(callbackThread != sourceThread, "application callback runs away from the notification caller");

        reads = MMDeviceEnumerator.Reads;
        AudioEndpointVolume.RaiseAll();
        await Wait(() => calls == 3);
        Check(MMDeviceEnumerator.Reads == reads, "volume notification does not re-enumerate or rebind endpoints");

        monitor.Dispose();
        monitor.Dispose();
        await Wait(() => MMDeviceEnumerator.Unregistered == 1 && AudioEndpointVolume.Subscribers == 0);
        Check(MMDeviceEnumerator.Unregistered == 1 && AudioEndpointVolume.Subscribers == 0,
            "Dispose unregisters endpoint and volume callbacks once");
        int stopped = calls;
        await Task.Delay(900);
        Check(calls == stopped, "disposed observer does not notify later");

        using (var notStarted = new AudioConfigurationMonitor(() => throw new InvalidOperationException()))
        {
            notStarted.Dispose();
            notStarted.Start();
        }
        Check(MMDeviceEnumerator.Created == 1, "Dispose before Start is safe and never registers");

        using var second = new AudioConfigurationMonitor(() => Interlocked.Increment(ref calls));
        second.Start();
        await Wait(() => calls == stopped + 1);
        MMDeviceEnumerator.Client!.OnDeviceRemoved("fixture-id");
        second.Dispose();
        await Wait(() => MMDeviceEnumerator.Unregistered == 2);
        await Task.Delay(900);
        Check(calls == stopped + 1, "pending delayed change is suppressed after disposal");

        MMDeviceEnumerator.Devices["virtual-output-a"] = (DataFlow.Render, DeviceState.Active);
        MMDeviceEnumerator.Devices["virtual-capture-a"] = (DataFlow.Capture, DeviceState.Active);
        MMDeviceEnumerator.Devices["virtual-output-b"] = (DataFlow.Render, DeviceState.Active);
        MMDeviceEnumerator.Devices["virtual-capture-b"] = (DataFlow.Capture, DeviceState.Active);
        MMDeviceEnumerator.Devices["disabled-output"] = (DataFlow.Render, DeviceState.Disabled);
        var selection = new MicrophonePreferences("virtual-output-a", "virtual-capture-a");
        int providerCalls = 0, providerThread = 0;
        bool failProvider = false;
        using var selected = new AudioConfigurationMonitor(() => Interlocked.Increment(ref calls), () =>
        {
            providerThread = Environment.CurrentManagedThreadId;
            Interlocked.Increment(ref providerCalls);
            if (failProvider) throw new IOException("Fixture settings read failed.");
            return Volatile.Read(ref selection);
        });
        int before = calls;
        selected.Start();
        await Wait(() => calls == before + 1);
        Check(AudioEndpointVolume.Subscribers == 4 && providerCalls == 1,
            "selected virtual output and capture are observed alongside physical defaults");
        Check(providerThread != Environment.CurrentManagedThreadId,
            "selected endpoint provider runs on observer worker");
        reads = MMDeviceEnumerator.Reads;
        before = calls;
        AudioEndpointVolume.Raise("virtual-output-a");
        await Wait(() => calls == before + 1);
        Check(MMDeviceEnumerator.Reads == reads,
            "muting a nondefault selected output triggers callback without device reads");
        before = calls;
        AudioEndpointVolume.Raise("virtual-capture-a");
        await Wait(() => calls == before + 1);
        Check(MMDeviceEnumerator.Reads == reads,
            "selected capture volume change also triggers callback without rebind");

        Volatile.Write(ref selection, new("virtual-output-b", "virtual-capture-b"));
        before = calls;
        selected.RequestRefresh();
        Check(calls == before, "selection refresh is asynchronous");
        await Wait(() => calls == before + 1);
        Check(AudioEndpointVolume.Subscribers == 4 && MMDeviceEnumerator.Reads == reads + 8,
            "selection refresh replaces bindings once without leaking old subscriptions");
        before = calls;
        AudioEndpointVolume.Raise("virtual-output-a");
        AudioEndpointVolume.Raise("virtual-capture-a");
        await Task.Delay(900);
        Check(calls == before, "previous selection no longer triggers notifications");
        AudioEndpointVolume.Raise("virtual-output-b");
        await Wait(() => calls == before + 1);
        Check(calls == before + 1, "replacement selection is observed");

        Volatile.Write(ref selection, new("Render", "Capture"));
        before = calls;
        selected.RequestRefresh();
        await Wait(() => calls == before + 1);
        Check(AudioEndpointVolume.Subscribers == 2,
            "selected endpoints that equal defaults share subscriptions");

        MMDeviceEnumerator.DistinctDefaultRoles = true;
        Volatile.Write(ref selection, new("virtual-output-b", "virtual-capture-b"));
        before = calls;
        reads = MMDeviceEnumerator.Reads;
        for (int i = 0; i < 1000; i++) selected.RequestRefresh();
        await Wait(() => calls == before + 1);
        await Task.Delay(150);
        Check(calls == before + 1 && MMDeviceEnumerator.Reads == reads + 8 && AudioEndpointVolume.Subscribers == 8,
            "1000 explicit refreshes coalesce and subscriptions stay bounded to eight");
        reads = MMDeviceEnumerator.Reads;
        int providerReads = providerCalls;
        await Task.Delay(1200);
        Check(MMDeviceEnumerator.Reads == reads && providerCalls == providerReads,
            "selected endpoint observer still performs no idle device or settings polling");

        Volatile.Write(ref selection, new("disabled-output", "virtual-output-a"));
        before = calls;
        selected.RequestRefresh();
        await Wait(() => calls == before + 1);
        Check(AudioEndpointVolume.Subscribers == 6,
            "inactive selected output and wrong-direction capture do not add subscriptions");
        Volatile.Write(ref selection, new("absent-output", "absent-capture"));
        before = calls;
        selected.RequestRefresh();
        await Wait(() => calls == before + 1);
        Check(AudioEndpointVolume.Subscribers == 6,
            "missing saved endpoints do not terminate default endpoint monitoring");
        failProvider = true;
        before = calls;
        selected.RequestRefresh();
        await Wait(() => calls == before + 1);
        Check(AudioEndpointVolume.Subscribers == 6,
            "settings provider failure is contained and defaults remain observed");

        selected.Dispose();
        await Wait(() => MMDeviceEnumerator.Unregistered == 3 && AudioEndpointVolume.Subscribers == 0);
        before = calls;
        selected.RequestRefresh();
        await Task.Delay(900);
        Check(calls == before, "refresh after disposal is ignored");

        int disposedBeforeFailure = MMDeviceEnumerator.Disposed;
        MMDeviceEnumerator.FailRegister = true;
        using var failed = new AudioConfigurationMonitor(() => Interlocked.Increment(ref calls));
        failed.Start();
        await Wait(() => calls == before + 1 && MMDeviceEnumerator.Disposed == disposedBeforeFailure + 1);
        Check(MMDeviceEnumerator.Disposed == disposedBeforeFailure + 1,
            "registration failure is contained and releases enumerator");
        Console.WriteLine($"Audio configuration monitor lifecycle checks: {_passed} passed.");
    }
}
