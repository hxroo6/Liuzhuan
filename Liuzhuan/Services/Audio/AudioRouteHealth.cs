using NAudio.CoreAudioApi;

namespace Liuzhuan.Services.Audio;

public sealed record AudioRouteEndpoint(string Id, string Name, DataFlow Flow, bool Active = true,
    bool? Muted = null, float? Volume = null);
public sealed record AudioDefaultRoute(DataFlow Flow, Role Role, AudioRouteEndpoint? Endpoint, bool Readable = true);
public sealed record AudioRouteInventory(AudioRouteEndpoint[] Endpoints, AudioDefaultRoute[] Defaults,
    bool RenderReadable = true, bool CaptureReadable = true);
public enum AudioRouteSeverity { Information, Warning, Error }
public sealed record AudioRouteFinding(string Code, AudioRouteSeverity Severity, string Title, string Detail);
public sealed record AudioRouteReport(string Summary, string Playback, string Output, string Recording,
    AudioRouteFinding[] Findings)
{
    // Only controlled codes/titles, never device names, endpoint IDs, credentials or audio.
    public string SanitizedText => "音频路由体检（仅配置，不代表听写成功）\n" +
        (Findings.Length == 0 ? "未发现配置阻断；仍需实际听写验证。" :
            string.Join("\n", Findings.Select(x => $"{x.Code}: {x.Title}")));
}

/// <summary>Read-only Core Audio inventory. Run off the UI thread; never activates a stream or sets a default.</summary>
public static class AudioRouteScanner
{
    private static AudioRouteEndpoint ReadEndpoint(MMDevice device, DataFlow flow)
    {
        bool? muted = null; float? volume = null;
        if (device.State == DeviceState.Active)
        {
            // Read-only endpoint metadata. Failure is unknown, never an implicit healthy volume.
            try { muted = device.AudioEndpointVolume.Mute; volume = device.AudioEndpointVolume.MasterVolumeLevelScalar; }
            catch { }
        }
        return new(device.ID, device.FriendlyName, flow, device.State == DeviceState.Active, muted, volume);
    }
    public static AudioRouteInventory Scan()
    {
        var endpoints = new List<AudioRouteEndpoint>();
        var defaults = new List<AudioDefaultRoute>();
        bool render = true, capture = true;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
            {
                bool readable = true;
                try
                {
                    foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.All))
                    {
                        using (device)
                        {
                            try { endpoints.Add(ReadEndpoint(device, flow)); }
                            catch { readable = false; }
                        }
                    }
                }
                catch { readable = false; }
                if (flow == DataFlow.Render) render = readable; else capture = readable;
                foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
                {
                    try
                    {
                        using var device = enumerator.GetDefaultAudioEndpoint(flow, role);
                        defaults.Add(new(flow, role, ReadEndpoint(device, flow)));
                    }
                    catch (System.Runtime.InteropServices.COMException ex) when ((uint)ex.HResult == 0x80070490)
                    { defaults.Add(new(flow, role, null)); } // No default endpoint is a known absence, not a successful route.
                    catch { defaults.Add(new(flow, role, null, false)); }
                }
            }
        }
        catch { render = capture = false; }
        return new(endpoints.ToArray(), defaults.ToArray(), render, capture);
    }
}

public static class AudioRouteHealth
{
    public static bool StandardOutput(string name) => name.StartsWith("CABLE Input (", StringComparison.OrdinalIgnoreCase);
    public static bool StandardCapture(string name) => name.StartsWith("CABLE Output (", StringComparison.OrdinalIgnoreCase);
    public static bool VirtualName(string name) => new[] { "CABLE", "VB-Audio", "Voicemeeter", "Virtual Audio", "Steam Streaming", "NVIDIA Broadcast" }
        .Any(x => name.Contains(x, StringComparison.OrdinalIgnoreCase));
    private static string RoleName(Role role) => role switch { Role.Console => "日常", Role.Multimedia => "媒体", _ => "通话" };
    private static string LevelText(AudioRouteEndpoint endpoint) => !endpoint.Active ? "（已禁用或断开）" :
        endpoint.Muted == true ? "（已静音）" : endpoint.Volume is { } volume && float.IsFinite(volume) ? $"（音量 {volume:P0}）" : "（音量未知）";
    private static string DefaultText(AudioDefaultRoute route) =>
        $"{RoleName(route.Role)}：{(!route.Readable ? "暂时无法读取" : route.Endpoint is { } endpoint ? endpoint.Name + LevelText(endpoint) : "未设置可用设备")}";

    public static AudioRouteReport Review(AudioRouteInventory inventory, string outputId, string captureId, bool diagnosticOnly)
    {
        var issues = new List<AudioRouteFinding>();
        void Add(string code, AudioRouteSeverity severity, string title, string detail) => issues.Add(new(code, severity, title, detail));
        void CheckLevel(AudioRouteEndpoint? endpoint, string code, string title)
        {
            if (endpoint?.Active != true) return;
            if (endpoint.Muted == true)
                Add(code + "-muted", AudioRouteSeverity.Warning, title + "已静音", "请在 Windows 声音设置中检查静音；流转不会代改系统音量。");
            else if (endpoint.Volume is { } volume && float.IsFinite(volume) && volume <= 0)
                Add(code + "-silent", AudioRouteSeverity.Warning, title + "音量为零", "请检查该设备音量；不要通过提高虚拟设备音量来替代实体外放路由。");
            if (endpoint.Muted == null || endpoint.Volume is not { } level || !float.IsFinite(level) || level < 0 || level > 1)
                Add(code + "-level-unknown", AudioRouteSeverity.Information, title + "音量暂时未知", "本次未能完整读取静音或音量，不能据此确认声音已正常。");
        }
        if (!inventory.RenderReadable || !inventory.CaptureReadable)
            Add("inventory-unavailable", AudioRouteSeverity.Warning, "设备列表读取不完整",
                "不能据此判断驱动缺失。请重新体检；持续失败时检查 Windows 音频服务。");
        var playbackDefaults = inventory.Defaults.Where(x => x.Flow == DataFlow.Render).ToArray();
        var captureDefaults = inventory.Defaults.Where(x => x.Flow == DataFlow.Capture).ToArray();
        if (inventory.Defaults.Length != 6 || inventory.Defaults.Any(x => !x.Readable))
            Add("defaults-unavailable", AudioRouteSeverity.Warning, "部分系统默认设备暂时无法读取", "请重新体检，不把未知状态当作配置正确。");
        foreach (var route in playbackDefaults)
        {
            if (!route.Readable) continue;
            if (route.Endpoint == null || !route.Endpoint.Active)
                Add("playback-missing-" + route.Role, AudioRouteSeverity.Warning, $"{RoleName(route.Role)}播放未设置可用设备",
                    "打开播放设备，选择实际使用的扬声器或耳机。流转不会代改系统默认设备。");
            else if (VirtualName(route.Endpoint.Name))
                Add("playback-virtual-" + route.Role, AudioRouteSeverity.Warning, $"{RoleName(route.Role)}播放指向虚拟音频设备",
                    "系统声音可能进入虚拟线，导致外放无声或混入听写。打开播放设备，将默认设备和默认通信设备设为实体扬声器／耳机。");
            CheckLevel(route.Endpoint, "playback-" + route.Role, $"{RoleName(route.Role)}播放");
        }
        if (inventory.RenderReadable && !inventory.Endpoints.Any(x => x.Flow == DataFlow.Render && x.Active && !VirtualName(x.Name)))
            Add("physical-playback-missing", AudioRouteSeverity.Warning, "未发现可用的实体扬声器或耳机",
                "笔记本内置扬声器若消失，请在设备管理器检查声卡或扫描硬件改动；连接耳机、显示器等设备后再体检。切换虚拟音频设备无法恢复缺失的实体声卡。");
        AudioRouteEndpoint? Find(string id, DataFlow flow) => inventory.Endpoints.FirstOrDefault(x => x.Id == id && x.Flow == flow);
        var output = Find(outputId, DataFlow.Render); var capture = Find(captureId, DataFlow.Capture);
        string SelectedText(AudioRouteEndpoint? endpoint, string id, string direction) =>
            endpoint == null ? string.IsNullOrEmpty(id) ? $"尚未选择{direction}" : $"已保存的{direction}不在当前设备列表中" :
                endpoint.Name + LevelText(endpoint);
        if (diagnosticOnly)
            Add("diagnostic-only", AudioRouteSeverity.Information, "当前仅诊断收音，不能用于听写", "此模式只接收并测量音频，不向虚拟设备输出。需要听写时关闭服务、取消勾选并选好设备后重开。");
        else
        {
            CheckLevel(output, "output", "流转的虚拟播放端");
            CheckLevel(capture, "capture", "听写的虚拟录音端");
            foreach (var tuple in new[] { (output, outputId, DataFlow.Render, "output", "虚拟播放端"), (capture, captureId, DataFlow.Capture, "capture", "虚拟录音端") })
            {
                bool readable = tuple.Item3 == DataFlow.Render ? inventory.RenderReadable : inventory.CaptureReadable;
                if (string.IsNullOrEmpty(tuple.Item2))
                    Add(tuple.Item4 + "-unselected", AudioRouteSeverity.Error, $"尚未选择{tuple.Item5}", "先在下方选择设备；标准 VB-CABLE 方向是 CABLE Input → CABLE Output。");
                else if ((tuple.Item1 == null && readable) || tuple.Item1?.Active == false)
                    Add(tuple.Item4 + "-unavailable", AudioRouteSeverity.Error, $"已选{tuple.Item5}不可用", "设备可能被禁用、拔出或重新安装。关闭音频服务后刷新并重新选择；不会自动回退到扬声器。");
            }
            if (output?.Active == true && !VirtualName(output.Name))
                Add("output-unverified", AudioRouteSeverity.Warning, "流转输出可能会直接外放",
                    "当前输出未识别为常见虚拟设备，请确认不是实体扬声器。若是其他虚拟驱动，请自行核对相连的录音端。");
            if (capture?.Active == true && !VirtualName(capture.Name))
                Add("capture-unverified", AudioRouteSeverity.Warning, "听写输入可能是其他麦克风",
                    "所选录音端未识别为常见虚拟设备，请核对它是否接收流转写入的音频。");
            if (output?.Name.Contains("CABLE In 16ch", StringComparison.OrdinalIgnoreCase) == true)
                Add("cable-16ch", AudioRouteSeverity.Warning, "当前选择了 16 通道设备", "标准 VB-CABLE 设置请改选 CABLE Input，避免与 CABLE Output 配对错误。");
            if (output?.Active == true && capture?.Active == true &&
                (StandardOutput(output.Name) != StandardCapture(capture.Name)))
                Add("pair-mismatch", AudioRouteSeverity.Warning, "所选虚拟设备可能不属于同一对", "标准 CABLE Input 应配标准 CABLE Output；其他驱动的端点连通性需要实际验证。");
            if (capture?.Active == true && captureDefaults.Any(x => x.Readable && x.Endpoint?.Id != captureId))
                Add("capture-default-different", AudioRouteSeverity.Information, "部分默认录音设备与听写目标不同",
                    "若听写软件跟随系统输入，请将默认录音与默认通信录音设为所选虚拟录音端；若软件独立选了输入，无需为此改全局设置。");
        }
        bool cableVisible = inventory.Endpoints.Any(x => StandardOutput(x.Name) || StandardCapture(x.Name));
        if (!cableVisible && inventory.RenderReadable && inventory.CaptureReadable && !diagnosticOnly &&
            (output == null || capture == null))
            Add("cable-not-found", AudioRouteSeverity.Information, "未找到标准 VB-CABLE 设备",
                "若尚未安装，可从下方官网独立安装并按安装器提示重启；使用其他虚拟驱动时请选择其对应端点。");
        int blocking = issues.Count(x => x.Severity == AudioRouteSeverity.Error);
        int warnings = issues.Count(x => x.Severity == AudioRouteSeverity.Warning);
        string summary = blocking > 0 ? $"有 {blocking} 项配置需要处理" : warnings > 0 ? $"发现 {warnings} 项需要核对的设置" :
            diagnosticOnly ? "诊断模式：只验证手机到电脑" : "未发现配置阻断，下一步验证实际听写";
        return new(summary, playbackDefaults.Length == 0 ? "无法读取默认播放设备" : string.Join("\n", playbackDefaults.Select(DefaultText)),
            diagnosticOnly ? "仅诊断：不输出虚拟音频" : SelectedText(output, outputId, "虚拟播放端"),
            SelectedText(capture, captureId, "虚拟录音端") + "\n" +
                (captureDefaults.Length == 0 ? "无法读取默认录音设备" : string.Join("\n", captureDefaults.Select(DefaultText))), issues.ToArray());
    }
}

public enum AudioSettingsPage { Sound, Playback, Recording }
public sealed record AudioSettingsCommand(string FileName, string Arguments)
{
    public static AudioSettingsCommand For(AudioSettingsPage page) => page switch
    {
        AudioSettingsPage.Sound => new("ms-settings:sound", ""),
        AudioSettingsPage.Playback => new("control.exe", "mmsys.cpl,,0"),
        AudioSettingsPage.Recording => new("control.exe", "mmsys.cpl,,1"),
        _ => throw new ArgumentOutOfRangeException(nameof(page))
    };
}
