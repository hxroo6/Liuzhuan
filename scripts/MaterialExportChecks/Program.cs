using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Liuzhuan.Models;
using Liuzhuan.Services;

internal static class Program
{
    private sealed class DirectProgress : IProgress<MaterialExportProgress>
    {
        private readonly Action<MaterialExportProgress> _action;
        public DirectProgress(Action<MaterialExportProgress> action) => _action = action;
        public void Report(MaterialExportProgress value) => _action(value);
    }

    private static int _checks;
    private static void Check(bool value, string label)
    {
        if (!value) throw new Exception(label);
        _checks++;
        Console.WriteLine("PASS " + label);
    }

    static async Task Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "liuzhuan-material-export-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var originals = Path.Combine(root, "originals");
            var destination = Path.Combine(root, "exports");
            Directory.CreateDirectory(originals);
            Directory.CreateDirectory(destination);
            var source = Path.Combine(originals, "照片.bin");
            var sourceBytes = Enumerable.Range(0, 2 * 1024 * 1024).Select(i => (byte)(i % 251)).ToArray();
            await File.WriteAllBytesAsync(source, sourceBytes);
            var secondDirectory = Path.Combine(originals, "second");
            Directory.CreateDirectory(secondDirectory);
            var second = Path.Combine(secondDirectory, "照片.bin");
            await File.WriteAllTextAsync(second, "distinct same-name source");
            var fullText = new string('全', 200000) + "\n完整结尾 ✅";
            var textFile = Path.Combine(originals, "长笔记.txt");
            await File.WriteAllTextAsync(textFile, fullText);
            // 高代理项恰好落在编码块末尾。
            var clipboardText = new string('文', 4095) + "😀" + new string('字', 8193) + "\n末尾";
            var mutable = new MaterialItem { Id = "text", Type = MaterialType.Text, DisplayName = "剪贴板笔记", TextContent = clipboardText };
            var items = new[]
            {
                new MaterialItem { Id = "a", Type = MaterialType.Image, DisplayName = "照片", FilePath = source },
                new MaterialItem { Id = "b", Type = MaterialType.Other, FilePath = second },
                new MaterialItem { Id = "c", Type = MaterialType.Text, FilePath = textFile, TextContent = "截断预览" },
                mutable,
                new MaterialItem { Id = "a", Type = MaterialType.Image, FilePath = source },
                new MaterialItem { Id = "d", Type = MaterialType.Image, FilePath = source }
            };
            var snapshot = MaterialExportService.Snapshot(items);
            mutable.TextContent = "修改后的实时值";
            mutable.DisplayName = "修改后的实时名称";
            var keep = Path.Combine(destination, "keep.txt");
            await File.WriteAllTextAsync(keep, "keep parent untouched");
            var reports = new List<MaterialExportProgress>();
            var result = await MaterialExportService.ExportAsync(destination, snapshot, new DirectProgress(reports.Add));
            var files = Directory.GetFiles(result.DirectoryPath);
            Check(result.ItemCount == 5 && files.Length == 5, "deduplicates identical IDs but retains separate duplicate references");
            Check(files.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 5, "same-name originals receive separate filenames without overwriting");
            Check(File.ReadAllBytes(Path.Combine(result.DirectoryPath, "照片.bin")).SequenceEqual(sourceBytes)
                && File.ReadAllText(Path.Combine(result.DirectoryPath, "照片 (2).bin")) == "distinct same-name source"
                && File.ReadAllBytes(Path.Combine(result.DirectoryPath, "照片 (3).bin")).SequenceEqual(sourceBytes), "all original bytes survive filename collisions");
            Check(File.ReadAllText(Path.Combine(result.DirectoryPath, "长笔记.txt")) == fullText, "text files export full original rather than truncated preview");
            Check(File.ReadAllText(Path.Combine(result.DirectoryPath, "剪贴板笔记.txt")) == clipboardText, "snapshot is independent of later UI mutation and preserves Unicode across blocks");
            Check(!File.ReadAllBytes(Path.Combine(result.DirectoryPath, "剪贴板笔记.txt")).Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }), "plain text uses UTF-8 without a BOM");
            var final = reports[^1];
            Check(final.CompletedItems == 5 && final.TotalItems == 5 && final.CopiedBytes == result.CopiedBytes
                && final.TotalBytes == result.CopiedBytes && files.Sum(p => new FileInfo(p).Length) == result.CopiedBytes, "progress totals match complete output files");
            Check(reports.Zip(reports.Skip(1)).All(p => p.First.CopiedBytes <= p.Second.CopiedBytes
                && p.First.CompletedItems <= p.Second.CompletedItems), "progress byte and item counts remain monotonic");
            Check(files.All(p => Path.GetDirectoryName(p) == result.DirectoryPath) && !files.Any(p => Path.GetFileName(p) is "manifest.json" or "data.json" or "lan.json"), "exports only content files inside a separate child folder");
            Check(File.ReadAllText(keep) == "keep parent untouched", "existing destination contents remain unchanged");

            var unsafeNames = new[] { "../../escape", "CON", "LPT¹", "PRN.txt", "bad<>:\"/\\|?*\u0001", new string('长', 300), "..", "尾随. " };
            var unsafeSources = unsafeNames.Select((name, i) => new MaterialExportSource("safe-" + i, name, MaterialType.Text, "", "content-" + i)).ToArray();
            var safe = await MaterialExportService.ExportAsync(destination, unsafeSources);
            var safeFiles = Directory.GetFiles(safe.DirectoryPath);
            Check(safeFiles.Length == unsafeNames.Length && safeFiles.All(p => Path.GetDirectoryName(p) == safe.DirectoryPath), "traversal names never escape the export child directory");
            Check(safeFiles.All(p => { var name = Path.GetFileName(p); return name.Length <= 160 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.EndsWith('.') && !name.EndsWith(' '); }), "illegal characters and oversized names are sanitized");
            Check(File.Exists(Path.Combine(safe.DirectoryPath, "_CON.txt")) && File.Exists(Path.Combine(safe.DirectoryPath, "_LPT¹.txt")) && File.Exists(Path.Combine(safe.DirectoryPath, "_PRN.txt")), "Windows reserved names including superscript variants are escaped");

            var existingFolders = Directory.GetDirectories(destination).OrderBy(p => p).ToArray();
            using (var cancellation = new CancellationTokenSource())
            {
                bool stopped = false;
                try
                {
                    await MaterialExportService.ExportAsync(destination, snapshot, new DirectProgress(p =>
                    {
                        if (p.CopiedBytes > 0) cancellation.Cancel();
                    }), cancellation.Token);
                }
                catch (OperationCanceledException) { stopped = true; }
                Check(stopped && Directory.GetDirectories(destination).OrderBy(p => p).SequenceEqual(existingFolders), "cancel during streaming removes only its incomplete child directory");
            }
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                bool stopped = false;
                try { await MaterialExportService.ExportAsync(destination, snapshot, cancellationToken: cancellation.Token); }
                catch (OperationCanceledException) { stopped = true; }
                Check(stopped && Directory.GetDirectories(destination).OrderBy(p => p).SequenceEqual(existingFolders), "pre-cancelled export creates no output");
            }

            bool missing = false;
            try
            {
                await MaterialExportService.ExportAsync(destination, new[] { new MaterialExportSource("missing", "missing note", MaterialType.Text,
                    Path.Combine(originals, "missing.txt"), "fallback must not silently succeed") });
            }
            catch (FileNotFoundException) { missing = true; }
            Check(missing && Directory.GetDirectories(destination).OrderBy(p => p).SequenceEqual(existingFolders), "missing referenced text file fails instead of exporting its truncated preview");

            using (var locked = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                bool failed = false;
                try { await MaterialExportService.ExportAsync(destination, snapshot.Take(2).ToArray()); }
                catch (IOException) { failed = true; }
                Check(failed && Directory.GetDirectories(destination).OrderBy(p => p).SequenceEqual(existingFolders), "failure after an earlier successful copy removes partial output and preserves existing exports");
            }
            bool sourceProtected = false;
            var protectionReport = new DirectProgress(p =>
            {
                if (p.CopiedBytes > 0 && !sourceProtected)
                {
                    try { using var writer = new FileStream(source, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); }
                    catch (IOException) { sourceProtected = true; }
                }
            });
            await MaterialExportService.ExportAsync(destination, snapshot.Take(1).ToArray(), protectionReport);
            Check(sourceProtected, "source file is protected against concurrent writes during copying");
            Check(SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(SHA256.HashData(sourceBytes))
                && File.ReadAllText(second) == "distinct same-name source" && File.ReadAllText(textFile) == fullText, "success, cancellation and failures leave source content unchanged");
            Check(File.ReadAllText(keep) == "keep parent untouched" && Directory.GetFiles(destination).Length == 1, "parent folder and its original file survive all cleanup paths");
            var empty = await MaterialExportService.ExportAsync(destination,
                new[] { new MaterialExportSource("empty", "空白文字", MaterialType.Text, "", "") });
            Check(empty.ItemCount == 1 && empty.CopiedBytes == 0 && new FileInfo(Path.Combine(empty.DirectoryPath, "空白文字.txt")).Length == 0,
                "empty text is exported as a valid zero-byte file");
            var noDestination = Path.Combine(root, "not-selected-folder");
            bool invalidDestination = false;
            try { await MaterialExportService.ExportAsync(noDestination, snapshot); }
            catch (DirectoryNotFoundException) { invalidDestination = true; }
            Check(invalidDestination && !Directory.Exists(noDestination), "invalid destination does not silently create or alter the requested parent");
            existingFolders = Directory.GetDirectories(destination).OrderBy(p => p).ToArray();
            using (var cancellation = new CancellationTokenSource())
            {
                bool stopped = false;
                try
                {
                    await MaterialExportService.ExportAsync(destination, snapshot.Take(1).ToArray(), new DirectProgress(p =>
                    {
                        if (p.CompletedItems == p.TotalItems) cancellation.Cancel();
                    }), cancellation.Token);
                }
                catch (OperationCanceledException) { stopped = true; }
                Check(stopped && Directory.GetDirectories(destination).OrderBy(p => p).SequenceEqual(existingFolders), "cancellation after final progress still cleans output and never reports success");
            }
            string? injected = null;
            bool progressFailed = false;
            try
            {
                await MaterialExportService.ExportAsync(destination, snapshot.Take(1).ToArray(), new DirectProgress(p =>
                {
                    if (p.CopiedBytes == 0) return;
                    var child = Directory.GetDirectories(destination).Single(p => !existingFolders.Contains(p));
                    injected = Path.Combine(child, "externally-created.txt");
                    File.WriteAllText(injected, "must survive partial-export cleanup");
                    throw new InvalidOperationException("intentional progress callback failure");
                }));
            }
            catch (InvalidOperationException) { progressFailed = true; }
            Check(progressFailed && injected != null && File.ReadAllText(injected) == "must survive partial-export cleanup"
                && Directory.GetFiles(Path.GetDirectoryName(injected)!).Length == 1, "cleanup removes its own partial files but never recursively deletes externally added content");
            var largeSource = Path.Combine(originals, "大文件.bin");
            await using (var largeFile = new FileStream(largeSource, FileMode.CreateNew, FileAccess.Write))
                for (int block = 0; block < 32; block++) await largeFile.WriteAsync(sourceBytes);
            var largeReports = new List<MaterialExportProgress>();
            var elapsed = Stopwatch.StartNew();
            var largeExport = await MaterialExportService.ExportAsync(destination,
                new[] { new MaterialExportSource("large", "大文件", MaterialType.Other, largeSource, "") }, new DirectProgress(largeReports.Add));
            elapsed.Stop();
            // 宽松按实际运行时长给上界，快盘复制不应为每个 80 KB 缓冲区排一个 UI 回调。
            var reportUpperBound = 3 + (int)Math.Ceiling(elapsed.Elapsed.TotalMilliseconds / 100);
            Check(largeReports.Count <= reportUpperBound, "large fast copy bounds callback volume by elapsed time instead of buffer count");
            Check(largeReports[0].CopiedBytes == 0 && largeReports[0].CompletedItems == 0
                && largeReports.First(p => p.CopiedBytes > 0).CopiedBytes <= 81920, "throttling retains item start and immediate first-byte feedback");
            Check(largeReports[^1].CompletedItems == 1 && largeReports[^1].CopiedBytes == largeExport.CopiedBytes
                && largeReports[^1].TotalBytes == new FileInfo(largeSource).Length, "throttling retains final exact item and byte totals");
            Console.WriteLine($"64 MB export: {largeReports.Count} progress callbacks in {elapsed.Elapsed.TotalMilliseconds:F0} ms.");
            Console.WriteLine($"{_checks} checks passed; all fixtures are temporary and App was never started.");
        }
        finally
        {
            // 自己创建且验证限定在临时目录的测试夹具；不触碰真实素材库。
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(root).StartsWith("liuzhuan-material-export-check-", StringComparison.Ordinal)) Directory.Delete(root, true);
        }
    }
}
