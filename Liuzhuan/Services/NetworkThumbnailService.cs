using System.IO;
using System.Windows.Media.Imaging;

namespace Liuzhuan.Services;

public static class NetworkThumbnailService
{
    private static readonly SemaphoreSlim Gate = new(2);
    public static async Task<byte[]> CreateAsync(string path)
    {
        await Gate.WaitAsync();
        try
        {
            byte[]? result = null;
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    using var input = File.OpenRead(path);
                    var image = new BitmapImage();
                    image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                    image.DecodePixelWidth = 256; image.StreamSource = input; image.EndInit();
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    using var output = new MemoryStream(); encoder.Save(output); result = output.ToArray();
                }
                catch (Exception ex) { failure = ex; }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            await Task.Run(thread.Join);
            if (failure != null) throw failure;
            return result!;
        }
        finally { Gate.Release(); }
    }
}
