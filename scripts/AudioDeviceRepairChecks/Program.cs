using System.Text.Json;
using Liuzhuan.Services.Audio;
using NAudio.CoreAudioApi;

internal static class Program
{
    private static int _checks;
    private static void Check(bool value, string name)
    { if (!value) throw new InvalidOperationException(name); _checks++; Console.WriteLine("PASS " + name); }
    private static AudioDeviceRepairEndpoint Output(string id = "out1", string parent = "ROOT\\MEDIA\\0001") =>
        new(id, "CABLE Input (VB-Audio Virtual Cable)", DataFlow.Render, true, "CABLE Input", "controller:" + parent, parent, "VBAudioVACWDM");
    private static AudioDeviceRepairEndpoint Capture(string id = "in1", string parent = "ROOT\\MEDIA\\0001") =>
        new(id, "CABLE Output (VB-Audio Virtual Cable)", DataFlow.Capture, true, "CABLE Output", "controller:" + parent, parent, "VBAudioVACWDM");
    private static AudioDeviceRepairSuggestion Suggest(params AudioDeviceRepairEndpoint[] endpoints) => AudioDeviceRepairService.Suggest(new("stale-out", "stale-in"), new(endpoints));
    public static void Main(string[] args)
    {
        if (args.SequenceEqual(["--scan"]))
        {
            // Explicit optional read-only inventory; never loads or changes user preferences.
            var inventory = AudioDeviceRepairService.Scan();
            Console.WriteLine(JsonSerializer.Serialize(new { Inventory = inventory, Suggestion = AudioDeviceRepairService.Suggest(new(), inventory) },
                new JsonSerializerOptions { WriteIndented = true })); return;
        }
        var output = Output(); var capture = Capture();
        var standard = Suggest(output, capture);
        Check(standard.Status == "repair_available" && standard.Preferences == new MicrophonePreferences(output.Id, capture.Id), "standard CABLE pair uses exact driver and common PnP parent metadata");
        var generic = output with { Name = "扬声器 (2- VB-Audio Virtual Cable)", Description = "扬声器" };
        Check(Suggest(generic, capture).Status == "repair_available", "generic speaker label is accepted only with verified VB-CABLE metadata");
        Check(Suggest(generic with { MatchingDeviceId = "HDAUDIO\\REALTEK" }, capture).Status == "setup_required", "real speakers cannot be selected by a matching display label");
        Check(Suggest(output with { MatchingDeviceId = "UNKNOWN" }, capture).Status == "setup_required", "CABLE name alone cannot establish driver identity");
        Check(Suggest(output with { MatchingDeviceId = "FAKE-VBAudioVACWDM-EXTRA" }, capture).Status == "setup_required", "driver matching is exact rather than substring based");
        Check(Suggest(output, Capture(parent: "ROOT\\MEDIA\\0002")).Status == "setup_required", "separate installed driver instances never cross-pair");
        Check(Suggest(output with { ControllerDeviceId = "other-controller" }, capture).Status == "setup_required", "same parent with conflicting controller metadata is rejected");
        Check(Suggest(output with { ParentInstanceId = "" }, capture).Status == "setup_required", "empty PnP parent never supplies a binding");
        Check(Suggest(output with { ControllerDeviceId = "" }, capture).Status == "setup_required", "empty controller identity never supplies a binding");
        Check(Suggest(output with { Name = "CABLE In 16 Ch (VB-Audio Virtual Cable)", Description = "CABLE In 16 Ch" }, capture).Status == "setup_required", "16 Ch rendering endpoint is excluded");
        Check(Suggest(output with { Description = "CABLE In 16ch" }, capture).Status == "setup_required", "16ch exclusion uses endpoint description even after display rename");
        Check(Suggest(output with { Description = "CABLE In 16 channels" }, capture).Status == "setup_required", "16 channels spelling is excluded");
        Check(Suggest(output, capture with { Description = "Other", Name = "Microphone" }).Status == "setup_required", "capture role must remain recognizable as CABLE Output");
        Check(Suggest(output with { Active = false }, capture).Status == "setup_required", "inactive rendering endpoints are never selected");
        Check(Suggest(output, capture with { Active = false }).Status == "setup_required", "inactive recording endpoints are never selected");
        Check(Suggest(output, capture, Output("out2")).Status == "ambiguous", "multiple stereo render candidates require manual confirmation");
        Check(Suggest(output, capture, Output("out2", "ROOT\\MEDIA\\0002"), Capture("in2", "ROOT\\MEDIA\\0002")).Status == "ambiguous", "two complete driver installations never cause arbitrary first-match repair");
        var current = AudioDeviceRepairService.Suggest(new(output.Id, capture.Id), new([output, capture, Output("out2", "ROOT\\MEDIA\\0002"), Capture("in2", "ROOT\\MEDIA\\0002")]));
        Check(current.Status == "current" && current.Preferences == new MicrophonePreferences(output.Id, capture.Id), "healthy exact saved pair takes precedence without changing to a different installation");
        Check(AudioDeviceRepairService.Suggest(new(), new([output, capture], false)).Status == "unavailable", "incomplete enumeration cannot produce automatic repair");
        Check(Suggest(output with { MetadataReadable = false }, capture).Status == "unavailable", "partial active endpoint metadata is unknown rather than a safe candidate");
        Check(Suggest(output, capture, output).Status == "unavailable", "duplicate endpoint identity forces a new scan");
        Check(Suggest().Status == "setup_required", "empty complete inventory asks for setup");
        Check(Suggest(output).Status == "setup_required", "one-sided installation is not a repairable pair");
        Check(Suggest(output with { MatchingDeviceId = "vbaudiovacwdm", ParentInstanceId = "root\\media\\0001", ControllerDeviceId = "CONTROLLER:ROOT\\MEDIA\\0001" }, capture).Status == "repair_available", "PnP IDs and driver matching use case-insensitive Windows identity comparison");
        Check(Suggest(output, capture, output with { Id = "inactive", Active = false, MetadataReadable = false }).Status == "repair_available", "inactive historical endpoints with missing metadata do not block an active exact pair");
        var saved = new MicrophonePreferences(output.Id, "stale-in");
        Check(AudioDeviceRepairService.Suggest(saved, new([output, capture])).Status == "repair_available", "partially stale saved pair suggests one fully verified replacement pair");
        Check(standard.Message.Contains("不改变电脑外放") && !standard.Message.Contains("成功"), "repair guidance describes settings scope without claiming audio success");
        Console.WriteLine($"Audio device repair checks passed: {_checks}; synthetic metadata only, no audio operations.");
    }
}
