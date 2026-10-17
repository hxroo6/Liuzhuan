using System.Buffers.Binary;

namespace Liuzhuan.Services.Audio;

public sealed record AudioLevel(float Rms, float Peak, float Dbfs);
public sealed class PcmSignalMeter
{
    private AudioLevel _level = new(0, 0, -96);
    public AudioLevel Level => Volatile.Read(ref _level);
    public void Reset() => Volatile.Write(ref _level, new(0, 0, -96));
    public void Measure(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length < 2) { Reset(); return; }
        double squares = 0; int peak = 0;
        for (int i = 0; i + 1 < pcm.Length; i += 2)
        {
            int value = BinaryPrimitives.ReadInt16LittleEndian(pcm[i..]);
            squares += (double)value * value; peak = Math.Max(peak, Math.Abs(value));
        }
        var rms = (float)(Math.Sqrt(squares / (pcm.Length / 2)) / 32768);
        Volatile.Write(ref _level, new(rms, peak / 32768f, rms > 0 ? Math.Max(-96, 20 * MathF.Log10(rms)) : -96));
    }
}

/// <summary>Explicit receive-only diagnostics: observes level and immediately discards PCM, never plays it.</summary>
public sealed class DiagnosticAudioSink : IAudioSink
{
    private readonly PcmSignalMeter _meter = new();
    private bool _enabled;
    public bool DiagnosticOnly => true;
    public AudioLevel Level => _meter.Level;
    public long DroppedFrames => 0;
    public int BufferedMilliseconds => 0;
    public void SetForwarding(bool value) { _enabled = value; _meter.Reset(); }
    public void Accept(byte[] frame, int offset, int count) { if (_enabled) _meter.Measure(frame.AsSpan(offset, count)); }
    public void Dispose() => SetForwarding(false);
}
