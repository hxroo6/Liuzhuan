using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Liuzhuan.Models;
using Liuzhuan.Utils;

namespace Liuzhuan.Services.Lan;

/// <summary>
/// 数据面 HTTP 文件下载服务器（端口 = 控制面 + 1，默认 8900）
/// 极简实现：TcpListener 手写 HTTP/1.1 GET（避开 HttpListener 的 http.sys URL ACL 权限坑）
/// GET /file/{id} → 200 文件流 / 404
/// </summary>
public class FileHttpServer : IDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    /// <summary>按 id 查素材（注入）</summary>
    public Func<string, MaterialItem?>? ItemLookup { get; set; }

    /// <summary>文件上传完成（文件路径, 显示名）— 由 MainWindow 建素材</summary>
    public event Action<string, string>? FileUploaded;

    public void Start(int port)
    {
        if (_listener != null) return;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoop(_listener, _cts.Token));
        Logger.Run("FileHttpServer: listening on port {0}", port);
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
            _listener = null;
            _cts = null;
            Logger.Run("FileHttpServer: stopped");
        }
        catch (Exception ex)
        {
            Logger.Error("FileHttpServer.Stop failed: {0}", ex.Message);
        }
    }

    private async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = Task.Run(() => HandleClient(client));
        }
    }

    private async Task HandleClient(TcpClient client)
    {
        try
        {
            using var stream = client.GetStream();
            var requestLine = await ReadLineAsync(stream, 8192);
            if (string.IsNullOrEmpty(requestLine)) return;

            var parts = requestLine.Split(' ');
            if (parts.Length < 2)
            {
                await WriteSimpleAsync(stream, 405, "Method Not Allowed");
                return;
            }

            var pathAndQuery = parts[1].TrimStart('/');
            var query = "";
            var qIdx = pathAndQuery.IndexOf('?');
            if (qIdx >= 0)
            {
                query = pathAndQuery[(qIdx + 1)..];
                pathAndQuery = pathAndQuery[..qIdx];
            }

            if (parts[0] == "POST" && pathAndQuery.StartsWith("upload", StringComparison.OrdinalIgnoreCase))
            {
                await HandleUpload(stream, query, pathAndQuery);
                return;
            }

            if (parts[0] != "GET")
            {
                await WriteSimpleAsync(stream, 405, "Method Not Allowed");
                return;
            }

            // 消费 GET 请求头后再关闭连接，避免有未读数据时 TCP RST 截断响应正文。
            for (int i = 0; i < 100; i++)
            {
                if (string.IsNullOrEmpty(await ReadLineAsync(stream,8192))) break;
                if (i == 99) { await WriteSimpleAsync(stream,400,"Bad Request"); return; }
            }

            var isThumbnail = pathAndQuery.StartsWith("thumbnail/", StringComparison.OrdinalIgnoreCase);
            var id = isThumbnail ? pathAndQuery.Substring(10) : pathAndQuery.StartsWith("file/", StringComparison.OrdinalIgnoreCase)
                ? pathAndQuery.Substring(5)
                : "";

            if (string.IsNullOrEmpty(id))
            {
                await WriteSimpleAsync(stream, 404, "Not Found");
                return;
            }

            // 下载鉴权：URL 必须带 auth=口令哈希（与 WS 握手一致）
            var auth = ParseQueryValue(query, "auth");
            if (!string.Equals(auth, LanConfig.PasswordHash, StringComparison.Ordinal))
            {
                await WriteSimpleAsync(stream, 403, "Forbidden");
                return;
            }

            var item = ItemLookup?.Invoke(id);
            if (item == null || !item.IsFile || !File.Exists(item.FilePath))
            {
                await WriteSimpleAsync(stream, 404, "Not Found");
                return;
            }

            if (isThumbnail)
            {
                if (item.Type != MaterialType.Image) { await WriteSimpleAsync(stream,404,"Not Found"); return; }
                try
                {
                    var thumbnail = await NetworkThumbnailService.CreateAsync(item.FilePath);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {thumbnail.Length}\r\nConnection: close\r\n\r\n"));
                    await stream.WriteAsync(thumbnail);
                }
                catch { await WriteSimpleAsync(stream,415,"Unsupported Media Type"); }
                return;
            }

            var fileLen = new FileInfo(item.FilePath).Length;
            var header = "HTTP/1.1 200 OK\r\n" +
                         "Content-Length: " + fileLen + "\r\n" +
                         "Content-Type: application/octet-stream\r\n" +
                         "Content-Disposition: attachment; filename*=UTF-8''" + Uri.EscapeDataString(Path.GetFileName(item.FilePath)) + "\r\n" +
                         "Connection: close\r\n\r\n";
            var headerBytes = Encoding.ASCII.GetBytes(header);
            await stream.WriteAsync(headerBytes);
            await using (var fs = File.OpenRead(item.FilePath))
            {
                await CopyTracked(fs, stream, fileLen, Guid.NewGuid().ToString("N"), item.DisplayName, "电脑 → 手机", "已发送");
            }
            Logger.Run("FileHttpServer: served {0} ({1} bytes)", item.DisplayName, fileLen);
        }
        catch (Exception ex)
        {
            Logger.Error("FileHttpServer client error: {0}", ex.Message);
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>处理手机上传：POST /upload?name=文件名&auth=口令哈希 → 存 data/uploads/ → 触发 FileUploaded</summary>
    private async Task HandleUpload(NetworkStream stream, string query, string fullPath)
    {
        // 上传鉴权：URL 必须带 auth=口令哈希（与 WS 握手一致）
        var auth = ParseQueryValue(query, "auth");
        if (!string.Equals(auth, LanConfig.PasswordHash, StringComparison.Ordinal))
        {
            await WriteSimpleAsync(stream, 403, "Forbidden");
            return;
        }

        // 读取请求头（直到空行），取 Content-Length
        var headers = new Dictionary<string, string>();
        while (true)
        {
            var line = await ReadLineAsync(stream, 8192);
            if (string.IsNullOrEmpty(line)) break;
            var idx = line.IndexOf(':');
            if (idx > 0) headers[line[..idx].Trim().ToLowerInvariant()] = line[(idx + 1)..].Trim();
        }

        if (!long.TryParse(headers.GetValueOrDefault("content-length"), out var length) || length <= 0)
        {
            await WriteSimpleAsync(stream, 400, "Bad Request");
            return;
        }

        // 解析 name 参数（URL 解码，防路径穿越）
        var name = ParseQueryValue(query, "name");
        if (string.IsNullOrWhiteSpace(name)) name = "upload.bin";
        name = Path.GetFileName(name); // 防路径穿越
        if (string.IsNullOrWhiteSpace(name)) name = "upload.bin";

        var uploadDir = Path.Combine(ConfigService.GetEffectiveDataDir(), "uploads");
        Directory.CreateDirectory(uploadDir);
        var savePath = Path.Combine(uploadDir, Guid.NewGuid().ToString("N") + "_" + name);

        var transferId = Guid.NewGuid().ToString("N");
        var partial = savePath + ".part";
        try
        {
            using (var fs = File.Create(partial))
            {
                await CopyTracked(stream, fs, length, transferId, name, "手机 → 电脑", "处理中");
            }
            File.Move(partial, savePath);
            FileUploaded?.Invoke(savePath, name);
            TransferJournal.Report(new(transferId,name,"手机 → 电脑",length,length,"已接收","",0));
        }
        catch (Exception ex)
        {
            if (File.Exists(partial)) File.Delete(partial);
            TransferJournal.Report(new(transferId,name,"手机 → 电脑",0,length,"失败",ex.Message,0));
            await WriteSimpleAsync(stream,400,"Incomplete or Failed Upload");
            return;
        }

        Logger.Run("FileHttpServer: upload saved {0} ({1} bytes)", savePath, length);

        // 响应
        var resp = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(resp));
    }

    private static async Task CopyTracked(Stream input, Stream output, long total, string id, string name, string direction, string finalState)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long done = 0, lastReport = -200;
        var buffer = new byte[65536];
        void Report(string state, string error = "") => TransferJournal.Report(new(id,name,direction,done,total,state,error,done / Math.Max(0.01,clock.Elapsed.TotalSeconds)));
        Report("传输中");
        try
        {
            while (done < total)
            {
                int n = await input.ReadAsync(buffer.AsMemory(0,(int)Math.Min(buffer.Length,total-done))).AsTask().WaitAsync(TimeSpan.FromSeconds(120));
                if (n == 0) throw new EndOfStreamException($"文件未收完整：{done}/{total} 字节");
                await output.WriteAsync(buffer.AsMemory(0,n)).AsTask().WaitAsync(TimeSpan.FromSeconds(120));
                done += n;
                if (clock.ElapsedMilliseconds-lastReport>=200) { Report("传输中");lastReport=clock.ElapsedMilliseconds; }
            }
            Report(finalState);
        }
        catch(Exception ex) { Report("失败",ex.Message);throw; }
    }

    private static async Task<string?> ReadLineAsync(NetworkStream stream, int maxLen)
    {
        var sb = new StringBuilder();
        var buf = new byte[1];
        while (sb.Length < maxLen)
        {
            var n = await stream.ReadAsync(buf, 0, 1);
            if (n == 0) break;
            var ch = (char)buf[0];
            if (ch == '\n') break;
            sb.Append(ch);
        }
        return sb.ToString().TrimEnd('\r');
    }

    private static async Task WriteSimpleAsync(Stream stream, int code, string reason)
    {
        var resp = "HTTP/1.1 " + code + " " + reason + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(resp));
    }

    /// <summary>解析 query 参数（key=value&key2=value2）</summary>
    private static string ParseQueryValue(string query, string key)
    {
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=');
            if (kv.Length == 2 && kv[0] == key)
                return Uri.UnescapeDataString(kv[1]);
        }
        return "";
    }

    public void Dispose()
    {
        Stop();
    }
}
