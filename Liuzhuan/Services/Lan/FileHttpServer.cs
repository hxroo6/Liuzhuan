using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Collections.Concurrent;
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
    private readonly object _lifecycle = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private readonly SemaphoreSlim _connections;
    private readonly TimeSpan _headerTimeout;
    public FileHttpServer(TimeSpan? requestHeaderTimeout=null, int maxConnections=16)
    {
        _headerTimeout=requestHeaderTimeout??TimeSpan.FromSeconds(10);
        if(_headerTimeout<=TimeSpan.Zero || _headerTimeout>TimeSpan.FromMinutes(1))throw new ArgumentOutOfRangeException(nameof(requestHeaderTimeout));
        if(maxConnections is <1 or >128)throw new ArgumentOutOfRangeException(nameof(maxConnections));
        _connections=new SemaphoreSlim(maxConnections,maxConnections);
    }

    /// <summary>按 id 查素材（注入）</summary>
    public Func<string, MaterialItem?>? ItemLookup { get; set; }

    /// <summary>文件上传完成（文件路径, 显示名）— 由 MainWindow 建素材</summary>
    public event Action<string, string>? FileUploaded;
    public event Action<string, string, string, string>? FileCollected;

    public void Start(int port)
    {
        lock(_lifecycle) {
        if (_listener != null) return;
        TransferPolicyService.EnsureLoaded();
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _cts = new CancellationTokenSource();
        var listener=_listener;var token=_cts.Token;
        _acceptLoop = Task.Run(() => AcceptLoop(listener, token));
        Logger.Run("FileHttpServer: listening on port {0}", port);
        }
    }

    public void Stop()
    {
        lock(_lifecycle) {
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
            foreach(var client in _clients.Keys)try{client.Dispose();}catch { }
            _listener = null;
            _cts?.Dispose();
            _cts = null;
            Logger.Run("FileHttpServer: stopped");
        }
        catch (Exception ex)
        {
            Logger.Error("FileHttpServer.Stop failed: {0}", ex.Message);
        }
        }
    }

    private async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch { break; }
            if(!_connections.Wait(0)){client.Dispose();continue;}
            _clients.TryAdd(client,0);
            // Stop can run between Accept and registration; close that last arrival too.
            if(ct.IsCancellationRequested){_clients.TryRemove(client,out _);client.Dispose();_connections.Release();break;}
            _ = HandleOwnedClient(client,ct);
        }
    }

    private async Task HandleOwnedClient(TcpClient client,CancellationToken lifetime)
    {
        try {await HandleClient(client,lifetime);}
        finally {_clients.TryRemove(client,out _);_connections.Release();}
    }

    private async Task HandleClient(TcpClient client,CancellationToken lifetime)
    {
        NetworkStream? stream=null;
        try
        {
            stream = client.GetStream();
            string? requestLine;
            Dictionary<string,string> headers;
            using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(lifetime)) {
                deadline.CancelAfter(_headerTimeout);
                using var close=deadline.Token.Register(client.Dispose);
                (requestLine,headers)=await ReadHeadersAsync(stream,deadline.Token);
            }
            lifetime.ThrowIfCancellationRequested();
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
                await HandleUpload(stream, query, headers,lifetime);
                return;
            }

            if (parts[0] != "GET")
            {
                await WriteSimpleAsync(stream, 405, "Method Not Allowed");
                return;
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
            if (item == null || !item.HasFileSource || !File.Exists(item.FilePath))
            {
                await WriteSimpleAsync(stream, 404, "Not Found");
                return;
            }

            if (isThumbnail)
            {
                if (item.Type != MaterialType.Image) { await WriteSimpleAsync(stream,404,"Not Found"); return; }
                try
                {
                    var thumbnail = await NetworkThumbnailService.CreateAsync(item.FilePath,item.SourceRevision);
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
                await CopyTracked(fs, stream, fileLen, Guid.NewGuid().ToString("N"), item.DisplayName, "电脑 → 手机", "已发送",lifetime);
            }
            Logger.Run("FileHttpServer: served {0} ({1} bytes)", item.DisplayName, fileLen);
        }
        catch (OperationCanceledException) { }
        catch (InvalidDataException)
        {
            try {if(stream!=null && !lifetime.IsCancellationRequested)await WriteSimpleAsync(stream,400,"Bad Request");}catch { }
        }
        catch (Exception ex)
        {
            Logger.Error("FileHttpServer client error: {0}", ex.Message);
        }
        finally
        {
            stream?.Dispose();
            client.Dispose();
        }
    }

    /// <summary>处理手机上传：POST /upload?name=文件名&auth=口令哈希 → 存 data/uploads/ → 触发 FileUploaded</summary>
    private async Task HandleUpload(NetworkStream stream, string query, Dictionary<string,string> headers,CancellationToken lifetime)
    {
        // 上传鉴权：URL 必须带 auth=口令哈希（与 WS 握手一致）
        var auth = ParseQueryValue(query, "auth");
        if (!string.Equals(auth, LanConfig.PasswordHash, StringComparison.Ordinal))
        {
            await WriteSimpleAsync(stream, 403, "Forbidden");
            return;
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
                await CopyTracked(stream, fs, length, transferId, name, "手机 → 电脑", "处理中",lifetime);
            }
            File.Move(partial, savePath);
            FileUploaded?.Invoke(savePath, name);
            var device=ParseQueryValue(query,"device");
            if(device.Length>64 || device.Any(char.IsControl))device="手机";
            FileCollected?.Invoke(savePath, name, ParseQueryValue(query, "origin") == "directory" ? "Directory" : "Android",string.IsNullOrWhiteSpace(device)?"手机":device);
            TransferJournal.Report(new(transferId,name,"手机 → 电脑",length,length,"已接收","",0,savePath));
        }
        catch (Exception ex)
        {
            if (File.Exists(partial)) File.Delete(partial);
            TransferJournal.Report(new(transferId,name,"手机 → 电脑",0,length,ex is OperationCanceledException?"已取消":"失败",ex is OperationCanceledException?"":ex.Message,0));
            await WriteSimpleAsync(stream,400,"Incomplete or Failed Upload");
            return;
        }

        Logger.Run("FileHttpServer: upload saved {0} ({1} bytes)", savePath, length);

        // 响应
        var resp = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(resp));
    }

    private static async Task CopyTracked(Stream input, Stream output, long total, string id, string name, string direction, string finalState,CancellationToken lifetime)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long done = 0, lastReport = -200;
        var buffer = new byte[65536];
        var remote=direction=="手机 → 电脑"?input:output;
        using var control=TransferJournal.RegisterControl(id,remote.Dispose,lifetime);
        void Report(string state, string error = "") => TransferJournal.Report(new(id,name,direction,done,total,state,error,done / Math.Max(0.01,clock.Elapsed.TotalSeconds)));
        Report("排队中");
        try
        {
            var sending=direction=="电脑 → 手机";
            while (done < total)
            {
                await control.WaitAsync();
                int n;
                if(sending)n=await input.ReadAsync(buffer.AsMemory(0,(int)Math.Min(buffer.Length,total-done)),control.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(120),control.Token);
                else {
                    using var admission=await TransferPolicyService.EnterPayloadAsync(false,control.Token);
                    n=await input.ReadAsync(buffer.AsMemory(0,(int)Math.Min(buffer.Length,total-done)),control.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(120),control.Token);
                    if(n>0)await TransferPolicyService.PayloadAsync(false,n,control.Token);
                }
                if (n == 0) throw new EndOfStreamException($"文件未收完整：{done}/{total} 字节");
                await control.WaitAsync();
                if(sending) {
                    using var admission=await TransferPolicyService.EnterPayloadAsync(true,control.Token);
                    await TransferPolicyService.PayloadAsync(true,n,control.Token);
                    await output.WriteAsync(buffer.AsMemory(0,n),control.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(120),control.Token);
                }else await output.WriteAsync(buffer.AsMemory(0,n),control.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(120),control.Token);
                done += n;
                if (clock.ElapsedMilliseconds-lastReport>=200) { Report("传输中");lastReport=clock.ElapsedMilliseconds; }
            }
            Report(finalState);
        }
        catch(Exception ex) {
            if(control.IsCancelled) { Report(direction=="手机 → 电脑"?"正在取消…":"已取消");throw new OperationCanceledException("传输已取消",ex); }
            if(ex is TimeoutException || control.IsPauseExpired) {const string message="暂停过久或连接超时，请在发送端重新传输";Report("失败",message);throw new TimeoutException(message,ex);}
            Report("失败",ex.Message);throw;
        }
        finally { TransferJournal.EndControl(id); }
    }

    private static async Task<(string? Request,Dictionary<string,string> Headers)> ReadHeadersAsync(NetworkStream stream,CancellationToken token)
    {
        const int totalBudget=65536;
        var request=await ReadLineAsync(stream,8192,token);
        var headers=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        if(request==null)return (null,headers);
        int consumed=request.Length+2;
        for(int count=0;count<=100;count++) {
            var line=await ReadLineAsync(stream,Math.Min(8192,totalBudget-consumed),token)??throw new EndOfStreamException("Incomplete HTTP headers");
            consumed+=line.Length+2;
            if(consumed>totalBudget)throw new InvalidDataException("HTTP headers too large");
            if(line.Length==0)return (request,headers);
            if(count==100)throw new InvalidDataException("Too many HTTP headers");
            var idx=line.IndexOf(':');
            if(idx<=0 || line[..idx].Any(c=>c<=32 || c>=127))throw new InvalidDataException("Invalid HTTP header");
            var name=line[..idx];var value=line[(idx+1)..].Trim();
            if(headers.TryGetValue(name,out var previous) && name.Equals("Content-Length",StringComparison.OrdinalIgnoreCase) && previous!=value)
                throw new InvalidDataException("Conflicting HTTP lengths");
            headers[name]=value;
        }
        throw new InvalidDataException("Incomplete HTTP headers");
    }

    private static async Task<string?> ReadLineAsync(NetworkStream stream, int maxLen,CancellationToken token)
    {
        var sb = new StringBuilder();
        var buf = new byte[1];
        if(maxLen<=0)throw new InvalidDataException("HTTP headers too large");
        while (sb.Length < maxLen)
        {
            var n = await stream.ReadAsync(buf.AsMemory(),token);
            if (n == 0) {if(sb.Length==0)return null;throw new EndOfStreamException("Incomplete HTTP line");}
            var ch = (char)buf[0];
            if (ch == '\n') {
                var line=sb.ToString();if(line.EndsWith('\r'))line=line[..^1];
                if(line.Any(c=>c<' ' && c!='\t'))throw new InvalidDataException("Invalid HTTP control character");
                return line;
            }
            sb.Append(ch);
        }
        throw new InvalidDataException("HTTP line too large");
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
