using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NAudio.CoreAudioApi;

namespace Liuzhuan.Services.Audio;

/// <summary>Evidence for one current stream; counters alone are not an ASR or loudspeaker test.</summary>
public sealed record AudioSignalEvidence(string OutputId, string CaptureId, long ConnectionId, long Frames,
    long OutputPcmBytes, DateTimeOffset ObservedAtUtc, bool Receiving = true, bool Requested = true,
    bool DiagnosticOnly = false, bool HasFailure = false, float SignalPeak = 0);
public sealed record AudioProfileOperation(bool Success, bool Changed, string Message);
public sealed record AudioProfileNotice(string Code, AudioRouteSeverity Severity, string Title, string Detail);
public sealed record AudioProfileAssessment(bool HasConfirmedProfile, bool HasObservedRoute, bool CanRestore,
    string Summary, AudioProfileNotice[] Notices, DateTimeOffset? ConfirmedAtUtc);
public sealed record AudioProfileRestore(MicrophonePreferences? Preferences, string Source, string Message)
{
    public bool CanRestore => Preferences != null;
}

/// <summary>
/// Two local files, no timers or audio: an observed virtual route and a separately user-confirmed configuration.
/// Never changes Windows defaults/volume, restores capture consent, or starts a stream. Do not migrate these files.
/// </summary>
public sealed class AudioConfigurationGuard
{
    public const string ConfirmedFileName = "audio-known-good-v1.json";
    public const string ObservedFileName = "audio-route-verified-v1.json";
    private const int MaximumBytes = 32768;
    private static readonly object DiskGate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };
    private readonly string _directory;
    private readonly Func<DateTimeOffset> _clock;

    private sealed record SavedDefault([property: JsonRequired] DataFlow Flow, [property: JsonRequired] Role Role,
        [property: JsonRequired] string Id, [property: JsonRequired] string Name);
    private sealed record SavedPlayback([property: JsonRequired] string Id, [property: JsonRequired] string Name,
        [property: JsonRequired] bool Muted, [property: JsonRequired] float Volume);
    private sealed record Profile([property: JsonRequired] int Version, [property: JsonRequired] string Kind,
        [property: JsonRequired] DateTimeOffset SavedAtUtc, [property: JsonRequired] MicrophonePreferences Preferences,
        [property: JsonRequired] SavedDefault[] Defaults, [property: JsonRequired] SavedPlayback[] Playback);
    private sealed record Loaded(Profile? Profile, string? Error);

    public AudioConfigurationGuard(string directory, Func<DateTimeOffset>? clock = null)
    {
        _directory = Path.GetFullPath(directory);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Call only with the effective running endpoints and fresh non-silent PCM/output-read evidence.</summary>
    public AudioProfileOperation Observe(AudioRouteInventory inventory, MicrophonePreferences preferences, AudioSignalEvidence evidence)
    {
        lock (DiskGate)
        {
            var error = ValidateEvidence(inventory, preferences, evidence);
            if (error != null) return new(false, false, error);
            var observed = Load(ObservedFileName, "observed");
            if (observed.Error != null) return new(false, false, observed.Error);
            if (observed.Profile?.Preferences == preferences)
                return new(true, false, "已保留这组验证过音频读取的流转设备；尚不代表外放或听写已确认。");
            var profile = new Profile(1, "observed", _clock(), preferences, Array.Empty<SavedDefault>(), Array.Empty<SavedPlayback>());
            var result = Save(ObservedFileName, "observed", profile,
                "已保存流转音频读取验证过的设备；外放和听写仍需你确认。");
            return result;
        }
    }

    /// <summary>Explicit user action after hearing normal playback and confirming dictation. No implicit confirmation.</summary>
    public AudioProfileOperation ConfirmGood(AudioRouteInventory inventory, MicrophonePreferences preferences,
        AudioSignalEvidence evidence, bool userConfirmed)
    {
        lock (DiskGate)
        {
            if (!userConfirmed) return new(false, false, "请先实际确认扬声器外放和听写均正常，再保存可用配置。");
            var error = ValidateEvidence(inventory, preferences, evidence) ?? ValidateDefaults(inventory);
            if (error != null) return new(false, false, error);
            var defaults = inventory.Defaults.Select(x => new SavedDefault(x.Flow, x.Role, x.Endpoint!.Id, x.Endpoint.Name)).ToArray();
            var playback = inventory.Defaults.Where(x => x.Flow == DataFlow.Render).Select(x => x.Endpoint!)
                .DistinctBy(x => x.Id).Select(x => new SavedPlayback(x.Id, x.Name, x.Muted!.Value, x.Volume!.Value)).ToArray();
            return Save(ConfirmedFileName, "confirmed", new Profile(1, "confirmed", _clock(), preferences, defaults, playback),
                "已保存你确认可用的音频配置。系统默认、外放音量仅作对照，恢复时不会自动修改。");
        }
    }

    /// <summary>Pure inspection; never saves preferences or replaces a good profile with a suspicious configuration.</summary>
    public AudioProfileAssessment Evaluate(AudioRouteInventory inventory, MicrophonePreferences preferences, bool diagnosticOnly = false)
    {
        lock (DiskGate)
        {
            var confirmed = Load(ConfirmedFileName, "confirmed"); var observed = Load(ObservedFileName, "observed");
            var notices = new List<AudioProfileNotice>();
            void Add(string code, string title, string detail, AudioRouteSeverity severity = AudioRouteSeverity.Warning) =>
                notices.Add(new(code, severity, title, detail));
            if (confirmed.Error != null) Add("confirmed-profile-unreadable", "已确认的配置保护档案无法读取", confirmed.Error);
            if (observed.Error != null) Add("observed-profile-unreadable", "音频读取配置保护档案无法读取", observed.Error);
            if (!inventory.RenderReadable || !inventory.CaptureReadable || inventory.Defaults.Length != 6 || inventory.Defaults.Any(x => !x.Readable))
                Add("profile-inventory-unavailable", "当前音频设备信息不完整", "请刷新体检；未知状态不会覆盖已保存的可用配置。");
            if (inventory.RenderReadable && !inventory.Endpoints.Any(x => x.Flow == DataFlow.Render && x.Active && !AudioRouteHealth.VirtualName(x.Name)))
                Add("profile-physical-playback-missing", "没有发现可用的实体扬声器或耳机", "虚拟音频设备不能代替扬声器外放。请检查实体设备连接、驱动和 Windows 声音设置。");
            foreach (var pair in new[] { (preferences.OutputId, DataFlow.Render, inventory.RenderReadable, "output", "虚拟播放端"),
                (preferences.CaptureId, DataFlow.Capture, inventory.CaptureReadable, "capture", "虚拟录音端") })
            {
                if (string.IsNullOrEmpty(pair.Item1) || !pair.Item3) continue;
                var endpoint = inventory.Endpoints.FirstOrDefault(x => x.Id == pair.Item1 && x.Flow == pair.Item2 && x.Active);
                if (endpoint == null) Add("profile-selected-" + pair.Item4 + "-unavailable", "所选" + pair.Item5 + "不可用", "请恢复原设备或明确重新选择，不会按同名设备自动替换。");
                else if (endpoint.Muted == true || endpoint.Volume == 0)
                    Add("profile-selected-" + pair.Item4 + "-muted", "所选" + pair.Item5 + "已静音或音量为零", "虽然可能仍收到音频帧，听写链路仍可能无声。请检查 Windows 对应设备静音及音量。");
            }
            if (diagnosticOnly) Add("profile-diagnostic-only", "当前为仅诊断模式", "只测量收音，无法向听写设备输出；保护档案不会自动启用转发。", AudioRouteSeverity.Information);
            var baseline = confirmed.Profile;
            if (baseline != null)
            {
                if (baseline.Preferences != preferences)
                    Add("profile-selected-route-changed", "流转设备选择与可用配置不同", "若当前听写失效，可以恢复原设备选择；恢复会停止音频服务，之后需你重新开启。");
                foreach (var before in baseline.Defaults)
                {
                    var now = inventory.Defaults.FirstOrDefault(x => x.Flow == before.Flow && x.Role == before.Role);
                    if (now?.Readable == true && now.Endpoint?.Id != before.Id)
                    {
                        bool dangerous = before.Flow == DataFlow.Render && (now.Endpoint?.Active != true || AudioRouteHealth.VirtualName(now.Endpoint.Name));
                        Add($"profile-default-changed-{before.Flow}-{before.Role}",
                            $"{DirectionName(before.Flow)}{RoleName(before.Role)}默认设备已变化",
                            $"保存时：{before.Name}；现在：{now.Endpoint?.Name ?? "未设置可用设备"}。正常切换耳机或每应用独立输入不一定有问题；流转不会自动改回。",
                            dangerous ? AudioRouteSeverity.Warning : AudioRouteSeverity.Information);
                    }
                }
                foreach (var before in baseline.Playback)
                {
                    if (!inventory.Defaults.Any(x => x.Flow == DataFlow.Render && x.Readable && x.Endpoint?.Id == before.Id)) continue;
                    var now = inventory.Endpoints.FirstOrDefault(x => x.Flow == DataFlow.Render && x.Id == before.Id && x.Active);
                    if (now?.Volume is float volume && volume > 0 && volume < Math.Min(.05f, before.Volume * .2f))
                        Add("profile-playback-volume-low", "实体外放音量明显低于可用配置", "若没有刻意调低音量，请检查 Windows 输出音量；流转只提醒，不自动更改。");
                }
            }
            foreach (var route in inventory.Defaults.Where(x => x.Flow == DataFlow.Render && x.Readable))
            {
                if (route.Endpoint?.Active != true)
                {
                    Add("profile-playback-unavailable-" + route.Role, $"{RoleName(route.Role)}默认播放端不可用",
                        "请在 Windows 声音设置选择实际可用的扬声器或耳机；存在其他实体输出不代表默认播放已经接通。");
                    continue;
                }
                var endpoint = route.Endpoint!;
                if (AudioRouteHealth.VirtualName(endpoint.Name))
                    Add("profile-playback-virtual-" + route.Role, $"{RoleName(route.Role)}外放可能进入虚拟线",
                        "请把 Windows 对应默认播放改为实际扬声器或耳机，保留流转输出为虚拟播放端。");
                else if (endpoint.Muted == true || endpoint.Volume == 0)
                    Add("profile-playback-muted-" + route.Role, $"{RoleName(route.Role)}扬声器已静音或音量为零",
                        "请检查 Windows 输出静音、音量及应用音量；流转不会自动更改系统音量。");
            }
            if (baseline == null && observed.Profile != null && observed.Profile.Preferences != preferences)
                Add("profile-observed-route-changed", "流转设备与已验证的音频读取配置不同", "可以恢复曾被实际音频读取验证的设备；该记录未确认扬声器外放或听写结果。");
            var restore = PrepareRestoreCore(inventory, confirmed, observed);
            if ((baseline != null || observed.Profile != null) && !restore.CanRestore)
                Add("profile-restore-unavailable", "原设备暂不可恢复", restore.Message);
            bool warnings = notices.Any(x => x.Severity != AudioRouteSeverity.Information);
            string summary = confirmed.Error != null || observed.Error != null ? "保护档案异常，已保留原文件" : warnings ? "音频配置发生变化，请核对后恢复" :
                baseline != null ? notices.Any(x => x.Code.StartsWith("profile-default-changed-", StringComparison.Ordinal)) ?
                    "已有可用配置；部分设备已切换，可按实际用途保留" : "已有你确认可用的配置，当前未发现配置偏差" :
                observed.Profile != null ? "已保留音频读取验证过的设备，等待确认外放与听写" :
                "尚无可用配置保护档案；实际使用正常后可确认保存";
            return new(baseline != null, observed.Profile != null, restore.CanRestore, summary, notices.ToArray(), baseline?.SavedAtUtc);
        }
    }

    /// <summary>Only returns an exact, active pair; the caller must explicitly stop and save without auto-starting.</summary>
    public AudioProfileRestore PrepareRestore(AudioRouteInventory inventory)
    {
        lock (DiskGate) return PrepareRestoreCore(inventory, Load(ConfirmedFileName, "confirmed"), Load(ObservedFileName, "observed"));
    }

    private static AudioProfileRestore PrepareRestoreCore(AudioRouteInventory inventory, Loaded confirmed, Loaded observed)
    {
        // A corrupt higher-trust record must never silently fall back to an unconfirmed record.
        if (confirmed.Error != null) return new(null, "", confirmed.Error);
        var profile = confirmed.Profile;
        if (profile == null)
        {
            if (observed.Error != null) return new(null, "", observed.Error);
            profile = observed.Profile;
        }
        if (profile == null) return new(null, "", "尚未保存可恢复的流转音频设备。");
        if (!inventory.RenderReadable || !inventory.CaptureReadable)
            return new(null, profile.Kind, "设备列表读取不完整，请刷新；不会猜测或替换设备。");
        if (!inventory.Endpoints.Any(x => x.Id == profile.Preferences.OutputId && x.Flow == DataFlow.Render && x.Active) ||
            !inventory.Endpoints.Any(x => x.Id == profile.Preferences.CaptureId && x.Flow == DataFlow.Capture && x.Active))
            return new(null, profile.Kind, "原虚拟设备已断开、被禁用或重装。请恢复原驱动，或重新选择并验证；不会按同名设备替换。");
        return new(profile.Preferences with { }, profile.Kind,
            "只恢复流转的精确设备选择；系统默认、静音、音量、录音许可和自动跟随均不改动，音频不会自动重开。");
    }

    private string? ValidateEvidence(AudioRouteInventory inventory, MicrophonePreferences preferences, AudioSignalEvidence evidence)
    {
        if (!ValidText(preferences.OutputId, 1024) || !ValidText(preferences.CaptureId, 1024)) return "请先选择有效的虚拟音频设备。";
        if (evidence.OutputId != preferences.OutputId || evidence.CaptureId != preferences.CaptureId || evidence.ConnectionId <= 0 ||
            evidence.Frames <= 0 || evidence.OutputPcmBytes <= 0 || !evidence.Receiving || !evidence.Requested || evidence.DiagnosticOnly || evidence.HasFailure ||
            !float.IsFinite(evidence.SignalPeak) || evidence.SignalPeak is <= 0 or > 1 ||
            evidence.ObservedAtUtc > _clock() || _clock() - evidence.ObservedAtUtc > TimeSpan.FromSeconds(30))
            return "尚无当前设备的有效音频与输出读取证据。请在转发中对手机说话，实际听写正常后再保存。";
        if (!inventory.RenderReadable || !inventory.CaptureReadable) return "设备列表读取不完整，不保存为可用配置。";
        var output = inventory.Endpoints.FirstOrDefault(x => x.Id == preferences.OutputId && x.Flow == DataFlow.Render && x.Active);
        var capture = inventory.Endpoints.FirstOrDefault(x => x.Id == preferences.CaptureId && x.Flow == DataFlow.Capture && x.Active);
        if (output == null || capture == null) return "当前选择的精确音频设备不可用，不保存为可用配置。";
        if (output.Muted == true || output.Volume == 0 || capture.Muted == true || capture.Volume == 0)
            return "所选虚拟音频设备已静音或音量为零，不保存为可用配置。";
        if (!AudioRouteHealth.VirtualName(output.Name) || !AudioRouteHealth.VirtualName(capture.Name))
            return "当前设备未识别为虚拟音频对，暂不建立自动恢复档案，以免恢复到实体外放。";
        return null;
    }

    private static string? ValidateDefaults(AudioRouteInventory inventory)
    {
        if (inventory.Defaults.Length != 6 || inventory.Defaults.Any(x => !x.Readable || x.Endpoint?.Active != true || x.Endpoint.Flow != x.Flow ||
                !ValidText(x.Endpoint.Id, 1024) || !ValidText(x.Endpoint.Name, 512) || !inventory.Endpoints.Any(y => y.Id == x.Endpoint.Id && y.Flow == x.Flow && y.Active)) ||
            inventory.Defaults.Select(x => (x.Flow, x.Role)).Distinct().Count() != 6 ||
            inventory.Defaults.Any(x => x.Flow is not (DataFlow.Render or DataFlow.Capture) || x.Role is not (Role.Console or Role.Multimedia or Role.Communications)))
            return "六路系统默认设备信息不完整；请刷新并确认播放、录音均正常后再保存。";
        foreach (var endpoint in inventory.Defaults.Where(x => x.Flow == DataFlow.Render).Select(x => x.Endpoint!))
        {
            if (AudioRouteHealth.VirtualName(endpoint.Name)) return "系统默认播放仍指向虚拟音频设备。请先确认扬声器外放配置，不把疑似错误保存为可用配置。";
            if (!endpoint.Muted.HasValue || !endpoint.Volume.HasValue || !float.IsFinite(endpoint.Volume.Value) || endpoint.Volume < 0 || endpoint.Volume > 1)
                return "无法读取实体默认播放的静音或音量，暂不把当前配置保存为完整可用基准。";
            if (endpoint.Muted == true || endpoint.Volume <= 0) return "实体默认播放已静音或音量为零，请先实际确认扬声器外放正常。";
        }
        return null;
    }

    private Loaded Load(string name, string kind)
    {
        string path = Path.Combine(_directory, name);
        try
        {
            if (!File.Exists(path)) return new(null, null);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length <= 0 || stream.Length > MaximumBytes) throw new InvalidDataException();
            var bytes = new byte[MaximumBytes + 1]; int count = 0, read;
            while (count < bytes.Length && (read = stream.Read(bytes, count, bytes.Length - count)) > 0) count += read;
            if (count > MaximumBytes) throw new InvalidDataException();
            using (var document = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 16 })) ValidateObject(document.RootElement);
            var profile = JsonSerializer.Deserialize<Profile>(bytes.AsSpan(0, count), JsonOptions);
            if (profile == null || profile.Version != 1 || profile.Kind != kind || profile.SavedAtUtc == default ||
                profile.SavedAtUtc > _clock() + TimeSpan.FromMinutes(5) || profile.Preferences == null ||
                !ValidText(profile.Preferences.OutputId, 1024) || !ValidText(profile.Preferences.CaptureId, 1024) ||
                profile.Defaults == null || profile.Playback == null ||
                profile.Defaults.Any(x => x == null || !ValidText(x.Id, 1024) || !ValidText(x.Name, 512)) ||
                profile.Playback.Any(x => x == null || !ValidText(x.Id, 1024) || !ValidText(x.Name, 512) || !float.IsFinite(x.Volume) || x.Volume is <= 0 or > 1 || x.Muted))
                throw new InvalidDataException();
            if (kind == "observed" && (profile.Defaults.Length != 0 || profile.Playback.Length != 0)) throw new InvalidDataException();
            if (kind == "confirmed" && (profile.Defaults.Length != 6 || profile.Playback.Length is < 1 or > 3 ||
                profile.Defaults.Select(x => (x.Flow, x.Role)).Distinct().Count() != 6 ||
                profile.Defaults.Any(x => x.Flow is not (DataFlow.Render or DataFlow.Capture) || x.Role is not (Role.Console or Role.Multimedia or Role.Communications)) ||
                profile.Playback.Select(x => x.Id).Distinct().Count() != profile.Playback.Length ||
                profile.Defaults.Where(x => x.Flow == DataFlow.Render).Any(x => !profile.Playback.Any(y => y.Id == x.Id))))
                throw new InvalidDataException();
            return new(profile, null);
        }
        catch { return new(null, "配置保护档案不可读取，原文件已保留。请保留故障文件后再重新建立保护档案；不会空覆盖或猜测设备。"); }
    }

    private AudioProfileOperation Save(string name, string kind, Profile profile, string message)
    {
        var previous = Load(name, kind);
        if (previous.Error != null) return new(false, false, previous.Error);
        string? temporary = null;
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(profile, JsonOptions);
            if (bytes.Length > MaximumBytes) throw new InvalidDataException();
            Directory.CreateDirectory(_directory);
            temporary = Path.Combine(_directory, name + "." + Guid.NewGuid().ToString("N") + ".tmp");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, Path.Combine(_directory, name), true);
            return new(true, true, message);
        }
        catch { return new(false, false, "配置保护档案保存失败，未确认成功；原档案仍保留，请检查存储权限和空间后重试。"); }
        finally { if (temporary != null) try { File.Delete(temporary); } catch { } }
    }

    private static bool ValidText(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);
    private static void ValidateObject(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidDataException(); ValidateObject(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) ValidateObject(item);
    }
    private static string DirectionName(DataFlow flow) => flow == DataFlow.Render ? "播放" : "录音";
    private static string RoleName(Role role) => role switch { Role.Console => "日常", Role.Multimedia => "媒体", _ => "通话" };
}
