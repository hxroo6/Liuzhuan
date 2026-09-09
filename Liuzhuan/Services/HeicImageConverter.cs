using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Liuzhuan.Services;

public enum HeicConversionMode { Off, Png, Jpeg }

/// <summary>按文件内容识别 HEIC；输出独立副本，始终保留收到的原始文件。</summary>
public static class HeicImageConverter
{
    public static bool IsHeic(string path)
    {
        using var input = File.OpenRead(path);
        var header = new byte[4096];
        int count = 0, read;
        while (count < header.Length && (read = input.Read(header, count, header.Length - count)) > 0)
            count += read;
        if (count < 16 || Encoding.ASCII.GetString(header, 4, 4) != "ftyp") return false;
        uint size = ((uint)header[0] << 24) | ((uint)header[1] << 16) | ((uint)header[2] << 8) | header[3];
        if (size < 16 || size > count) return false;
        // mif1 本身不代表 HEIC（AVIF 也可使用），只接受 HEVC 图像品牌。
        for (int offset = 8; offset + 4 <= size; offset += 4)
        {
            if (offset == 12) continue; // minor_version
            var brand = Encoding.ASCII.GetString(header, offset, 4);
            if (brand is "heic" or "heix" or "hevc" or "hevx" or "heim" or "heis" or "hevm" or "hevs")
                return true;
        }
        return false;
    }

    public static (string Path, string Name) ConvertIfNeeded(string path, string name, HeicConversionMode mode)
    {
        if (mode == HeicConversionMode.Off || !IsHeic(path)) return (path, name);
        if (mode != HeicConversionMode.Png && mode != HeicConversionMode.Jpeg)
            throw new ArgumentOutOfRangeException(nameof(mode));

        using var input = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource source = decoder.Frames[0];
        BitmapEncoder encoder;
        if (mode == HeicConversionMode.Jpeg)
        {
            // JPEG 不支持透明度，使用白底，避免透明区域变黑。
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var rect = new Rect(0, 0, source.PixelWidth, source.PixelHeight);
                dc.DrawRectangle(Brushes.White, null, rect);
                dc.DrawImage(source, rect);
            }
            var flattened = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            flattened.Render(visual);
            source = flattened;
            encoder = new JpegBitmapEncoder { QualityLevel = 95 };
        }
        else encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));

        var extension = mode == HeicConversionMode.Png ? ".png" : ".jpg";
        // 即使原文件错误地命名为 .png，也不会覆盖原文件。
        var output = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!,
            System.IO.Path.GetFileNameWithoutExtension(path) + "_converted_" + Guid.NewGuid().ToString("N") + extension);
        var temporary = output + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write)) encoder.Save(stream);
            File.Move(temporary, output);
            return (output, System.IO.Path.ChangeExtension(name, extension));
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
