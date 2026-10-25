using System.Text.Json.Serialization;

namespace Liuzhuan.Services.Lan;

/// <summary>
/// 局域网协议消息帧（v1）
/// 与安卓端《流转》App 对应：docs/流转安卓版开发文档.md §3
/// </summary>
public class LanMessage
{
    public const string LibraryEditFeature = "material_library_edit_v2";
    public const string LibraryChangesFeature = "material_library_changes_v1";
    public const string ProjectPackageFeature = "project_package_v1";
    // Feature 表示 clipboard_push 的同请求登记回执与进程内幂等支持，协议版本仍为 1。
    public const string ClipboardReceiptsFeature = "clipboard_receipts_v1";
    public const string MicrophonePairingFeature = "microphone_pairing_v1";
    public const string MicrophoneConfigurationFeature = "microphone_configuration_v1";
    public const string MicrophoneConfigurationRequest = "microphone_configuration_request";
    public const string MicrophoneConfigurationReply = "microphone_configuration_result";
    public const string MicrophoneInviteRequest = "microphone_invite";
    public const string MicrophoneInviteReply = "microphone_invite_result";
    [JsonPropertyName("v")] public int V { get; set; } = 1;
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("ts")] public long Ts { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    [JsonPropertyName("device")] public string Device { get; set; } = "";
    [JsonPropertyName("data")] public Dictionary<string, object?>? Data { get; set; }
}
