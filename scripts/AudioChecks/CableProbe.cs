using System.Buffers.Binary;
using System.Diagnostics;
using Liuzhuan.Services.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

internal static class CableProbe
{
    private static double Peak(byte[] bytes, int count, WaveFormat format)
    {
        bool floating = format.Encoding == WaveFormatEncoding.IeeeFloat || format is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
        double peak = 0;
        for (int i = 0; i + format.BitsPerSample / 8 <= count; i += format.BitsPerSample / 8)
        {
            double sample = floating && format.BitsPerSample == 32 ? BitConverter.ToSingle(bytes, i) :
                format.BitsPerSample == 16 ? BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i)) / 32768.0 : 0;
            peak = Math.Max(peak, Math.Abs(sample));
        }
        return peak;
    }
    public static async Task CaptureOnly(int seconds)
    {
        var endpoint = VirtualAudioOutput.List(DataFlow.Capture).First(x => x.Name.StartsWith("CABLE Output", StringComparison.OrdinalIgnoreCase));
        using var enumerator = new MMDeviceEnumerator(); using var device = enumerator.GetDevice(endpoint.Id);
        using var capture = new WasapiCapture(device);
        long bytes = 0; double peak = 0;
        capture.DataAvailable += (_, e) => { Interlocked.Add(ref bytes, e.BytesRecorded); double value = Peak(e.Buffer, e.BytesRecorded, capture.WaveFormat); if (value > peak) peak = value; };
        capture.StartRecording();
        for (int i = 0; i < Math.Clamp(seconds, 1, 30); i++)
        { await Task.Delay(1000); Console.WriteLine($"CABLE capture second={i + 1} bytes={Interlocked.Read(ref bytes)} peak={peak:F5}"); peak = 0; }
        capture.StopRecording();
        Console.WriteLine("Capture ended; audio was measured and discarded, no recording file saved.");
    }
    public static async Task Run()
    {
        var output = VirtualAudioOutput.List(DataFlow.Render).FirstOrDefault(x => x.Name.StartsWith("CABLE Input", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("CABLE Input not present");
        var input = VirtualAudioOutput.List(DataFlow.Capture).FirstOrDefault(x => x.Name.StartsWith("CABLE Output", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("CABLE Output not present");
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(input.Id);
        using var capture = new WasapiCapture(device);
        using var render = new VirtualAudioOutput(output.Id);
        long received = 0; double peak = 0;
        capture.DataAvailable += (_, e) =>
        {
            Interlocked.Add(ref received, e.BytesRecorded);
            peak = Math.Max(peak, Peak(e.Buffer, e.BytesRecorded, capture.WaveFormat));
        };
        render.SetForwarding(true); capture.StartRecording();
        var watch = Stopwatch.StartNew(); int offset = 0;
        while (watch.ElapsedMilliseconds < 3000)
        {
            var pcm = new byte[AudioProtocol.PayloadSize];
            for (int i = 0; i < AudioProtocol.Samples; i++)
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(Math.Sin(2 * Math.PI * 500 * (offset + i) / AudioProtocol.Rate) * 2600));
            render.Accept(pcm, 0, pcm.Length); offset += AudioProtocol.Samples; await Task.Delay(20);
        }
        render.SetForwarding(false); capture.StopRecording();
        Console.WriteLine($"CABLE test: capturedBytes={received}, peak={peak:F4}, format={capture.WaveFormat}");
        if (received == 0 || peak < 0.005) throw new InvalidOperationException("Virtual cable did not deliver the synthetic signal; restart may be required.");
        Console.WriteLine("PASS: production PCM16 mono16k render path reaches CABLE Output. No physical speaker or phone microphone used.");
    }
}
