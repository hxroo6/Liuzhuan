using System.IO;
using System.Windows.Media.Imaging;

namespace Liuzhuan.Services;

public static class ThumbnailBudget
{
    public const int MaxEdge=320;
    private static int _active,_peak;
    public static int Active=>Volatile.Read(ref _active);
    public static int Peak=>Volatile.Read(ref _peak);
    public static BitmapSource Decode(string path,int maxEdge=MaxEdge)
    {
        if(maxEdge is <1 or >MaxEdge)throw new ArgumentOutOfRangeException(nameof(maxEdge));
        var active=Interlocked.Increment(ref _active);int peak;
        do {peak=Volatile.Read(ref _peak);if(active<=peak)break;}while(Interlocked.CompareExchange(ref _peak,active,peak)!=peak);
        try {
            using var input=File.OpenRead(path);
            var frame=BitmapDecoder.Create(input,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None).Frames[0];
            if(frame.PixelWidth<1||frame.PixelHeight<1||(long)frame.PixelWidth*frame.PixelHeight>256_000_000)throw new IOException("缩略图尺寸超出预算。");
            int width=frame.PixelWidth,height=frame.PixelHeight;input.Position=0;
            var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;
            if(width>=height)bitmap.DecodePixelWidth=Math.Min(maxEdge,width);else bitmap.DecodePixelHeight=Math.Min(maxEdge,height);
            bitmap.StreamSource=input;bitmap.EndInit();bitmap.Freeze();return bitmap;
        } finally {Interlocked.Decrement(ref _active);}
    }
}
