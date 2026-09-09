using System.IO;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using Liuzhuan.Services;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Directory.CreateDirectory(ConfigService.DirectoryPath);
        void Check(bool condition, string label) { if (!condition) throw new Exception(label); Console.WriteLine("PASS " + label); }
        Check(AppSettings.HeicConversion == HeicConversionMode.Png, "default PNG");
        foreach (var mode in Enum.GetValues<HeicConversionMode>())
        {
            AppSettings.HeicConversion = mode;
            AppSettings.Save();
            AppSettings.HeicConversion = (HeicConversionMode)99;
            AppSettings.Load();
            Check(AppSettings.HeicConversion == mode, "settings round trip " + mode);
        }
        foreach (var original in args)
        {
            var path = Path.Combine(ConfigService.DirectoryPath, Path.GetFileName(original));
            File.Copy(original, path);
            var hash = SHA256.HashData(File.ReadAllBytes(path));
            Check(HeicImageConverter.IsHeic(path), "HEIC detected despite PNG suffix");
            Check(HeicImageConverter.ConvertIfNeeded(path, Path.GetFileName(path), HeicConversionMode.Off).Path == path, "disabled passthrough");
            using var input = File.OpenRead(path);
            var source = BitmapDecoder.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            foreach (var mode in new[] { HeicConversionMode.Png, HeicConversionMode.Jpeg })
            {
                var result = HeicImageConverter.ConvertIfNeeded(path, Path.GetFileName(path), mode);
                using var output = File.OpenRead(result.Path);
                var decoder = BitmapDecoder.Create(output, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                Check(mode == HeicConversionMode.Png ? decoder is PngBitmapDecoder : decoder is JpegBitmapDecoder, "actual output encoding " + mode);
                var frame = decoder.Frames[0];
                Check(frame.PixelWidth == source.PixelWidth && frame.PixelHeight == source.PixelHeight, "full resolution " + mode);
                var stride = (frame.PixelWidth * frame.Format.BitsPerPixel + 7) / 8;
                frame.CopyPixels(new byte[stride * frame.PixelHeight], stride, 0);
                Check(!HeicImageConverter.IsHeic(result.Path), "converted image is not HEIC");
                Check(HeicImageConverter.ConvertIfNeeded(result.Path, result.Name, mode).Path == result.Path, "ordinary image passthrough");
            }
            Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))), "original bytes unchanged");
        }
        var broken = Path.Combine(ConfigService.DirectoryPath, "broken.png");
        File.WriteAllBytes(broken, new byte[] { 0,0,0,16,102,116,121,112,104,101,105,99,0,0,0,0 });
        bool failed = false;
        try { HeicImageConverter.ConvertIfNeeded(broken, "broken.png", HeicConversionMode.Png); }
        catch { failed = true; }
        Check(failed && File.Exists(broken) && !Directory.GetFiles(ConfigService.DirectoryPath, "*.tmp").Any(), "corrupt HEIC fails without loss or temporary residue");
        Console.WriteLine("Artifacts: " + ConfigService.DirectoryPath);
    }
}

namespace Liuzhuan.Services
{
    public static class ConfigService
    {
        public static string DirectoryPath = Path.Combine(Path.GetTempPath(), "Liuzhuan-HeicChecks-" + Guid.NewGuid().ToString("N"));
        public static string GetEffectiveDataDir() => DirectoryPath;
    }
}
namespace Liuzhuan.Utils
{
    public static class Logger
    {
        public static void Run(string format, params object[] args) { }
        public static void Error(string format, params object[] args) => throw new Exception(string.Format(format, args));
    }
}
