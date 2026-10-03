using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Liuzhuan.Models;
using Liuzhuan.Services;
using Liuzhuan.Views;

internal static class Program
{
    private sealed class OffscreenApp : Application { protected override void OnStartup(StartupEventArgs eventArgs) { } }
    private sealed class DirectProgress : IProgress<ImageProcessingProgress>
    {
        private readonly Action<ImageProcessingProgress> _action;
        public DirectProgress(Action<ImageProcessingProgress> action) => _action = action;
        public void Report(ImageProcessingProgress value) => _action(value);
    }
    private static int _checks;
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception(description);
        _checks++;
        Console.WriteLine("PASS " + description);
    }
    private static ImageProcessingResult Run(string destination, IReadOnlyList<ImageProcessingSource> sources, ImageProcessingOptions options,
        IProgress<ImageProcessingProgress>? progress = null, CancellationToken cancellation = default)
        => ImageProcessingService.ProcessAsync(destination, sources, options, progress, cancellation).GetAwaiter().GetResult();
    private static BitmapFrame Decode(string path)
    {
        using var input = File.OpenRead(path);
        return BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
    }
    private static byte[] Pixel(BitmapSource source, int x, int y)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var result = new byte[4];
        converted.CopyPixels(new Int32Rect(x, y, 1, 1), result, 4, 0);
        return result;
    }
    private static BitmapSource Bitmap(int width, int height, Func<int, int, (byte R, byte G, byte B, byte A)> color)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            var value = color(x, y); var offset = (y * width + x) * 4;
            pixels[offset] = value.B; pixels[offset + 1] = value.G; pixels[offset + 2] = value.R; pixels[offset + 3] = value.A;
        }
        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
    }
    private static void SavePng(string path, BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }
    [STAThread]
    static void Main(string[] args)
    {
        var artifacts = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "liuzhuan-image-processing-artifacts");
        Directory.CreateDirectory(artifacts);
        var root = Path.Combine(Path.GetTempPath(), "liuzhuan-image-processing-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var originals = Path.Combine(root, "originals"); var destination = Path.Combine(root, "output");
            Directory.CreateDirectory(originals); Directory.CreateDirectory(destination);
            var first = Path.Combine(originals, "同名图片.png");
            SavePng(first, Bitmap(640, 320, (x, y) => x < 320 ? ((byte)200, (byte)0, (byte)0, (byte)0) : ((byte)10, (byte)180, (byte)40, (byte)255)));
            var secondFolder = Path.Combine(originals, "another"); Directory.CreateDirectory(secondFolder);
            var second = Path.Combine(secondFolder, "同名图片.png"); SavePng(second, Bitmap(20, 60, (_, _) => ((byte)30, (byte)90, (byte)200, (byte)255)));
            var mutable = new MaterialItem { Id = "a", Type = MaterialType.Image, DisplayName = "同名图片.png", FilePath = first };
            var sources = ImageProcessingService.Snapshot(new[] { mutable, new MaterialItem { Id = "b", Type = MaterialType.Image, DisplayName = "竖图.png", FilePath = second },
                new MaterialItem { Id = "excluded", Type = MaterialType.Text, TextContent = "not an image" } });
            mutable.FilePath = Path.Combine(originals, "different.png");
            Check(sources.Count == 2 && sources[0].FilePath == first, "UI snapshot includes only images and survives later item mutation");
            var originalHashes = new Dictionary<string, byte[]> { [first] = SHA256.HashData(File.ReadAllBytes(first)), [second] = SHA256.HashData(File.ReadAllBytes(second)) };
            var keep = Path.Combine(destination, "keep.txt"); File.WriteAllText(keep, "parent must survive");
            var reports = new List<ImageProcessingProgress>(); int mainThread = Environment.CurrentManagedThreadId; bool backgroundSta = false;
            var jpeg = Run(destination, sources, new(ImageOutputFormat.Jpeg, 160, 90), new DirectProgress(value =>
            {
                backgroundSta = Environment.CurrentManagedThreadId != mainThread && Thread.CurrentThread.GetApartmentState() == ApartmentState.STA;
                reports.Add(value);
            }));
            Check(jpeg.SuccessCount == 2 && jpeg.FailureCount == 0 && jpeg.DirectoryPath != null, "real images produce successful individual batch results");
            Check(backgroundSta, "decoding and encoding run on a dedicated STA worker rather than the caller UI thread");
            var converted = Decode(jpeg.Items[0].OutputPath!); var small = Decode(jpeg.Items[1].OutputPath!);
            Check(converted.PixelWidth == 160 && converted.PixelHeight == 80, "longest edge shrinks a landscape image while retaining its ratio");
            Check(small.PixelWidth == 20 && small.PixelHeight == 60, "a smaller portrait is never enlarged");
            Check(converted.Decoder is JpegBitmapDecoder && Path.GetExtension(jpeg.Items[0].OutputPath!) == ".jpg", "JPEG extension matches actual encoded bytes");
            var white = Pixel(converted, 10, 10);
            Check(white[0] >= 245 && white[1] >= 245 && white[2] >= 245 && white[3] == 255, "transparent JPEG areas are white rather than black");
            Check(Path.GetFileName(jpeg.Items[0].OutputPath!) == "同名图片.jpg" && Path.GetFileName(jpeg.Items[1].OutputPath!) == "同名图片 (2).jpg", "same-name images retain both results with collision numbering");
            Check(jpeg.Items.All(item => item.OutputBytes == new FileInfo(item.OutputPath!).Length) && reports[^1].CompletedItems == 2
                && reports.Count(value => value.ItemResult != null) == 2, "reported sizes and item completion match closed output files");
            var png = Run(destination, sources.Take(1).ToArray(), new(ImageOutputFormat.Png, null));
            var transparent = Decode(png.Items[0].OutputPath!);
            Check(transparent.Decoder is PngBitmapDecoder && transparent.PixelWidth == 640 && transparent.PixelHeight == 320,
                "PNG conversion with resizing disabled preserves full dimensions");
            Check(Pixel(transparent, 10, 10)[3] == 0 && Pixel(transparent, 400, 10)[3] == 255, "PNG preserves transparent and opaque pixels");

            var noise = Path.Combine(originals, "纹理.png"); var random = new Random(23);
            SavePng(noise, Bitmap(512, 384, (_, _) => ((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)255)));
            var noisySource = new[] { new ImageProcessingSource("noise", "纹理", noise) };
            var lowQuality = Run(destination, noisySource, new(ImageOutputFormat.Jpeg, null, 35));
            var highQuality = Run(destination, noisySource, new(ImageOutputFormat.Jpeg, null, 98));
            Check(lowQuality.Items[0].OutputBytes < highQuality.Items[0].OutputBytes && Decode(lowQuality.Items[0].OutputPath!).Decoder is JpegBitmapDecoder,
                "JPEG quality changes real encoded file size on a textured fixture");
            var wrongExtension = Path.Combine(originals, "真实PNG但扩展名jpg.jpg"); File.Copy(first, wrongExtension);
            var wrongResult = Run(destination, new[] { new ImageProcessingSource("wrong", "真实PNG", wrongExtension) }, new(ImageOutputFormat.Png, 80));
            Check(wrongResult.SuccessCount == 1 && Decode(wrongResult.Items[0].OutputPath!).PixelWidth == 80, "source decoding follows image bytes rather than the filename extension");

            var oriented = Path.Combine(originals, "旋转相片.jpg"); var metadata = new BitmapMetadata("jpg");
            metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)6); metadata.SetQuery("/app1/ifd/{ushort=271}", "Fixture camera");
            var orientationEncoder = new JpegBitmapEncoder { QualityLevel = 98 };
            orientationEncoder.Frames.Add(BitmapFrame.Create(Bitmap(20, 40, (_, y) => y < 20 ? ((byte)220, (byte)10, (byte)10, (byte)255) : ((byte)10, (byte)10, (byte)220, (byte)255)), null, metadata, null));
            using (var output = File.Create(oriented)) orientationEncoder.Save(output);
            var rotation = Run(destination, new[] { new ImageProcessingSource("orientation", "旋转相片", oriented) }, new(ImageOutputFormat.Png, null));
            var rotated = Decode(rotation.Items[0].OutputPath!); var left = Pixel(rotated, 5, 10); var right = Pixel(rotated, 35, 10);
            Check(rotated.PixelWidth == 40 && rotated.PixelHeight == 20 && left[0] > left[2] && right[2] > right[0], "EXIF orientation is applied to pixels before dimensions and conversion");
            Check(rotated.Metadata is not BitmapMetadata outputMetadata || !outputMetadata.ContainsQuery("/app1/ifd/{ushort=271}"), "generated pictures do not carry source camera metadata");
            var expectedCorner = new[] { "red", "green", "yellow", "blue", "red", "blue", "yellow", "green" };
            for (int orientation = 1; orientation <= 8; orientation++)
            {
                var path = Path.Combine(originals, "orientation-" + orientation + ".jpg"); var exif = new BitmapMetadata("jpg");
                exif.SetQuery("/app1/ifd/{ushort=274}", (ushort)orientation);
                var encoded = new JpegBitmapEncoder { QualityLevel = 98 };
                encoded.Frames.Add(BitmapFrame.Create(Bitmap(40, 60, (x, y) => y < 30
                    ? (x < 20 ? ((byte)220, (byte)10, (byte)10, (byte)255) : ((byte)10, (byte)220, (byte)10, (byte)255))
                    : (x < 20 ? ((byte)10, (byte)10, (byte)220, (byte)255) : ((byte)220, (byte)220, (byte)10, (byte)255))), null, exif, null));
                using (var file = File.Create(path)) encoded.Save(file);
                var result = Run(destination, new[] { new ImageProcessingSource("orientation-" + orientation, "Orientation", path) }, new(ImageOutputFormat.Png, null));
                var frame = Decode(result.Items[0].OutputPath!); var corner = Pixel(frame, frame.PixelWidth / 4, frame.PixelHeight / 4);
                var actualCorner = corner[2] > 150 && corner[1] > 150 ? "yellow" : corner[2] > 150 ? "red" : corner[1] > 150 ? "green" : "blue";
                Check(frame.PixelWidth == (orientation >= 5 ? 60 : 40) && frame.PixelHeight == (orientation >= 5 ? 40 : 60)
                    && actualCorner == expectedCorner[orientation - 1], "EXIF orientation " + orientation + " preserves the expected visual corner and dimensions");
            }

            var gif = Path.Combine(originals, "多帧.gif"); var gifEncoder = new GifBitmapEncoder();
            gifEncoder.Frames.Add(BitmapFrame.Create(Bitmap(12, 12, (_, _) => ((byte)220, (byte)0, (byte)0, (byte)255))));
            gifEncoder.Frames.Add(BitmapFrame.Create(Bitmap(12, 12, (_, _) => ((byte)0, (byte)220, (byte)0, (byte)255))));
            using (var output = File.Create(gif)) gifEncoder.Save(output);
            var firstFrame = Run(destination, new[] { new ImageProcessingSource("gif", "多帧", gif) }, new(ImageOutputFormat.Png, null));
            var gifPixel = Pixel(Decode(firstFrame.Items[0].OutputPath!), 5, 5);
            Check(gifPixel[2] > gifPixel[1], "multi-frame input consistently processes its explicitly documented first frame");

            var invalid = Path.Combine(originals, "损坏图片.png"); File.WriteAllText(invalid, "not encoded image bytes");
            var failedSources = new[] { sources[0], new ImageProcessingSource("bad", "损坏图片", invalid), new ImageProcessingSource("missing", "不存在图片", Path.Combine(originals, "missing.png")) };
            var partial = Run(destination, failedSources, new(ImageOutputFormat.Png, 80));
            Check(partial.SuccessCount == 1 && partial.FailureCount == 2 && partial.Items.Skip(1).All(item => !string.IsNullOrWhiteSpace(item.Error)
                && item.OutputPath == null), "corrupt and missing files are individually reported while valid items remain usable");
            Check(Directory.GetFiles(partial.DirectoryPath!).Length == 1, "failed individual items leave no partial image outputs");
            var beforeFailures = Directory.GetDirectories(destination).OrderBy(path => path).ToArray();
            var failuresOnly = Run(destination, failedSources.Skip(1).ToArray(), new(ImageOutputFormat.Png, null));
            Check(failuresOnly.SuccessCount == 0 && failuresOnly.FailureCount == 2 && failuresOnly.DirectoryPath == null
                && Directory.GetDirectories(destination).OrderBy(path => path).SequenceEqual(beforeFailures), "an entirely failed batch removes its empty directory and reports no saved folder");
            using (var cancellation = new CancellationTokenSource())
            {
                bool stopped = false;
                try { Run(destination, sources, new(ImageOutputFormat.Png, 80), new DirectProgress(value => { if (value.CompletedItems == 1) cancellation.Cancel(); }), cancellation.Token); }
                catch (OperationCanceledException) { stopped = true; }
                Check(stopped && Directory.GetDirectories(destination).OrderBy(path => path).SequenceEqual(beforeFailures), "cancellation after first completed image removes only this batch's files and directory");
            }
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel(); bool stopped = false;
                try { Run(destination, sources, new(), cancellation: cancellation.Token); }
                catch (OperationCanceledException) { stopped = true; }
                Check(stopped && Directory.GetDirectories(destination).OrderBy(path => path).SequenceEqual(beforeFailures), "already cancelled requests never create output directories");
            }
            bool invalidOptions = false;
            try { Run(destination, sources, new(ImageOutputFormat.Jpeg, 0, 85)); }
            catch (ArgumentException) { invalidOptions = true; }
            Check(invalidOptions && Directory.GetDirectories(destination).OrderBy(path => path).SequenceEqual(beforeFailures), "invalid processing options fail before touching the destination");
            var deduplicated = Run(destination, new[] { sources[0], sources[0] }, new(ImageOutputFormat.Png, 40));
            Check(deduplicated.Items.Count == 1 && deduplicated.SuccessCount == 1, "repeated selection IDs do not generate redundant image copies");
            var oversize = Path.Combine(originals, "过宽图片.png"); SavePng(oversize, Bitmap(40000, 1, (_, _) => ((byte)10, (byte)20, (byte)30, (byte)255)));
            var rejected = Run(destination, new[] { new ImageProcessingSource("oversize", "过宽图片", oversize) }, new(ImageOutputFormat.Png, 100));
            Check(rejected.SuccessCount == 0 && rejected.Items[0].Error!.Contains("图片过大"), "decoder dimensions are checked before rendering an oversized input");
            using (var locked = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var lockedResult = Run(destination, sources.Take(1).ToArray(), new(ImageOutputFormat.Png, 80));
                Check(lockedResult.SuccessCount == 0 && lockedResult.FailureCount == 1 && lockedResult.DirectoryPath == null,
                    "an inaccessible source is an explicit item failure rather than a successful empty export");
            }
            beforeFailures = Directory.GetDirectories(destination).OrderBy(path => path).ToArray();
            string? externalFile = null;
            using (var cancellation = new CancellationTokenSource())
            {
                bool stopped = false;
                try
                {
                    Run(destination, sources, new(ImageOutputFormat.Png, 80), new DirectProgress(value =>
                    {
                        if (value.CompletedItems != 1) return;
                        var newDirectory = Directory.GetDirectories(destination).Single(path => !beforeFailures.Contains(path));
                        externalFile = Path.Combine(newDirectory, "external.txt"); File.WriteAllText(externalFile, "not created by the processing service");
                        cancellation.Cancel();
                    }), cancellation.Token);
                }
                catch (OperationCanceledException) { stopped = true; }
                Check(stopped && externalFile != null && File.ReadAllText(externalFile) == "not created by the processing service"
                    && Directory.GetFiles(Path.GetDirectoryName(externalFile)!).Length == 1, "cancel cleanup removes its pictures but preserves files added by another actor");
            }
            Check(originalHashes.All(pair => SHA256.HashData(File.ReadAllBytes(pair.Key)).SequenceEqual(pair.Value)) && File.ReadAllText(keep) == "parent must survive",
                "conversion, resizing, quality changes, failures and cancellation preserve source bytes and destination contents");

            RenderDialog(artifacts, sources);
            Console.WriteLine($"{_checks} checks passed. Offscreen artifacts: {artifacts}");
        }
        finally
        {
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(root).StartsWith("liuzhuan-image-processing-check-", StringComparison.Ordinal)) Directory.Delete(root, true);
        }
    }

    private static void RenderDialog(string artifacts, IReadOnlyList<ImageProcessingSource> sources)
    {
        var app = new OffscreenApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var application = XDocument.Load(Path.Combine(Directory.GetCurrentDirectory(), "Liuzhuan", "App.xaml")).Root!;
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var dictionary = new XElement(application.Element(presentation + "Application.Resources")!.Element(presentation + "ResourceDictionary")!);
        foreach (var declaration in application.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
        {
            var value = declaration.Value.StartsWith("clr-namespace:Liuzhuan.", StringComparison.Ordinal) && !declaration.Value.Contains(";assembly=")
                ? declaration.Value + ";assembly=Liuzhuan" : declaration.Value;
            if (value != declaration.Value) foreach (var element in dictionary.DescendantsAndSelf())
            {
                if (element.Name.NamespaceName == declaration.Value) element.Name = XName.Get(element.Name.LocalName, value);
                foreach (var attribute in element.Attributes().Where(attribute => attribute.Name.NamespaceName == declaration.Value).ToList())
                { element.SetAttributeValue(XName.Get(attribute.Name.LocalName, value), attribute.Value); attribute.Remove(); }
            }
            dictionary.SetAttributeValue(declaration.Name, value);
        }
        app.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
        var window = new ImageProcessingWindow(sources);
        ((TextBox)window.FindName("Destination")).Text = "F:\\图片输出";
        void Render(string name, double scale = 1)
        {
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(680, 670)); content.Arrange(new Rect(0, 0, 680, 670)); content.UpdateLayout();
            var shot = new RenderTargetBitmap((int)(680 * scale), (int)(670 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            var background = new DrawingVisual(); using (var drawing = background.RenderOpen()) drawing.DrawRectangle((Brush)app.FindResource("PanelBrush"), null, new Rect(0, 0, 680, 670));
            shot.Render(background); shot.Render(content); SavePng(Path.Combine(artifacts, name), shot);
        }
        Render("image-processing-jpeg.png");
        ((RadioButton)window.FindName("PngFormat")).IsChecked = true;
        Check(!((Slider)window.FindName("Quality")).IsEnabled, "PNG choice disables the irrelevant JPEG quality control");
        Render("image-processing-png-150-percent.png", 1.5);
        ((CheckBox)window.FindName("ResizeEnabled")).IsChecked = false;
        Check(!((TextBox)window.FindName("MaxEdge")).IsEnabled, "disabling resize clearly disables the edge input");
        Check(((TextBlock)window.FindName("SelectionCount")).Text == "2 张图片" && ((Button)window.FindName("ProcessButton")).IsEnabled, "dialog accepts immutable image snapshots without starting the real App or reading its data store");
        var rows = (System.Collections.ObjectModel.ObservableCollection<ImageProcessingWindow.ResultRow>)((ListBox)window.FindName("Results")).ItemsSource;
        rows.Add(new("旅行照片.png", "4032 × 3024 → 1920 × 1440 · 612 KB · 减小 73%", "✓", (Brush)app.FindResource("AccentBrush")));
        rows.Add(new("损坏图片.heic", "无法解码图片。HEIC 等格式需要电脑已安装相应图片解码器。", "!", (Brush)app.FindResource("ErrorBrush")));
        ((Border)window.FindName("ResultsPanel")).Visibility = Visibility.Visible;
        ((TextBlock)window.FindName("Status")).Text = "已生成 1 张图片，1 张失败，请查看结果。";
        ((Button)window.FindName("OpenButton")).Visibility = Visibility.Visible;
        ((ScrollViewer)window.FindName("OptionsScroll")).ScrollToBottom();
        Render("image-processing-results.png");
        window.Close(); app.Shutdown();
    }
}
