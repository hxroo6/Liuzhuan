using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Liuzhuan.Models;
using Liuzhuan.Utils;

namespace Liuzhuan.Services;

/// <summary>
/// 拖拽服务 — 处理拖入识别分类和拖出数据封装
/// 核心：文件类仅存储路径索引，拖出时提供原始文件路径
/// </summary>
public static class DragDropService
{
    /// <summary>
    /// 从 DragEventArgs 提取素材项列表
    /// 支持来源：资源管理器、微信、浏览器
    /// </summary>
    public static List<MaterialItem> ExtractItems(DragEventArgs e)
    {
        var items = new List<MaterialItem>();
        var data = e.Data;

        try
        {
            // 优先处理文件拖入（资源管理器、微信文件、文件夹）
            if (data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])data.GetData(DataFormats.FileDrop);
                if (files != null)
                {
                    foreach (var path in files)
                    {
                        // 文件夹递归展开，单文件直接创建
                        var subItems = CreateFromPathRecursive(path);
                        items.AddRange(subItems);
                    }
                    Logger.Run("DragDrop: extracted {0} file items from FileDrop", items.Count);
                }
            }

            // 如果文件拖入没有结果，尝试从其他格式提取
            if (items.Count == 0)
            {
                // 浏览器拖入的图片可能以 Bitmap 或 DIB 格式提供
                if (data.GetDataPresent(DataFormats.Bitmap))
                {
                    var bmp = data.GetData(DataFormats.Bitmap) as System.Windows.Media.Imaging.BitmapSource;
                    if (bmp != null)
                    {
                        // 浏览器图片拖入 — 保存到临时目录并建立索引
                        var tempPath = SaveBitmapToTemp(bmp);
                        if (!string.IsNullOrEmpty(tempPath))
                        {
                            var item = CreateFromPath(tempPath);
                            if (item != null)
                                items.Add(item);
                        }
                    }
                }

                // 优先 UnicodeText（UTF-16，不会乱码），再 Text（ANSI，中文可能丢失）
                if (items.Count == 0 && data.GetDataPresent(DataFormats.UnicodeText))
                {
                    var text = data.GetData(DataFormats.UnicodeText) as string;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        items.Add(CreateTextItem(text));
                        Logger.Run("DragDrop: extracted text from UnicodeText, length={0}", text.Length);
                    }
                }

                // UnicodeText 没有则尝试 Text
                if (items.Count == 0 && data.GetDataPresent(DataFormats.Text))
                {
                    var text = data.GetData(DataFormats.Text) as string;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        items.Add(CreateTextItem(text));
                        Logger.Run("DragDrop: extracted text from Text, length={0}", text.Length);
                    }
                }

                // HTML 格式（浏览器拖入的文字可能只提供 HTML 格式）
                if (items.Count == 0 && data.GetDataPresent(DataFormats.Html))
                {
                    var html = data.GetData(DataFormats.Html) as string;
                    if (!string.IsNullOrWhiteSpace(html))
                    {
                        var plainText = ExtractPlainTextFromHtml(html);
                        if (!string.IsNullOrWhiteSpace(plainText))
                        {
                            items.Add(CreateTextItem(plainText));
                            Logger.Run("DragDrop: extracted text from HTML, length={0}", plainText.Length);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("DragDropService.ExtractItems failed: {0}", ex.Message);
        }

        return items;
    }

    /// <summary>
    /// 从文件路径创建素材项（支持所有格式，文件夹递归展开）
    /// </summary>
    public static MaterialItem? CreateFromPath(string path)
    {
        try
        {
            // 文件夹 — 递归展开内部文件
            if (Directory.Exists(path))
            {
                Logger.Run("DragDrop: expanding directory: {0}", path);
                return null; // 文件夹由调用方处理递归
            }

            if (!File.Exists(path))
            {
                Logger.Run("DragDrop: file not found: {0}", path);
                return null;
            }

            var type = FileClassifier.ClassifyByExtension(path);

            var info = new FileInfo(path);
            var item = new MaterialItem
            {
                Type = type,
                FilePath = path,
                DisplayName = info.Name,
                Size = info.Length,
                AddedTime = DateTime.Now
            };

            // 文本类文件：读取内容到 TextContent
            if (type == MaterialType.Text)
            {
                try
                {
                    item.TextContent = File.ReadAllText(path, System.Text.Encoding.UTF8);
                    // 如果内容过长，截断显示
                    if (item.TextContent.Length > 5000)
                        item.TextContent = item.TextContent[..5000] + "\n…（内容已截断）";
                }
                catch { }
            }

            return item;
        }
        catch (Exception ex)
        {
            Logger.Error("CreateFromPath failed for {0}: {1}", path, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 从文件路径创建素材项列表（文件夹递归展开为内部文件）
    /// </summary>
    public static List<MaterialItem> CreateFromPathRecursive(string path)
    {
        var result = new List<MaterialItem>();
        try
        {
            if (Directory.Exists(path))
            {
                // 递归展开文件夹内所有文件
                var files = Directory.GetFiles(path, "*", SearchOption.AllDirectories);
                foreach (var f in files)
                {
                    var item = CreateFromPath(f);
                    if (item != null)
                        result.Add(item);
                }
                Logger.Run("DragDrop: expanded directory {0} -> {1} files", path, result.Count);
            }
            else
            {
                var item = CreateFromPath(path);
                if (item != null)
                    result.Add(item);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("CreateFromPathRecursive failed for {0}: {1}", path, ex.Message);
        }
        return result;
    }

    /// <summary>
    /// 异步加载缩略图和时长（在后台线程执行，完成后回调到 UI 线程）
    /// </summary>
    public static void LoadDeferredPropertiesAsync(MaterialItem item, Action? onCompleted = null)
    {
        if (!item.IsFile) return;

        Task.Run(() =>
        {
            try
            {
                string? thumbPath = null;
                double duration = 0;

                // 缩略图 —— 后台线程生成，结果带回 UI 线程
                if (item.Type == MaterialType.Video || item.Type == MaterialType.Image)
                {
                    thumbPath = ThumbnailService.GetCachedThumbnailPath(item.FilePath);
                }

                // 音视频时长
                if (item.Type == MaterialType.Audio || item.Type == MaterialType.Video)
                {
                    duration = GetMediaDuration(item.FilePath);
                }

                return (thumbPath, duration);
            }
            catch (Exception ex)
            {
                Logger.Error("LoadDeferredPropertiesAsync failed: {0}", ex.Message);
                return (null, 0.0);
            }
        }).ContinueWith(t =>
        {
            // UI 线程：赋值 PropertyChanged 在此触发
            if (!string.IsNullOrEmpty(t.Result.thumbPath))
            {
                item.ThumbnailPath = t.Result.thumbPath;
                item.TryLoadThumbnailSync();
                Logger.Run("Deferred: thumbnail loaded for {0}", item.DisplayName);
            }
            if (t.Result.duration > 0)
            {
                item.Duration = t.Result.duration;
                Logger.Run("Deferred: duration loaded for {0}: {1}s", item.DisplayName, t.Result.duration);
            }
            onCompleted?.Invoke();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// 从剪贴板创建素材项（支持图片和文字）
    /// 返回值：item（素材项），bitmap（原始剪贴板图片，null 表示非图片类型）
    /// </summary>
    public static (MaterialItem? item, System.Windows.Media.Imaging.BitmapSource? bitmap) CreateFromClipboard()
    {
        try
        {
            // 优先检查剪贴板是否有图片 —— 使用 Win32 API 直接读取 CF_DIB
            // WPF Clipboard.GetImage() 返回的 BitmapSource 引用 COM 延迟加载内存，
            // PngBitmapEncoder 编码时像素数据已失效 → 透明 PNG。
            // Win32 GetClipboardData(CF_DIB) 直接读取 DIB 位图数据，立即复制到托管内存。
            var dibPath = SaveClipboardImageToFile();
            if (!string.IsNullOrEmpty(dibPath))
            {
                var item = CreateFromPath(dibPath);
                if (item != null)
                {
                    Logger.Run("Clipboard: pasted image via Win32 -> {0}", dibPath);
                    return (item, null);
                }
            }

            // 检查剪贴板是否有文件
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                if (files.Count > 0)
                {
                    var path = files[0] ?? "";
                    if (!string.IsNullOrEmpty(path))
                    {
                        var item = CreateFromPath(path);
                        if (item != null)
                        {
                            Logger.Run("Clipboard: pasted file -> {0}", path);
                            return (item, null);
                        }
                    }
                }
            }

            // 检查剪贴板是否有文字
            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    Logger.Run("Clipboard: pasted text, length={0}", text.Length);
                    return (CreateTextItem(text), null);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("CreateFromClipboard failed: {0}", ex.Message);
        }

        return (null, null);
    }

    /// <summary>
    /// 创建文字素材项
    /// </summary>
    public static MaterialItem CreateTextItem(string text)
    {
        text = text.Trim();
        return new MaterialItem
        {
            Type = MaterialType.Text,
            TextContent = text,
            DisplayName = text.Length > 30 ? text[..30] + "…" : text,
            Size = text.Length,
            AddedTime = DateTime.Now
        };
    }

    /// <summary>
    /// 创建拖出用的 DataObject
    /// 文件类：提供 FileDrop（原始文件路径）
    /// 文字类：提供 Text
    /// </summary>
    public static DataObject CreateDragOutData(MaterialItem item)
    {
        var data = new DataObject();

        if (item.IsFile)
        {
            if (File.Exists(item.FilePath))
            {
                // 关键：直接提供原始文件路径，不生成任何中间文件
                data.SetData(DataFormats.FileDrop, new[] { item.FilePath });
                Logger.Run("DragOut: FileDrop -> {0}", item.FilePath);
            }
            else
            {
                Logger.Run("DragOut: source file not found: {0}", item.FilePath);
            }
        }
        else
        {
            data.SetData(DataFormats.Text, item.TextContent);
            data.SetData(DataFormats.UnicodeText, item.TextContent);
            Logger.Run("DragOut: Text -> length={0}", item.TextContent.Length);
        }

        return data;
    }

    /// <summary>
    /// 获取拖出效果
    /// </summary>
    public static DragDropEffects GetDragOutEffects(MaterialItem item)
    {
        if (item.IsFile)
            return File.Exists(item.FilePath) ? DragDropEffects.Copy : DragDropEffects.None;
        return DragDropEffects.Copy;
    }

    #region Private helpers

    #region Win32 Clipboard CF_DIB → PNG

    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(IntPtr hWndOwner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint uFormat);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern uint GlobalSize(IntPtr hMem);

    private const uint CF_DIB = 8;

    /// <summary>
    /// 用 Win32 API 直接读剪贴板 CF_DIB → 转成 PNG 文件。
    /// 绕过 WPF Clipboard 的 COM 延迟加载机制，确保像素数据立即复制。
    /// </summary>
    private static string SaveClipboardImageToFile()
    {
        if (!IsClipboardFormatAvailable(CF_DIB))
            return string.Empty;

        if (!OpenClipboard(IntPtr.Zero))
            return string.Empty;

        try
        {
            var hData = GetClipboardData(CF_DIB);
            if (hData == IntPtr.Zero) return string.Empty;

            var size = GlobalSize(hData);
            if (size == 0) return string.Empty;

            var ptr = GlobalLock(hData);
            if (ptr == IntPtr.Zero) return string.Empty;

            try
            {
                // CF_DIB 数据 = BITMAPINFOHEADER + 像素数据
                // 读取 BITMAPINFOHEADER（40 字节）
                var bih = new byte[40];
                Marshal.Copy(ptr, bih, 0, 40);

                int biSize = BitConverter.ToInt32(bih, 0);
                int width = BitConverter.ToInt32(bih, 4);
                int height = BitConverter.ToInt32(bih, 8);
                short biPlanes = BitConverter.ToInt16(bih, 12);
                short biBitCount = BitConverter.ToInt16(bih, 14);

                // 读取完整 DIB 数据
                var dibData = new byte[size];
                Marshal.Copy(ptr, dibData, 0, (int)size);

                // 构造 BMP 文件 = BMP File Header (14B) + DIB Data
                var bmpFile = new byte[14 + dibData.Length];
                bmpFile[0] = (byte)'B';
                bmpFile[1] = (byte)'M';
                BitConverter.GetBytes((uint)(14 + dibData.Length)).CopyTo(bmpFile, 2);
                BitConverter.GetBytes((uint)0).CopyTo(bmpFile, 6);
                BitConverter.GetBytes((uint)54).CopyTo(bmpFile, 10); // offset to pixel data
                dibData.CopyTo(bmpFile, 14);

                // 保存为 BMP 文件
                var tempDir = Path.Combine(ConfigService.GetEffectiveDataDir(), "temp");
                Directory.CreateDirectory(tempDir);
                var fileName = $"drag_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.bmp";
                var bmpPath = Path.Combine(tempDir, fileName);
                File.WriteAllBytes(bmpPath, bmpFile);

                Logger.Run("Clipboard: CF_DIB saved as BMP: {0} ({1}x{2} {3}bpp)", bmpPath, width, height, biBitCount);
                return bmpPath;
            }
            finally
            {
                GlobalUnlock(hData);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    #endregion

    private static string SaveBitmapToTemp(System.Windows.Media.Imaging.BitmapSource bitmap)
    {
        try
        {
            var tempDir = Path.Combine(ConfigService.GetEffectiveDataDir(), "temp");
            Directory.CreateDirectory(tempDir);

            var fileName = $"drag_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.png";
            var filePath = Path.Combine(tempDir, fileName);

            // 剪贴板的 BitmapSource 底层引用 COM 共享内存，
            // BitmapFrame.Create / FormatConvertedBitmap 均无法可靠读取像素。
            // 使用 RenderTargetBitmap 物理渲染 —— 创建全新的独立像素副本。
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                bitmap.PixelWidth, bitmap.PixelHeight, 96, 96,
                System.Windows.Media.PixelFormats.Pbgra32);
            var dv = new System.Windows.Media.DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawImage(bitmap, new System.Windows.Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
            }
            rtb.Render(dv);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = new FileStream(filePath, FileMode.Create);
            encoder.Save(fs);

            Logger.Run("DragDrop: saved bitmap to temp: {0} ({1}x{2})", filePath, bitmap.PixelWidth, bitmap.PixelHeight);
            return filePath;
        }
        catch (Exception ex)
        {
            Logger.Error("SaveBitmapToTemp failed: {0}", ex.Message);
            return string.Empty;
        }
    }

    private static string ExtractPlainTextFromHtml(string html)
    {
        try
        {
            // Windows 剪贴板 CF_HTML 格式有一个头部，包含 StartHTML/EndHTML/StartFragment/EndFragment 偏移量
            // .NET GetData(Html) 返回的字符串可能因编码问题导致中文乱码
            // 尝试从 CF_HTML 头部解析 Fragment 部分

            int fragmentStart = -1;
            int fragmentEnd = -1;

            // 解析 CF_HTML 头部偏移量
            var startMatch = System.Text.RegularExpressions.Regex.Match(html, @"StartFragment:(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var endMatch = System.Text.RegularExpressions.Regex.Match(html, @"EndFragment:(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (startMatch.Success && endMatch.Success)
            {
                if (int.TryParse(startMatch.Groups[1].Value, out fragmentStart) &&
                    int.TryParse(endMatch.Groups[1].Value, out fragmentEnd) &&
                    fragmentStart >= 0 && fragmentEnd > fragmentStart && fragmentEnd <= html.Length)
                {
                    html = html.Substring(fragmentStart, fragmentEnd - fragmentStart);
                }
            }

            // 移除 <head> 部分
            var headEnd = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
            if (headEnd >= 0)
                html = html[(headEnd + 7)..];

            // 移除所有标签
            var result = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
            // 解码 HTML 实体（&amp; &lt; &nbsp; 等）
            result = System.Net.WebUtility.HtmlDecode(result);
            // 压缩空白
            result = System.Text.RegularExpressions.Regex.Replace(result, @"\s+", " ").Trim();

            return result;
        }
        catch
        {
            return html;
        }
    }

    private static double GetMediaDuration(string filePath)
    {
        // MediaPlayer 需要 STA 线程；在后台线程调用时通过专用 STA 线程执行
        double duration = 0;

        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            // 在专用 STA 线程中运行
            var thread = new Thread(() =>
            {
                duration = GetMediaDurationInner(filePath);
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join(3000); // 最多等 3 秒
            return duration;
        }

        return GetMediaDurationInner(filePath);
    }

    private static double GetMediaDurationInner(string filePath)
    {
        try
        {
            var player = new System.Windows.Media.MediaPlayer();
            player.Open(new Uri(filePath));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!player.NaturalDuration.HasTimeSpan && sw.ElapsedMilliseconds < 1500)
                System.Threading.Thread.Sleep(50);

            var duration = player.NaturalDuration.HasTimeSpan
                ? player.NaturalDuration.TimeSpan.TotalSeconds
                : 0;
            player.Close();
            return duration;
        }
        catch (Exception ex)
        {
            Logger.Error("GetMediaDuration failed for {0}: {1}", filePath, ex.Message);
            return 0;
        }
    }

    #endregion
}
