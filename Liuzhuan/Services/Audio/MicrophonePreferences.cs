using System.IO;
using System.Text.Json;

namespace Liuzhuan.Services.Audio;

/// <summary>Only local endpoint preferences; never consent, invitations or running states.</summary>
public sealed record MicrophonePreferences(string OutputId = "", string CaptureId = "")
{
    public static MicrophonePreferences Load(string directory)
    {
        try
        {
            var file = new FileInfo(Path.Combine(directory, "microphone.json"));
            if (!file.Exists || file.Length > 8192) return new();
            return JsonSerializer.Deserialize<MicrophonePreferences>(File.ReadAllText(file.FullName)) ?? new();
        }
        catch { return new(); }
    }
    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "microphone.json"); var temporary = path + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(this)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
