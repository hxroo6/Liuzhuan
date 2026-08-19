using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Liuzhuan.Utils;

namespace Liuzhuan.Services;

/// <summary>
/// Windows Shell 缩略图服务 — 使用 IShellItemImageFactory 获取系统缩略图
/// 支持图片和视频（系统注册了缩略图处理器的格式均可）
/// </summary>
public static class ThumbnailService
{
    private static string CacheDir => Path.Combine(ConfigService.GetEffectiveDataDir(), "thumbnails");

    static ThumbnailService()
    {
        Directory.CreateDirectory(CacheDir);
    }

    /// <summary>获取缩略图 BitmapSource，失败返回 null</summary>
    public static BitmapSource? GetThumbnail(string filePath, int size = 160)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                Logger.Run("ThumbnailService: file not found: {0}", filePath);
                return null;
            }

            // 尝试 Shell API 获取缩略图
            var hBitmap = GetShellThumbnail(filePath, size, size);
            if (hBitmap != IntPtr.Zero)
            {
                try
                {
                    var bmp = Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap, IntPtr.Zero, Int32Rect.Empty,
                        BitmapSizeOptions.FromWidthAndHeight(size, size));
                    bmp.Freeze();
                    return bmp;
                }
                finally
                {
                    DeleteObject(hBitmap);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("ThumbnailService.GetThumbnail failed for {0}: {1}", filePath, ex.Message);
        }

        return null;
    }

    /// <summary>获取或缓存缩略图路径（同步版本，在后台线程调用安全）</summary>
    public static string GetCachedThumbnailPath(string filePath, int size = 160)
    {
        try
        {
            if (!File.Exists(filePath)) return string.Empty;

            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            var hash = filePath.GetHashCode() ^ (int)File.GetLastWriteTime(filePath).Ticks ^ size;
            var cacheFile = Path.Combine(CacheDir, $"{hash}{ext}.png");

            if (File.Exists(cacheFile))
                return cacheFile;

            // 在当前线程获取 HBITMAP（COM 调用，不需要 UI 线程）
            var hBitmap = GetShellThumbnail(filePath, size, size);
            if (hBitmap == IntPtr.Zero)
                return string.Empty;

            try
            {
                // 在当前线程创建 BitmapSource（CreateBitmapSourceFromHBitmap 可在非UI线程调用）
                var bmp = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(size, size));
                bmp.Freeze(); // Freeze 使其跨线程安全

                SaveToPng(bmp, cacheFile);
                Logger.Run("Thumbnail generated: {0} -> {1}", Path.GetFileName(filePath), cacheFile);
                return cacheFile;
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("GetCachedThumbnailPath failed: {0}", ex.Message);
        }

        return string.Empty;
    }

    private static void SaveToPng(BitmapSource source, string path)
    {
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var fs = new FileStream(path, FileMode.Create);
            encoder.Save(fs);
        }
        catch (Exception ex)
        {
            Logger.Error("SaveToPng failed: {0}", ex.Message);
        }
    }

    #region Win32 Shell API

    private const int SIIGBF_RESIZETOFIT = 0x00;
    private const int SIIGBF_BIGGERSIZEOK = 0x01;
    private const int SIIGBF_THUMBNAILONLY = 0x02;
    private const int SIIGBF_ICONONLY = 0x04;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        [In] ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, [In] ref Guid bhid, [In] ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(int sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(int sfgaoMask, out int psfgaoAttribs);
        void Compare(IShellItem psi, int hint, out int piOrder);
    }

    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(
            [In] NativeSize size,
            [In] int flags,
            out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;

        public NativeSize(int width, int height)
        {
            Width = width;
            Height = height;
        }
    }

    private static Guid _guidIShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    private static Guid _guidIShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    private static IntPtr GetShellThumbnail(string filePath, int width, int height)
    {
        IShellItem? shellItem = null;
        try
        {
            var hr = SHCreateItemFromParsingName(filePath, IntPtr.Zero, ref _guidIShellItem, out shellItem);
            if (hr != 0 || shellItem == null)
            {
                Logger.Run("SHCreateItemFromParsingName failed: hr=0x{0:X}", hr);
                return IntPtr.Zero;
            }

            // QI for IShellItemImageFactory — 这是独立的接口指针，需要单独释放
            var factory = (IShellItemImageFactory)shellItem;
            var size = new NativeSize(width, height);
            hr = factory.GetImage(size, SIIGBF_RESIZETOFIT | SIIGBF_BIGGERSIZEOK, out var hBitmap);

            if (hr != 0)
            {
                Logger.Run("IShellItemImageFactory.GetImage failed: hr=0x{0:X}", hr);
                return IntPtr.Zero;
            }

            return hBitmap;
        }
        catch (Exception ex)
        {
            Logger.Error("GetShellThumbnail exception: {0}", ex.Message);
            return IntPtr.Zero;
        }
        finally
        {
            // 关键：必须释放 COM 对象，否则会泄露 native 资源导致崩溃
            if (shellItem != null)
            {
                try { Marshal.ReleaseComObject(shellItem); } catch { }
            }
        }
    }

    #endregion
}
