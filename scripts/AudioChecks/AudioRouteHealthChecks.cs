using Liuzhuan.Services.Audio;
using NAudio.CoreAudioApi;

internal static class AudioRouteHealthChecks
{
    public static void Run(Action<bool, string> check)
    {
        var speaker = new AudioRouteEndpoint("speaker-private-id", "Fixture private speakers", DataFlow.Render, Muted: false, Volume: .5f);
        var output = new AudioRouteEndpoint("cable-private-output", "CABLE Input (VB-Audio Virtual Cable)", DataFlow.Render, Muted: false, Volume: .5f);
        var capture = new AudioRouteEndpoint("cable-private-capture", "CABLE Output (VB-Audio Virtual Cable)", DataFlow.Capture, Muted: false, Volume: .5f);
        var mic = new AudioRouteEndpoint("mic-private-id", "Fixture private microphone", DataFlow.Capture);
        var defaults = new[] { Role.Console, Role.Multimedia, Role.Communications }
            .SelectMany(role => new[] { new AudioDefaultRoute(DataFlow.Render, role, speaker), new AudioDefaultRoute(DataFlow.Capture, role, capture) }).ToArray();
        var inventory = new AudioRouteInventory(new[] { speaker, output, capture, mic }, defaults);
        AudioRouteReport Review(AudioRouteInventory value, string? o = null, string? c = null, bool diagnostic = false) =>
            AudioRouteHealth.Review(value, o ?? output.Id, c ?? capture.Id, diagnostic);
        var good = Review(inventory);
        check(good.Findings.Length == 0 && good.Summary.Contains("实际听写"), "correct separate playback and virtual route never claims ASR success");
        check(good.Playback.Contains(speaker.Name) && good.Output.Contains(output.Name) && good.Recording.Contains(capture.Name), "three direction cards show their own endpoint roles");
        var communication = defaults.Select(x => x.Flow == DataFlow.Render && x.Role == Role.Communications ? x with { Endpoint = output } : x).ToArray();
        var commReport = Review(inventory with { Defaults = communication });
        check(commReport.Findings.Count(x => x.Code.StartsWith("playback-virtual")) == 1 &&
            commReport.Findings.Any(x => x.Code.EndsWith("Communications") && x.Title.Contains("通话")), "communications-only virtual playback is detected even when normal speakers are correct");
        var media = defaults.Select(x => x.Flow == DataFlow.Render && x.Role == Role.Multimedia ? x with { Endpoint = output } : x).ToArray();
        check(Review(inventory with { Defaults = media }).Findings.Any(x => x.Code == "playback-virtual-Multimedia"), "media default is checked independently of daily playback");
        var allVirtual = defaults.Select(x => x.Flow == DataFlow.Render ? x with { Endpoint = output } : x).ToArray();
        check(Review(inventory with { Defaults = allVirtual }).Findings.Count(x => x.Code.StartsWith("playback-virtual")) == 3, "all three Windows playback defaults are inspected");
        var missing = Review(inventory, "previous-driver-id");
        check(missing.Findings.Any(x => x.Code == "output-unavailable") && missing.Output.Contains("已保存"), "missing saved endpoint remains missing instead of falling back to speakers or another CABLE ID");
        var inactive = inventory.Endpoints.Select(x => x.Id == output.Id ? x with { Active = false } : x).ToArray();
        check(Review(inventory with { Endpoints = inactive }).Findings.Any(x => x.Code == "output-unavailable"), "disabled or unplugged saved endpoint is treated as unavailable");
        var noDriver = inventory with { Endpoints = new[] { speaker, mic } };
        var unselected = Review(noDriver, "", "");
        check(unselected.Findings.Count(x => x.Severity == AudioRouteSeverity.Error) == 2 && unselected.Findings.Any(x => x.Code == "cable-not-found"), "absent driver gives setup guidance without installing or changing anything");
        var unknown = Review(noDriver with { RenderReadable = false, CaptureReadable = false }, "", "");
        check(unknown.Findings.Any(x => x.Code == "inventory-unavailable") && !unknown.Findings.Any(x => x.Code == "cable-not-found"), "COM inventory failure is unknown rather than falsely reporting an uninstalled driver");
        var defaultsError = defaults.Select(x => x.Flow == DataFlow.Render && x.Role == Role.Console ? x with { Endpoint = null, Readable = false } : x).ToArray();
        check(Review(inventory with { Defaults = defaultsError }).Findings.Any(x => x.Code == "defaults-unavailable") &&
            !Review(inventory with { Defaults = defaultsError }).Findings.Any(x => x.Code == "playback-missing-Console"), "default read failures are distinguished from an actually missing default device");
        var noDefault = defaults.Select(x => x.Flow == DataFlow.Render && x.Role == Role.Console ? x with { Endpoint = null } : x).ToArray();
        check(Review(inventory with { Defaults = noDefault }).Findings.Any(x => x.Code == "playback-missing-Console"), "a known absence of default playback requires action");
        var direct = Review(inventory, speaker.Id, mic.Id);
        check(direct.Findings.Any(x => x.Code == "output-unverified") && direct.Findings.Any(x => x.Code == "capture-unverified") &&
            !direct.Findings.Any(x => x.Code.StartsWith("playback-virtual")), "selecting physical endpoints warns about wrong direction without misclassifying physical system playback");
        var sixteen = new AudioRouteEndpoint("16ch", "CABLE In 16ch (VB-Audio Virtual Cable)", DataFlow.Render);
        var sixteenReport = Review(inventory with { Endpoints = inventory.Endpoints.Append(sixteen).ToArray() }, sixteen.Id);
        check(sixteenReport.Findings.Any(x => x.Code == "cable-16ch") && sixteenReport.Findings.Any(x => x.Code == "pair-mismatch"), "16-channel output is not silently accepted as standard CABLE Input");
        check(!AudioRouteHealth.StandardOutput(sixteen.Name) && AudioRouteHealth.StandardOutput(output.Name), "first-time recommendation selects only the standard CABLE output");
        var otherMicDefault = defaults.Select(x => x.Flow == DataFlow.Capture ? x with { Endpoint = mic } : x).ToArray();
        var otherMicReport = Review(inventory with { Defaults = otherMicDefault });
        check(otherMicReport.Findings.Any(x => x.Code == "capture-default-different" && x.Severity == AudioRouteSeverity.Information && x.Detail.Contains("独立")), "per-app input remains valid when system defaults are different");
        var diagnostic = Review(noDriver, "", "", true);
        check(diagnostic.Findings.Any(x => x.Code == "diagnostic-only") && !diagnostic.Findings.Any(x => x.Severity == AudioRouteSeverity.Error), "diagnostic mode needs no virtual driver and is never presented as dictation output");
        check(!commReport.SanitizedText.Contains("private") && !commReport.SanitizedText.Contains(output.Name) &&
            !good.SanitizedText.Contains(speaker.Name), "copyable route diagnostics omit endpoint IDs and friendly device names");
        var silentDefaults = defaults.Select(x => x.Flow == DataFlow.Render ? x with { Endpoint = speaker with { Muted = true } } : x).ToArray();
        check(Review(inventory with { Defaults = silentDefaults }).Findings.Any(x => x.Code == "playback-Console-muted"),
            "physical default playback mute is visible without writing Windows settings");
        var zeroDefaults = defaults.Select(x => x.Flow == DataFlow.Render ? x with { Endpoint = speaker with { Volume = 0 } } : x).ToArray();
        check(Review(inventory with { Defaults = zeroDefaults }).Findings.Any(x => x.Code == "playback-Multimedia-silent"),
            "physical media playback at zero volume is detected separately from routing");
        var unknownLevels = defaults.Select(x => x.Flow == DataFlow.Render ? x with { Endpoint = speaker with { Muted = null, Volume = null } } : x).ToArray();
        check(Review(inventory with { Defaults = unknownLevels }).Findings.Any(x => x.Code == "playback-Console-level-unknown") &&
            !Review(inventory with { Defaults = unknownLevels }).Findings.Any(x => x.Code.EndsWith("-muted") || x.Code.EndsWith("-silent")),
            "unreadable endpoint levels stay unknown rather than silently healthy or muted");
        var noPhysical = inventory with { Endpoints = new[] { output, capture } };
        check(Review(noPhysical).Findings.Any(x => x.Code == "physical-playback-missing") &&
            !Review(noPhysical with { RenderReadable = false }).Findings.Any(x => x.Code == "physical-playback-missing"),
            "missing physical audio hardware is distinguished from incomplete enumeration");
        var mutedInput = inventory.Endpoints.Select(x => x.Id == capture.Id ? x with { Muted = true } : x).ToArray();
        check(Review(inventory with { Endpoints = mutedInput }).Findings.Any(x => x.Code == "capture-muted"),
            "muted virtual capture can explain failed dictation even with correct external playback");
        check(AudioSettingsCommand.For(AudioSettingsPage.Playback) == new AudioSettingsCommand("control.exe", "mmsys.cpl,,0") &&
            AudioSettingsCommand.For(AudioSettingsPage.Recording) == new AudioSettingsCommand("control.exe", "mmsys.cpl,,1") &&
            AudioSettingsCommand.For(AudioSettingsPage.Sound).FileName == "ms-settings:sound", "repair buttons resolve only fixed Windows sound pages and never an executable from device data");
    }
}
