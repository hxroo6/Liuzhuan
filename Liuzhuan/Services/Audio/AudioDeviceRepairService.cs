using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using NAudio.CoreAudioApi;

namespace Liuzhuan.Services.Audio;

public sealed record AudioDeviceRepairEndpoint(string Id, string Name, DataFlow Flow, bool Active,
    string Description, string ControllerDeviceId, string ParentInstanceId, string MatchingDeviceId,
    bool MetadataReadable = true);
public sealed record AudioDeviceRepairInventory(AudioDeviceRepairEndpoint[] Endpoints, bool Readable = true);
public sealed record AudioDeviceRepairSuggestion(string Status, MicrophonePreferences? Preferences = null,
    string OutputName = "", string CaptureName = "", string Message = "");

/// <summary>
/// Read-only identification of a unique standard VB-CABLE device pair. A suggestion is not consent:
/// the caller must authorize repair explicitly and serialize changes through MicrophoneHost.
/// Never writes defaults/volume, activates a stream, installs a driver, or changes preferences.
/// </summary>
public static class AudioDeviceRepairService
{
    private static readonly PropertyKey MatchingDeviceId = new(new Guid("a8b865dd-2e3d-4094-ad97-e593a70c75d6"), 8);
    private static readonly PropertyKey EndpointInstanceId = new(new Guid("9c119480-ddc2-4954-a150-5bd240d454ad"), 2);
    private static readonly Regex ExtraChannels = new(@"(?:^|[^0-9])16\s*(?:ch(?:annel)?s?|声道)(?:\b|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    public static AudioDeviceRepairInventory Scan()
    {
        var endpoints = new List<AudioDeviceRepairEndpoint>(); bool readable = true;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
            {
                try
                {
                    foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
                    {
                        using (device)
                        {
                            try
                            {
                                var properties = device.Properties;
                                var controller = Read(properties, PropertyKeys.PKEY_Device_ControllerDeviceId);
                                var matching = Read(properties, MatchingDeviceId);
                                var instance = Read(properties, PropertyKeys.PKEY_Device_InstanceId);
                                if (instance.Length == 0) instance = Read(properties, EndpointInstanceId);
                                var parent = ParentId(instance);
                                var description = Read(properties, PropertyKeys.PKEY_Device_DeviceDesc);
                                endpoints.Add(new(device.ID, device.FriendlyName, flow, device.State == DeviceState.Active,
                                    description, controller, parent, matching,
                                    controller.Length > 0 && matching.Length > 0 && parent.Length > 0 && description.Length > 0));
                            }
                            catch { readable = false; }
                        }
                    }
                }
                catch { readable = false; }
            }
        }
        catch { readable = false; }
        return new(endpoints.ToArray(), readable);
    }

    public static AudioDeviceRepairSuggestion Suggest(MicrophonePreferences saved, AudioDeviceRepairInventory inventory)
    {
        if (!inventory.Readable || inventory.Endpoints.Any(x => x.Active && !x.MetadataReadable))
            return new("unavailable", Message: "音频设备信息读取不完整，请稍后再检查；未更改电脑设置。");
        var endpoints = inventory.Endpoints.Where(x => x.Active).ToArray();
        if (endpoints.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() != 1))
            return new("unavailable", Message: "音频设备列表发生变化，请重新检查。");
        var pairs = (from output in endpoints.Where(IsOutput)
                     from capture in endpoints.Where(IsCapture)
                     where SameDevice(output, capture)
                     select (output, capture)).ToArray();
        var current = pairs.Where(p => string.Equals(p.output.Id, saved.OutputId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.capture.Id, saved.CaptureId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (current.Length == 1)
            return new("current", new(current[0].output.Id, current[0].capture.Id), current[0].output.Name,
                current[0].capture.Name, "流转已选择当前可用的虚拟音频设备，可重新连接麦克风；电脑外放保持原设置。");
        if (pairs.Length == 0)
            return new("setup_required", Message: "未找到可安全匹配的虚拟音频设备对，请在电脑流转麦克风设置中手动选择或检查驱动。");
        if (pairs.Length != 1)
            return new("ambiguous", Message: "找到多组虚拟音频设备，无法自动选择。请在电脑流转麦克风设置中确认设备对。");
        var pair = pairs[0];
        return new("repair_available", new(pair.output.Id, pair.capture.Id), pair.output.Name, pair.capture.Name,
            "已找到同一驱动设备下的虚拟播放端和录音端。确认后仅重新设置流转，不改变电脑外放或系统默认设备。");
    }

    private static bool VerifiedCable(AudioDeviceRepairEndpoint endpoint) => endpoint.MetadataReadable &&
        string.Equals(endpoint.MatchingDeviceId, "VBAudioVACWDM", StringComparison.OrdinalIgnoreCase) &&
        endpoint.ControllerDeviceId.Length > 0 && endpoint.ParentInstanceId.Length > 0 &&
        !ExtraChannels.IsMatch(endpoint.Name) && !ExtraChannels.IsMatch(endpoint.Description);
    private static bool IsOutput(AudioDeviceRepairEndpoint endpoint) => endpoint.Flow == DataFlow.Render && VerifiedCable(endpoint);
    private static bool IsCapture(AudioDeviceRepairEndpoint endpoint) => endpoint.Flow == DataFlow.Capture && VerifiedCable(endpoint) &&
        (string.Equals(endpoint.Description, "CABLE Output", StringComparison.OrdinalIgnoreCase) ||
         endpoint.Name.StartsWith("CABLE Output (", StringComparison.OrdinalIgnoreCase));
    private static bool SameDevice(AudioDeviceRepairEndpoint output, AudioDeviceRepairEndpoint capture) =>
        string.Equals(output.ParentInstanceId, capture.ParentInstanceId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(output.ControllerDeviceId, capture.ControllerDeviceId, StringComparison.OrdinalIgnoreCase);
    private static string Read(PropertyStore properties, PropertyKey key) => properties.Contains(key) && properties[key].Value is string text ? text : "";

    private static string ParentId(string instance)
    {
        if (instance.Length is 0 or > 4096 || instance.Contains('\0') ||
            CM_Locate_DevNodeW(out var node, instance, 0) != 0 || CM_Get_Parent(out var parent, node, 0) != 0 ||
            CM_Get_Device_ID_Size(out var size, parent, 0) != 0 || size is 0 or > 4096) return "";
        var buffer = new StringBuilder(checked((int)size + 1));
        return CM_Get_Device_IDW(parent, buffer, (uint)buffer.Capacity, 0) == 0 ? buffer.ToString() : "";
    }
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Get_Parent(out uint parent, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Get_Device_ID_Size(out uint length, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_IDW(uint devInst, StringBuilder buffer, uint length, uint flags);
}
