using System.Buffers.Binary;
using System.Text.Json;

namespace Liuzhuan.Services.Audio;

/// <summary>Versioned, deliberately plaintext LAN audio protocol. Mirrored by AudioProtocol.kt.</summary>
public static class AudioProtocol
{
    public const int Version = 1, Port = 8902, Rate = 16000, Samples = 320;
    public const int HeaderSize = 44, PayloadSize = Samples * 2, FrameSize = HeaderSize + PayloadSize;
    public const int MaxMessageSize = 8192;
    public const string Kind = "liuzhuan-mic";

    public static bool TryFrame(ReadOnlySpan<byte> bytes, long generation, long previousSequence, out long sequence)
    {
        sequence = -1;
        if (bytes.Length != FrameSize || BinaryPrimitives.ReadUInt16BigEndian(bytes) != Version ||
            BinaryPrimitives.ReadUInt64BigEndian(bytes[4..]) != (ulong)generation ||
            BinaryPrimitives.ReadUInt32BigEndian(bytes[36..]) != Samples ||
            BinaryPrimitives.ReadUInt32BigEndian(bytes[40..]) != PayloadSize) return false;
        var raw = BinaryPrimitives.ReadUInt64BigEndian(bytes[12..]);
        if (raw > long.MaxValue || (long)raw <= previousSequence) return false;
        sequence = (long)raw;
        return true;
    }

    public static string Message(string type, long generation = 0, object? data = null) =>
        JsonSerializer.Serialize(new { v = Version, type, generation, data });
}

public interface IAudioSink : IDisposable
{
    bool DiagnosticOnly => false;
    AudioLevel Level => new(0, 0, -96);
    void SetForwarding(bool value);
    void Accept(byte[] frame, int offset, int count);
    long DroppedFrames { get; }
    int BufferedMilliseconds { get; }
    // Actual PCM consumed by the render callback; generated silence is not counted.
    long? ReadPcmBytes => null;
}
