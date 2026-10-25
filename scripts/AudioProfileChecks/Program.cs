using System.Security.Cryptography;
using System.Text;
using Liuzhuan.Services.Audio;
using NAudio.CoreAudioApi;

static class Program
{
    private static int _checks;
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 5, 0, 0, TimeSpan.Zero);
    private static readonly MicrophonePreferences Pair = new("render-cable", "capture-cable");
    private static readonly AudioRouteEndpoint Speaker = new("render-speaker", "Speakers (fixture hardware)", DataFlow.Render, true, false, .6f);
    private static readonly AudioRouteEndpoint Output = new(Pair.OutputId, "CABLE Input (fixture virtual)", DataFlow.Render, true, false, 1f);
    private static readonly AudioRouteEndpoint Capture = new(Pair.CaptureId, "CABLE Output (fixture virtual)", DataFlow.Capture, true, false, 1f);
    private static readonly AudioRouteEndpoint Mic = new("capture-physical", "Microphone (fixture hardware)", DataFlow.Capture, true, false, .8f);
    private static AudioRouteInventory Inventory() => new(new[] { Speaker, Output, Capture, Mic },
        new[] { Role.Console, Role.Multimedia, Role.Communications }.SelectMany(role => new[]
        { new AudioDefaultRoute(DataFlow.Render, role, Speaker), new AudioDefaultRoute(DataFlow.Capture, role, Capture) }).ToArray());
    private static AudioSignalEvidence Proof() => new(Pair.OutputId, Pair.CaptureId, 4, 20, 38400, Now, SignalPeak: .25f);
    private static void Check(bool value, string name)
    { if (!value) throw new InvalidOperationException("FAIL " + name); _checks++; Console.WriteLine("PASS " + name); }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static AudioRouteInventory Replace(AudioRouteInventory input, AudioRouteEndpoint endpoint) => input with
    {
        Endpoints = input.Endpoints.Select(x => x.Id == endpoint.Id ? endpoint : x).ToArray(),
        Defaults = input.Defaults.Select(x => x.Endpoint?.Id == endpoint.Id ? x with { Endpoint = endpoint } : x).ToArray()
    };
    private static bool HasWarning(AudioProfileAssessment assessment, string prefix) => assessment.Notices.Any(x => x.Code.StartsWith(prefix) && x.Severity == AudioRouteSeverity.Warning);

    public static int Main(string[] args)
    {
        string parent = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "liuzhuan-audio-profile-checks");
        string root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "main"); var guard = new AudioConfigurationGuard(path, () => Now); var current = Inventory();
        Check(!guard.Evaluate(current, Pair).HasConfirmedProfile && !Directory.Exists(path), "inspection is read-only and creates no profile or preferences");
        Check(!guard.PrepareRestore(current).CanRestore && !Directory.Exists(path), "restore without baseline refuses and does not create files");
        foreach (var bad in new[] { Proof() with { Frames = 0 }, Proof() with { OutputPcmBytes = 0 }, Proof() with { Receiving = false },
            Proof() with { Requested = false }, Proof() with { ConnectionId = 0 }, Proof() with { DiagnosticOnly = true },
            Proof() with { HasFailure = true }, Proof() with { OutputId = "other-output" }, Proof() with { CaptureId = "other-input" },
            Proof() with { ObservedAtUtc = Now.AddSeconds(-31) }, Proof() with { ObservedAtUtc = Now.AddSeconds(1) },
            Proof() with { SignalPeak = 0 }, Proof() with { SignalPeak = -.5f }, Proof() with { SignalPeak = 1.1f },
            Proof() with { SignalPeak = float.NaN }, Proof() with { SignalPeak = float.PositiveInfinity } })
            Check(!guard.Observe(current, Pair, bad).Success && !Directory.Exists(path), "unproven, stale or mismatched stream cannot establish route " + _checks);
        var automatic = guard.Observe(current, Pair, Proof());
        Check(automatic.Success && automatic.Changed && File.Exists(Path.Combine(path, AudioConfigurationGuard.ObservedFileName)), "fresh exact stream evidence saves an observed route");
        Check(!File.Exists(Path.Combine(path, AudioConfigurationGuard.ConfirmedFileName)) && !File.Exists(Path.Combine(path, "microphone.json")), "automatic observation does not confirm loudspeaker/ASR or change preferences");
        var routeHash = Hash(Path.Combine(path, AudioConfigurationGuard.ObservedFileName));
        Check(!guard.Observe(current, Pair, Proof()).Changed && Hash(Path.Combine(path, AudioConfigurationGuard.ObservedFileName)) == routeHash,
            "repeated same-pair observation does not rewrite disk");
        Check(guard.PrepareRestore(current) is { CanRestore: true, Source: "observed", Preferences: var observedPair } && observedPair == Pair,
            "explicit fallback returns exact observed pair with lower-trust source");
        Check(!guard.ConfirmGood(current, Pair, Proof(), false).Success && !File.Exists(Path.Combine(path, AudioConfigurationGuard.ConfirmedFileName)),
            "automatic success cannot replace explicit user confirmation");
        var confirmation = guard.ConfirmGood(current, Pair, Proof(), true);
        Check(confirmation.Success && confirmation.Changed, "explicitly confirmed healthy playback and live stream establishes full baseline");
        string goodFile = Path.Combine(path, AudioConfigurationGuard.ConfirmedFileName); string goodHash = Hash(goodFile);
        string text = File.ReadAllText(goodFile);
        Check(text.Contains("render-speaker") && text.Contains("Volume") && !text.Contains("Invitation") && !text.Contains("Automatic") && !text.Contains("Permission"),
            "baseline records defaults/volume but never consent, follow state or invitations");
        var healthy = guard.Evaluate(current, Pair);
        Check(healthy.HasConfirmedProfile && healthy.HasObservedRoute && healthy.CanRestore && healthy.ConfirmedAtUtc == Now && !healthy.Notices.Any(x => x.Severity == AudioRouteSeverity.Warning),
            "baseline survives readback and current healthy routes have no warning");
        var restarted = new AudioConfigurationGuard(path, () => Now.AddDays(20));
        Check(restarted.PrepareRestore(current) is { Source: "confirmed", Preferences: var restartedPair } && restartedPair == Pair,
            "confirmed baseline survives process restart and is not an expiring invitation");

        var headphones = new AudioRouteEndpoint("render-headphones", "Headphones (fixture hardware)", DataFlow.Render, true, false, .5f);
        var headphoneRoute = current with { Endpoints = current.Endpoints.Append(headphones).ToArray(),
            Defaults = current.Defaults.Select(x => x.Flow == DataFlow.Render ? x with { Endpoint = headphones } : x).ToArray() };
        var headphoneAssessment = guard.Evaluate(headphoneRoute, Pair);
        Check(headphoneAssessment.Notices.Count(x => x.Code.StartsWith("profile-default-changed") && x.Severity == AudioRouteSeverity.Information) == 3 &&
            !headphoneAssessment.Notices.Any(x => x.Severity == AudioRouteSeverity.Warning), "normal physical headphones switch is informational, not configuration failure");
        var disabledDefaultWithOtherHardware = Replace(current, Speaker with { Active = false }) with { Endpoints =
            Replace(current, Speaker with { Active = false }).Endpoints.Append(headphones).ToArray() };
        Check(HasWarning(guard.Evaluate(disabledDefaultWithOtherHardware, Pair), "profile-playback-unavailable-"),
            "disabled default playback warns even with another available physical endpoint");
        var otherCapture = current with { Defaults = current.Defaults.Select(x => x.Flow == DataFlow.Capture ? x with { Endpoint = Mic } : x).ToArray() };
        Check(!guard.Evaluate(otherCapture, Pair).Notices.Any(x => x.Severity == AudioRouteSeverity.Warning),
            "default recording changes do not reject applications with independently selected inputs");
        var virtualPlayback = current with { Defaults = current.Defaults.Select(x => x.Flow == DataFlow.Render ? x with { Endpoint = Output } : x).ToArray() };
        Check(HasWarning(guard.Evaluate(virtualPlayback, Pair), "profile-playback-virtual-"), "virtual default playback warns about external sound routing");
        Check(!guard.ConfirmGood(virtualPlayback, Pair, Proof(), true).Success && Hash(goodFile) == goodHash,
            "suspicious virtual playback cannot overwrite confirmed healthy baseline");
        var mutedSpeaker = Replace(current, Speaker with { Muted = true });
        Check(HasWarning(guard.Evaluate(mutedSpeaker, Pair), "profile-playback-muted-") && !guard.ConfirmGood(mutedSpeaker, Pair, Proof(), true).Success && Hash(goodFile) == goodHash,
            "muted physical playback warns and cannot replace a good baseline");
        var zeroSpeaker = Replace(current, Speaker with { Volume = 0 });
        Check(HasWarning(guard.Evaluate(zeroSpeaker, Pair), "profile-playback-muted-") && !guard.ConfirmGood(zeroSpeaker, Pair, Proof(), true).Success,
            "zero physical volume is a warning and blocks claiming a full healthy baseline");
        Check(HasWarning(guard.Evaluate(Replace(current, Speaker with { Volume = .01f }), Pair), "profile-playback-volume-low"),
            "large physical-volume drop is explained without changing system volume");
        var headphonesOnly = new AudioRouteEndpoint("headphones-active", "Headphones (fixture)", DataFlow.Render, true, false, .6f);
        var switchedAndQuiet = Replace(current, Speaker with { Volume = .01f });
        switchedAndQuiet = switchedAndQuiet with { Endpoints = switchedAndQuiet.Endpoints.Append(headphonesOnly).ToArray(),
            Defaults = switchedAndQuiet.Defaults.Select(x => x.Flow == DataFlow.Render ? x with { Endpoint = headphonesOnly } : x).ToArray() };
        Check(!guard.Evaluate(switchedAndQuiet, Pair).Notices.Any(x => x.Severity == AudioRouteSeverity.Warning),
            "a quiet old speaker does not warn after switching all playback to healthy physical headphones");
        var unknownVolume = Replace(current, Speaker with { Volume = null });
        Check(!guard.ConfirmGood(unknownVolume, Pair, Proof(), true).Success && Hash(goodFile) == goodHash, "unreadable volume is unknown and cannot prove healthy playback");
        var noPhysical = current with { Endpoints = current.Endpoints.Where(x => x.Id != Speaker.Id).ToArray() };
        Check(HasWarning(guard.Evaluate(noPhysical, Pair), "profile-physical-playback-missing"), "complete inventory with no physical playback reports the hardware absence");
        Check(!HasWarning(guard.Evaluate(noPhysical with { RenderReadable = false }, Pair), "profile-physical-playback-missing"),
            "incomplete inventory never claims hardware is absent");

        var mutedOutput = Replace(current, Output with { Muted = true });
        Check(HasWarning(guard.Evaluate(mutedOutput, Pair), "profile-selected-output-muted") && !guard.Observe(mutedOutput, Pair, Proof()).Success,
            "muted virtual output is not accepted even when PCM-read counters exist");
        var disabledOutput = Replace(current, Output with { Active = false });
        Check(!guard.PrepareRestore(disabledOutput).CanRestore && HasWarning(guard.Evaluate(disabledOutput, Pair), "profile-selected-output-unavailable"),
            "disabled endpoint blocks exact restoration and warns");
        var reinstalled = current with { Endpoints = current.Endpoints.Select(x => x.Id == Output.Id ? x with { Id = "new-render-id" } : x).ToArray() };
        Check(!guard.PrepareRestore(reinstalled).CanRestore, "same display name with new endpoint ID is never a replacement");
        Check(!guard.PrepareRestore(current with { CaptureReadable = false }).CanRestore, "incomplete capture inventory blocks restore instead of guessing");
        Check(!guard.Observe(current, new(null!, null!), Proof()).Success && guard.Evaluate(current, new(null!, null!)).HasConfirmedProfile,
            "malformed preference fields are rejected for saving and remain safe to inspect");
        Check(HasWarning(guard.Evaluate(current, Pair with { OutputId = "new-choice" }), "profile-selected-route-changed"),
            "changed selected pair offers prior known-good choices without overwriting them");
        var prepared = guard.PrepareRestore(current);
        Check(prepared.CanRestore && prepared.Preferences == Pair && !File.Exists(Path.Combine(path, "microphone.json")) && Hash(goodFile) == goodHash,
            "restore preparation is side-effect free and never writes defaults, volume, preference or capture state");
        Check(guard.Evaluate(current, Pair, true).Notices.Any(x => x.Code == "profile-diagnostic-only" && x.Severity == AudioRouteSeverity.Information),
            "diagnostic mode is explained without silently enabling the stream");
        Check(!Directory.EnumerateFiles(path, "*.tmp").Any(), "atomic saves leave no temporary files");

        foreach (string broken in new[] { "null", "{}", "{not-json}", text.Replace("\"Version\":1", "\"Version\":2"),
            text.Replace("\"Version\":1", "\"Version\":1,\"Version\":1"), text.Replace("\"Kind\":\"confirmed\"", "\"Kind\":\"observed\""),
            text.Replace("\"Defaults\":[", "\"Defaults\":[null,"), text.Replace("\"Playback\":[", "\"Playback\":[null,"),
            text.Insert(1, "\"UnexpectedPermission\":true,"), new string('x', 32769) })
        {
            string brokenPath = Path.Combine(root, "broken-" + _checks); Directory.CreateDirectory(brokenPath);
            string brokenFile = Path.Combine(brokenPath, AudioConfigurationGuard.ConfirmedFileName); File.WriteAllText(brokenFile, broken);
            var brokenGuard = new AudioConfigurationGuard(brokenPath, () => Now); string brokenHash = Hash(brokenFile);
            // A valid observed route cannot hide an unreadable stronger baseline.
            Check(brokenGuard.Observe(current, Pair, Proof()).Success && !brokenGuard.PrepareRestore(current).CanRestore &&
                !brokenGuard.ConfirmGood(current, Pair, Proof(), true).Success && Hash(brokenFile) == brokenHash &&
                HasWarning(brokenGuard.Evaluate(current, Pair), "confirmed-profile-unreadable"),
                "bad or unknown profile is preserved and cannot silently downgrade or overwrite " + _checks);
        }
        string observedOnly = Path.Combine(root, "broken-observed"); Directory.CreateDirectory(observedOnly);
        string observedFile = Path.Combine(observedOnly, AudioConfigurationGuard.ObservedFileName); File.WriteAllText(observedFile, "broken");
        string observedHash = Hash(observedFile); var observedGuard = new AudioConfigurationGuard(observedOnly, () => Now);
        Check(!observedGuard.Observe(current, Pair, Proof()).Success && !observedGuard.PrepareRestore(current).CanRestore && Hash(observedFile) == observedHash,
            "bad observed file is preserved rather than silently reset");
        string blockedPath = Path.Combine(root, "file-not-directory"); File.WriteAllText(blockedPath, "fixture sentinel");
        var blocked = new AudioConfigurationGuard(blockedPath, () => Now);
        Check(!blocked.Observe(current, Pair, Proof()).Success && File.ReadAllText(blockedPath) == "fixture sentinel", "failed disk save never claims success or alters unrelated content");
        Console.WriteLine($"PASS {_checks} audio configuration protection checks. Synthetic evidence only; no audio devices activated, no system defaults changed. Evidence: {root}");
        return 0;
    }
}
