using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Liuzhuan.Services.Audio;

public sealed record AudioEndpointChoice(string Id, string Name) { public override string ToString() => Name; }

public sealed class VirtualAudioOutput : IAudioSink, IWaveProvider
{
    private readonly LivePcmBuffer _buffer = new();
    private readonly PcmSignalMeter _meter = new();
    public AudioLevel Level => _meter.Level;
    private readonly MMDeviceEnumerator _enumerator;
    private readonly MMDevice _device;
    private WasapiOut? _output;
    private Exception? _failure;
    private long _renderCallbacks;
    public long RenderCallbacks => Interlocked.Read(ref _renderCallbacks);
    public WaveFormat WaveFormat { get; } = new(AudioProtocol.Rate, 16, 1);
    public long DroppedFrames => _buffer.DroppedFrames;
    public int BufferedMilliseconds => _buffer.BufferedMilliseconds;
    public long? ReadPcmBytes => _buffer.ReadPcmBytes;
    public static AudioEndpointChoice[] List(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active).Select(d =>
        { using (d) return new AudioEndpointChoice(d.ID, d.FriendlyName); }).ToArray();
    }
    public VirtualAudioOutput(string endpointId)
    {
        _enumerator = new MMDeviceEnumerator();
        MMDevice? acquired = null;
        try
        {
            acquired = _enumerator.GetDevice(endpointId);
            // Opening the service must not start a perpetual silent render callback.
            if (acquired.State != DeviceState.Active) throw new InvalidOperationException("虚拟播放设备不可用。");
            _device = acquired;
        }
        catch { acquired?.Dispose(); _enumerator.Dispose(); throw; }
    }
    public int Read(byte[] buffer, int offset, int count)
    { Interlocked.Increment(ref _renderCallbacks); return _buffer.Read(buffer, offset, count); }
    public void SetForwarding(bool value)
    {
        if (value && (_failure != null || _device.State != DeviceState.Active))
            throw new InvalidOperationException("虚拟播放设备不可用，请结束并重新选择设备。", _failure);
        _buffer.Enable(value);
        _meter.Reset();
        if (value)
        {
            if (_output == null)
            {
                var output = new WasapiOut(_device, AudioClientShareMode.Shared, true, 40);
                try { output.PlaybackStopped += (_, e) => { if (e.Exception != null) _failure = e.Exception; }; output.Init(this); _output = output; }
                catch { output.Dispose(); throw; }
            }
            _output.Play();
        }
        else try { _output?.Stop(); } catch (Exception ex) { _failure = ex; }
    }
    public void Accept(byte[] frame, int offset, int count)
    {
        if (_failure != null || _device.State != DeviceState.Active)
            throw new InvalidOperationException("虚拟播放设备已失效。", _failure);
        _buffer.Write(frame, offset, count);
        _meter.Measure(frame.AsSpan(offset, count));
    }
    public void Dispose() { _buffer.Enable(false); _output?.Stop(); _output?.Dispose(); _device.Dispose(); _enumerator.Dispose(); }
}
