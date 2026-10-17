using Liuzhuan.Services.Audio;
using System.Text.Json;

internal static class FollowChecks
{
    public static void Run(Action<bool, string> check)
    {
        var idle = new CaptureObservation(1, true, Array.Empty<string>(), 0, 0, "");
        var active = idle with { ActiveOwners = new[] { "verified-process-incarnation-A" } };
        var phone = new MicSnapshot("", Ready: true, ConnectionId: 1);
        var policy = new FollowDemandPolicy();
        bool blocked = false; try { policy.Enable(phone); } catch (InvalidOperationException) { blocked = true; }
        check(blocked && !policy.Enabled, "automatic mode requires an observed calibration cycle");
        policy.BeginCalibration(); policy.Observe(active, 0); policy.Observe(active, 1000);
        check(!policy.Calibrated, "a permanently active session cannot pass idle-active-idle calibration");
        policy.Observe(idle, 1200); policy.Observe(idle, 1700); policy.Observe(active, 1800);
        policy.Observe(idle, 2000); policy.Observe(idle, 2400);
        check(policy.Calibrated && !policy.Enabled, "calibration recognizes the cycle without starting forwarding");
        policy.Enable(phone);
        check(policy.Evaluate(active, phone, 2500, true) == true, "verified demand starts the currently authorized phone");
        phone = phone with { Requested = true };
        check(policy.Evaluate(active, phone, 2600, true) == null, "duplicate active observations do not generate repeated starts");
        check(policy.Evaluate(idle, phone, 2700, true) == null && policy.Evaluate(idle, phone, 3099, true) == null &&
            policy.Evaluate(idle, phone, 3100, true) == false, "last inactive session stops after 400ms grace");
        check(policy.Evaluate(active, phone, 3200, true) == null, "returning activity cancels idle grace");
        check(policy.Evaluate(active, phone with { StopRevision = 1 }, 3300, true) == false && !policy.Enabled,
            "phone pause revision overrides demand even if readiness already recovered");
        phone = phone with { Requested = false }; policy.Enable(phone);
        check(policy.Evaluate(active, phone with { ConnectionId = 2 }, 3400, true) == false && !policy.Enabled,
            "a replacement connection never inherits automatic forwarding");
        policy.Enable(phone);
        check(policy.Evaluate(active with { ActiveOwners = new[] { "reused-PID-new-incarnation" } }, phone, 3500, true) == false,
            "a different process incarnation is not a calibrated owner");
        policy.Enable(phone);
        check(policy.Evaluate(active, phone, 3600, false) == false && !policy.Enabled, "stale observation stops automatic forwarding");
        policy.Enable(phone); policy.Observe(active with { UnknownActive = 1 }, 3700);
        check(!policy.Enabled, "ambiguous ownership cannot enable or sustain automatic forwarding");
        policy.Reset(); check(!policy.Calibrated && !policy.Enabled, "observer restart invalidates calibration and automatic permission");
        var path = Path.Combine(Path.GetTempPath(), "lz-mic-prefs-" + Guid.NewGuid().ToString("N"));
        try
        {
            new MicrophonePreferences("output-exact-id", "capture-exact-id").Save(path);
            check(MicrophonePreferences.Load(path) == new MicrophonePreferences("output-exact-id", "capture-exact-id"), "only exact endpoint preferences survive restart");
            File.WriteAllText(Path.Combine(path, "microphone.json"), "{corrupt");
            check(MicrophonePreferences.Load(path) == new MicrophonePreferences(), "corrupt audio preferences do not crash startup");
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
        using var combined = JsonDocument.Parse(CombinedPairing.Create("192.168.1.2", 8899, "test-only",
            "{\"kind\":\"liuzhuan-mic\",\"v\":1,\"ip\":\"192.168.1.2\",\"port\":8902,\"token\":\"test-only\"}"));
        check(combined.RootElement.GetProperty("pwd").GetString() == "test-only" && combined.RootElement.GetProperty("port").GetInt32() == 8899 &&
            combined.RootElement.GetProperty("mic").GetProperty("port").GetInt32() == 8902, "combined QR preserves legacy material fields and independent audio invitation");
        using var legacy = JsonDocument.Parse(CombinedPairing.Create("192.168.1.2", 8899, "test-only", null));
        check(!legacy.RootElement.TryGetProperty("mic", out _), "disabled audio service does not advertise a dead microphone invitation");
    }
}
