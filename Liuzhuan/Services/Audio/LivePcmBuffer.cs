namespace Liuzhuan.Services.Audio;

/// <summary>Bounded PCM ring. Overflow drops old audio; underrun emits silence, never replays it.</summary>
public sealed class LivePcmBuffer
{
    private readonly object _gate = new();
    private readonly byte[] _ring = new byte[AudioProtocol.PayloadSize * 10];
    private int _head, _count;
    private bool _enabled, _primed;
    private long _readPcmBytes;
    public long ReadPcmBytes { get { lock (_gate) return _readPcmBytes; } }
    public long DroppedFrames { get; private set; }
    public int BufferedMilliseconds { get { lock (_gate) return _count * 1000 / (AudioProtocol.Rate * 2); } }
    public void Enable(bool value) { lock (_gate) { _enabled = value; _head = _count = 0; _primed = false; } }
    public void Write(byte[] source, int offset, int count)
    {
        lock (_gate)
        {
            if (!_enabled || count != AudioProtocol.PayloadSize) return;
            if (_count + count > _ring.Length)
            {
                int discard = _count - AudioProtocol.PayloadSize * 3;
                _head = (_head + discard) % _ring.Length; _count -= discard;
                DroppedFrames += discard / AudioProtocol.PayloadSize;
            }
            for (int i = 0; i < count; i++) _ring[(_head + _count + i) % _ring.Length] = source[offset + i];
            _count += count;
        }
    }
    public int Read(byte[] target, int offset, int count)
    {
        Array.Clear(target, offset, count);
        lock (_gate)
        {
            if (!_enabled) return count;
            if (!_primed && _count >= AudioProtocol.PayloadSize * 3) _primed = true;
            if (!_primed) return count;
            int take = Math.Min(count, _count) & ~1;
            for (int i = 0; i < take; i++) target[offset + i] = _ring[(_head + i) % _ring.Length];
            _head = (_head + take) % _ring.Length; _count -= take;
            _readPcmBytes += take;
            if (take < count) _primed = false;
        }
        return count;
    }
}
