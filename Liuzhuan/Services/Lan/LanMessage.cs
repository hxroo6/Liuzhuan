using System.Text.Json.Serialization;

namespace Liuzhuan.Services.Lan;

/// <summary>
/// 局域网协议消息帧（v1）
/// 与安卓端《流转》App 对应：docs/流转安卓版开发文档.md §3
/// </summary>
public class LanMessage
{
    [JsonPropertyName("v")] public int V { get; set; } = 1;
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("ts")] public long Ts { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    [JsonPropertyName("device")] public string Device { get; set; } = "";
    [JsonPropertyName("data")] public Dictionary<string, object?>? Data { get; set; }
}
