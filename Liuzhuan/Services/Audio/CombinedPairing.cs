using System.Text.Json;

namespace Liuzhuan.Services.Audio;

public static class CombinedPairing
{
    // Preserve the legacy top-level LAN fields: old Android versions ignore the optional mic object.
    public static string Create(string ip, int port, string password, string? microphonePayload)
    {
        if (microphonePayload == null) return JsonSerializer.Serialize(new { ip, port, pwd = password });
        using var microphone = JsonDocument.Parse(microphonePayload);
        return JsonSerializer.Serialize(new { ip, port, pwd = password, mic = microphone.RootElement });
    }
}
