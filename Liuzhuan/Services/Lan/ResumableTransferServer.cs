using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Liuzhuan.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Liuzhuan.Services.Lan;

/// <summary>Liuzhuan chunk protocol v1, inspired by tus offset/checksum semantics (not a tus endpoint).</summary>
public sealed class ResumableTransferServer : IDisposable
{
    public sealed record Manifest(string Id,string Owner,string Direction,string Name,long Size,string Sha256,
        string Path,string ItemId="",long Offset=0,string State="active",long Modified=0,DateTime Updated=default,string Origin="Android",string Device="手机",long ControlRevision=0,string ControlSource="",long ClientSequence=0,long SourceRevision=0);
    public sealed record CreateRequest(string Direction,string Name,long Size,string Sha256,string ItemId="",string Origin="",string Device="手机",long SourceRevision=0);
    private sealed class GateNode {public readonly SemaphoreSlim Gate=new(1);public int Users;}
    private sealed record GateLease(SemaphoreSlim Gate,Action Return):IDisposable {public void Dispose()=>Return();}
    private readonly Dictionary<string,GateNode> _locks=new(StringComparer.OrdinalIgnoreCase);
    private readonly object _registry=new();
    private GateLease RentGate(string key)
    {
        lock(_registry) {
            if(!_locks.TryGetValue(key,out var node))_locks[key]=node=new();
            node.Users++;
            return new(node.Gate,()=>{lock(_registry){if(--node.Users==0){_locks.Remove(key);node.Gate.Dispose();}}});
        }
    }
    private readonly ConcurrentDictionary<string,(long Offset,long At)> _progress=new(StringComparer.OrdinalIgnoreCase);
    private sealed record CatalogEntry(DateTime Modified,long Revision);
    private readonly object _createGate=new();
    private readonly Dictionary<string,CatalogEntry> _records=new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reservations=new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _maintenance=new(1);
    private long _catalogRevision,_nextReconcile,_nextSweep,_nextPressure;
    private (DateTime Modified,string Key)? _expiryAfter;
    private long _catalogScans;
    internal long CatalogReconciliations=>Interlocked.Read(ref _catalogScans);
    private readonly Func<Stream,CancellationToken,ValueTask<byte[]>> _downloadHash;
    public ResumableTransferServer():this((input,token)=>SHA256.HashDataAsync(input,token)) { }
    // An internal seam lets isolated checks hold hashing without replacing protocol or filesystem IO.
    internal ResumableTransferServer(Func<Stream,CancellationToken,ValueTask<byte[]>> downloadHash)=>_downloadHash=downloadHash;
    private readonly JsonSerializerOptions _json=new(){PropertyNameCaseInsensitive=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
    private WebApplication? _app;
    private string _root="";
    private DurableInbox? _inbox;
    public Action<string,string,string,long>? ControlChanged {get;set;}
    public bool Running=>_app!=null;
    public Func<string,MaterialItem?>? ItemLookup {get;set;}
    public Func<string,string,string,string,string,Task>? FileReceiver {get;set;}
    public string ServerId {get;private set;}="";
    private int SlotsUsed()=>_records.Count+_reservations.Count(key=>!_records.ContainsKey(key));
    private bool Reserve(string key)
    {
        lock(_createGate) {
            if(_reservations.Contains(key))throw new InvalidOperationException("Task creation already reserved");
            if(!_records.ContainsKey(key) && SlotsUsed()>=1000)return false;
            _reservations.Add(key);return true;
        }
    }
    private void ReleaseReservation(string key){lock(_createGate)_reservations.Remove(key);}
    private void RecordSaved(string key,DateTime modified)
    {
        lock(_createGate){_records[key]=new(modified,++_catalogRevision);_reservations.Remove(key);}
    }
    private void ReconcileCatalog(CancellationToken token)
    {
        long revision;lock(_createGate)revision=_catalogRevision;
        var observed=new Dictionary<string,DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach(var path in Directory.EnumerateFiles(_root,"*.json")) {
            token.ThrowIfCancellationRequested();
            if(System.IO.Path.GetFileName(path).Equals("server-id.json",StringComparison.OrdinalIgnoreCase))continue;
            var file=new FileInfo(path);file.Refresh();if(file.Exists)observed[System.IO.Path.GetFileNameWithoutExtension(path)]=file.LastWriteTimeUtc;
        }
        lock(_createGate) {
            // A save after enumeration began is newer authority than this disk observation.
            foreach(var key in _records.Where(x=>x.Value.Revision<=revision && !observed.ContainsKey(x.Key)).Select(x=>x.Key).ToArray())_records.Remove(key);
            foreach(var item in observed)if(!_records.TryGetValue(item.Key,out var current) || current.Revision<=revision)
                _records[item.Key]=new(item.Value,++_catalogRevision);
            Interlocked.Increment(ref _catalogScans);
        }
    }
    private bool ValidRecord(string key,Manifest m)=>Guid.TryParse(m.Id,out _) && Guid.TryParse(m.Owner,out _) &&
        Key(m.Owner,m.Id).Equals(key,StringComparison.OrdinalIgnoreCase) && m.Direction is "upload" or "download" &&
        m.Size>=0 && m.Offset>=0 && m.Offset<=m.Size && m.Path!=null && m.Name!=null && m.Sha256!=null && ValidHash(m.Sha256) &&
        m.State is "active" or "paused" or "stored" or "cancelled" or "expired";
    private void SweepExpired(CancellationToken token)
    {
        var cutoff=DateTime.UtcNow.AddDays(-8);
        string[] candidates;
        lock(_createGate) {
            var ordered=_records.Where(x=>x.Value.Modified<cutoff).OrderBy(x=>x.Value.Modified).ThenBy(x=>x.Key,StringComparer.OrdinalIgnoreCase).ToArray();
            var page=ordered.Where(x=>_expiryAfter==null || x.Value.Modified>_expiryAfter.Value.Modified ||
                x.Value.Modified==_expiryAfter.Value.Modified && StringComparer.OrdinalIgnoreCase.Compare(x.Key,_expiryAfter.Value.Key)>0).Take(128).ToArray();
            if(page.Length==0)page=ordered.Take(128).ToArray();
            if(page.Length>0)_expiryAfter=(page[^1].Value.Modified,page[^1].Key);
            candidates=page.Select(x=>x.Key).ToArray();
        }
        foreach(var key in candidates) {
            token.ThrowIfCancellationRequested();using var lease=RentGate(key);
            if(!lease.Gate.Wait(0))continue; // An in-flight read/hash/commit always wins over expiry.
            try {
                var path=RecordPath(key);var info=new FileInfo(path);info.Refresh();
                if(!info.Exists){lock(_createGate)_records.Remove(key);continue;}
                if(info.LastWriteTimeUtc>=cutoff){RecordSaved(key,info.LastWriteTimeUtc);continue;}
                if(info.Length>65536)continue;
                var expired=DurableJson.Read<Manifest>(path);
                if(expired==null || !ValidRecord(key,expired))continue; // Unknown/broken records occupy capacity until explicitly repaired.
                DeletePartial(key);File.Delete(path);
                lock(_createGate)_records.Remove(key);
                if(expired.State is "active" or "paused")Report(expired with{State="expired"});
                _progress.TryRemove(key,out _);
            }catch(Exception ex){Utils.Logger.Error("Expired transfer retained: {0}",ex.GetType().Name);}
            finally{lease.Gate.Release();}
        }
    }
    private async Task MaintainCatalogAsync(bool pressure,CancellationToken token)
    {
        var now=Environment.TickCount64;bool reconcile,sweep;
        lock(_createGate) {
            reconcile=now>=_nextReconcile || pressure && now>=_nextPressure;
            sweep=now>=_nextSweep || pressure && now>=_nextPressure;
        }
        if((!reconcile && !sweep) || !await _maintenance.WaitAsync(0,token))return;
        try {
            now=Environment.TickCount64;
            lock(_createGate){reconcile=now>=_nextReconcile || pressure && now>=_nextPressure;sweep=now>=_nextSweep || pressure && now>=_nextPressure;}
            if(reconcile)ReconcileCatalog(token);
            if(sweep)SweepExpired(token);
            lock(_createGate){if(reconcile){_nextReconcile=now+60_000;_nextPressure=now+1000;}if(sweep)_nextSweep=now+60_000;}
        }finally{_maintenance.Release();}
    }
    public static string LoadIdentity(string dataRoot)
    {
        var path=System.IO.Path.Combine(dataRoot,"transfers-v1","server-id.json");
        if(System.IO.File.Exists(path)) {
            var saved=DurableJson.Read<string>(path);
            if(!Guid.TryParseExact(saved,"D",out _))throw new InvalidDataException("素材库身份无法读取，原文件已保留。");
            return saved!;
        }
        var id=Guid.NewGuid().ToString("D");DurableJson.Write(path,id);return id;
    }
    public void Start(int port)
    {
        if(_app!=null)return;
        TransferPolicyService.EnsureLoaded();
        _root=System.IO.Path.Combine(ConfigService.GetEffectiveDataDir(),"transfers-v1");Directory.CreateDirectory(_root);
        ServerId=LoadIdentity(ConfigService.GetEffectiveDataDir());
        lock(_createGate){_records.Clear();_reservations.Clear();_catalogRevision=0;_catalogScans=0;_nextReconcile=0;_nextSweep=0;_nextPressure=0;_expiryAfter=null;}
        ReconcileCatalog(CancellationToken.None);
        lock(_createGate)_nextReconcile=Environment.TickCount64+60_000;
        _inbox=new DurableInbox(ConfigService.GetEffectiveDataDir());
        var builder=WebApplication.CreateBuilder(new WebApplicationOptions{Args=Array.Empty<string>(),ContentRootPath=AppContext.BaseDirectory});
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k=>{k.Listen(IPAddress.Any,port);k.Limits.MaxConcurrentConnections=8;k.Limits.MaxRequestBodySize=1048576;k.Limits.RequestHeadersTimeout=TimeSpan.FromSeconds(10);});
        var app=builder.Build();app.Run(HandleAsync);
        app.StartAsync().GetAwaiter().GetResult();_app=app;
        var loadedRoot=_root;
        _=Task.Run(()=>{foreach(var file in Directory.EnumerateFiles(loadedRoot,"*.json"))try {
            if(System.IO.Path.GetFileName(file)=="server-id.json")continue;
            // Serialize restoration with live requests so an older snapshot cannot replace newer progress.
            var key=System.IO.Path.GetFileNameWithoutExtension(file);using var lease=RentGate(key);var gate=lease.Gate;gate.Wait();
            try {if(new FileInfo(file).Length>65536)continue;var m=DurableJson.Read<Manifest>(file);if(m!=null && ValidRecord(key,m))Report(m);}finally{gate.Release();}
        } catch { /* An unreadable manifest is never overwritten or blindly resumed. */ }});
    }
    private string Key(string owner,string id)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(owner+":"+id))).ToLowerInvariant();
    private string RecordPath(string key)=>System.IO.Path.Combine(_root,key+".json");
    private void Save(string key,Manifest m){var now=DateTime.UtcNow;DurableJson.Write(RecordPath(key),m with{Updated=now});RecordSaved(key,now);Report(m);}
    private void Report(Manifest m)
    {
        var key=Key(m.Owner,m.Id);var journalId="resume:"+key;
        var at=Environment.TickCount64;_progress.TryGetValue(key,out var previous);
        var live=m.State=="active" && previous.At!=0 && m.Offset>previous.Offset;
        if(live && at-previous.At<200 && m.Offset<m.Size)return;
        var speed=live?(m.Offset-previous.Offset)*1000.0/Math.Max(1,at-previous.At):0;
        _progress[key]=(m.Offset,at);
        if(m.State is "active" or "paused")TransferJournal.RegisterPersistentControl(journalId,
            ()=>ChangeState(key,m.State=="paused"?"active":"paused"),()=>ChangeState(key,"cancelled"));
        else TransferJournal.EndPersistentControl(journalId);
        TransferJournal.Report(new(journalId,m.Name,m.Direction=="upload"?"手机 → 电脑":"电脑 → 手机",m.Offset,m.Size,
            m.State switch {"stored"=>m.Direction=="upload"?"已接收":"已发送","cancelled"=>"已取消","paused"=>"已暂停","expired"=>"已过期",_=>live?"传输中":"等待续传"},m.State=="expired"?"任务超过保留期，请检查对端后重新发送":"",speed,
            m.State=="stored" && m.Direction=="upload"?m.Path:"",m.State is "active" or "paused",m.State=="paused"));
    }
    private void ChangeState(string key,string state)=>_ = Task.Run(async()=>{
        using var lease=RentGate(key);var gate=lease.Gate;await gate.WaitAsync();
        try {var m=DurableJson.Read<Manifest>(RecordPath(key));if(m==null || m.State is "stored" or "cancelled")return;
            m=m with{State=state,ControlRevision=m.ControlRevision+1,ControlSource="desktop"};Save(key,m);if(state=="cancelled" && m.Direction=="upload")DeletePartial(key);
            ControlChanged?.Invoke(m.Owner,m.Id,state,m.ControlRevision);
        }catch(Exception ex){Utils.Logger.Error("Transfer control failed: {0}",ex.GetType().Name);}finally{gate.Release();}
    });
    private void DeletePartial(string key){var path=System.IO.Path.Combine(_root,key+".part");if(File.Exists(path))File.Delete(path);}
    private static string DiskName(string name)
    {
        var invalid=System.IO.Path.GetInvalidFileNameChars();
        name=new string(name.Select(c=>invalid.Contains(c)?'_':c).ToArray()).TrimEnd(' ','.');
        if(name.Length==0)return "upload.bin";
        var extension=System.IO.Path.GetExtension(name);
        if(extension.Length>32)extension="";
        return name.Length<=120?name:name[..(120-extension.Length)]+extension;
    }
    private static bool ValidHash(string s)=>s.Length==64 && s.All(Uri.IsHexDigit);
    private async Task HandleAsync(HttpContext c)
    {
        try {
            if(c.Request.Headers["X-Liuzhuan-Auth"].ToString()!=LanConfig.PasswordHash){c.Response.StatusCode=403;return;}
            var owner=c.Request.Headers["X-Liuzhuan-Client"].ToString();
            var parts=c.Request.Path.Value?.Split('/',StringSplitOptions.RemoveEmptyEntries)??Array.Empty<string>();
            if(!Guid.TryParse(owner,out _) || parts.Length!=3 || parts[0]!="v1" || parts[1]!="tasks" || !Guid.TryParse(parts[2],out _)){c.Response.StatusCode=400;return;}
            var id=parts[2];var key=Key(owner,id);
            using var lease=RentGate(key);var gate=lease.Gate;await gate.WaitAsync(c.RequestAborted);
            try {
                var path=RecordPath(key);
                if(new FileInfo(path) is {Exists:true,Length:>65536})throw new InvalidDataException("任务恢复记录过大，原文件已保留");
                var m=DurableJson.Read<Manifest>(path);var op=c.Request.Query["op"].ToString();
                if((m==null && File.Exists(path)) || (m!=null && !ValidRecord(key,m)))throw new InvalidDataException("任务恢复记录无效，原文件已保留");
                if(c.Request.Method=="POST" && op=="create") {
                    var req=await JsonSerializer.DeserializeAsync<CreateRequest>(c.Request.Body,_json,c.RequestAborted)??throw new ArgumentException();
                    if(req.Direction is not ("upload" or "download") || req.Name.Length>240 || req.Name.Any(char.IsControl) || req.Size<0 || req.Size>1099511627776L)throw new ArgumentException();
                    if(m!=null) {
                        if(m.Direction!=req.Direction || (req.Direction=="upload" && (m.Size!=req.Size || !string.Equals(m.Sha256,req.Sha256,StringComparison.OrdinalIgnoreCase))) || (req.Direction=="download" && m.ItemId!=req.ItemId)){c.Response.StatusCode=409;return;}
                        if(req.Direction=="download" && req.SourceRevision>0 && m.SourceRevision!=req.SourceRevision){c.Response.StatusCode=412;return;}
                    }else {
                        if(req.Direction=="upload" && !ValidHash(req.Sha256))throw new ArgumentException();
                        await MaintainCatalogAsync(false,c.RequestAborted);
                        if(!Reserve(key)) {
                            await MaintainCatalogAsync(true,c.RequestAborted);
                            if(!Reserve(key)){c.Response.StatusCode=429;return;}
                        }
                        try {
                            if(req.Direction=="upload") {
                                var name=System.IO.Path.GetFileName(req.Name.Replace('\\','/'));if(string.IsNullOrWhiteSpace(name))name="upload.bin";
                                var final=System.IO.Path.Combine(ConfigService.GetEffectiveDataDir(),"uploads",key+"_"+DiskName(name));
                                m=new(id,owner,"upload",name,req.Size,req.Sha256.ToLowerInvariant(),final,Origin:req.Origin=="directory"?"Directory":"Android",Device:req.Device.Length<=64 && !req.Device.Any(char.IsControl)?req.Device:"手机");
                                c.RequestAborted.ThrowIfCancellationRequested();
                                if(req.Size==0)using(var empty=File.Create(System.IO.Path.Combine(_root,key+".part")))empty.Flush(true);
                                Save(key,m);
                            }else {
                                var item=ItemLookup?.Invoke(req.ItemId);if(item==null || !item.HasFileSource){c.Response.StatusCode=404;return;}
                                if(req.SourceRevision>0 && item.SourceRevision!=req.SourceRevision){c.Response.StatusCode=412;return;}
                                var sourceRevision=item.SourceRevision;
                                var source=System.IO.Path.GetFullPath(item.FilePath);var name=item.DisplayName;
                                var info=new FileInfo(source);if(!info.Exists){c.Response.StatusCode=404;return;}
                                var size=info.Length;var modified=info.LastWriteTimeUtc.Ticks;
                                // Keep the read handle open through commit; hashing never holds the global capacity lock.
                                await using var input=File.OpenRead(source);
                                var hash=Convert.ToHexString(await _downloadHash(input,c.RequestAborted)).ToLowerInvariant();
                                c.RequestAborted.ThrowIfCancellationRequested();
                                info.Refresh();var current=ItemLookup?.Invoke(req.ItemId);
                                if(!info.Exists || info.Length!=size || info.LastWriteTimeUtc.Ticks!=modified || current==null || !current.HasFileSource ||
                                    current.SourceRevision!=sourceRevision || !System.IO.Path.GetFullPath(current.FilePath).Equals(source,StringComparison.OrdinalIgnoreCase))
                                    throw new IOException("来源文件在检查时变化，请重试");
                                m=new(id,owner,"download",name,size,hash,source,req.ItemId,Modified:modified,SourceRevision:sourceRevision);
                                Save(key,m);
                            }
                        }finally{ReleaseReservation(key);}
                    }
                }
                if(m==null){c.Response.StatusCode=404;return;}
                if(c.Request.Method=="POST" && op is "pause" or "resume" or "cancel") {
                    if(m.State is not ("stored" or "cancelled")) {
                        if(!long.TryParse(c.Request.Headers["X-Control-Sequence"],out var sequence) || sequence<=0)throw new ArgumentException();
                        if(sequence>m.ClientSequence){m=m with{State=op=="pause"?"paused":op=="cancel"?"cancelled":"active",ControlRevision=m.ControlRevision+1,ControlSource="client",ClientSequence=sequence};Save(key,m);if(op=="cancel" && m.Direction=="upload")DeletePartial(key);}
                    }
                }else if(op is "chunk" or "complete" or "ack") {
                    if(m.State=="cancelled"){c.Response.StatusCode=410;return;}
                    if(m.State=="paused"){c.Response.StatusCode=423;return;}
                    if(op=="chunk") {
                        if(!long.TryParse(c.Request.Query["offset"],out var offset) || offset<0 || offset>m.Size)throw new ArgumentException();
                        if(m.Direction=="upload" && c.Request.Method=="PUT") {
                            if(offset!=m.Offset || m.State=="stored"){c.Response.StatusCode=409;return;}
                            var length=c.Request.ContentLength??-1;
                            if(length<=0 || length>1048576 || length>m.Size-offset)throw new ArgumentException();
                            using var admission=await TransferPolicyService.EnterPayloadAsync(false,c.RequestAborted);
                            var bytes=new byte[(int)length];
                            for(var consumed=0;consumed<bytes.Length;) {
                                var take=Math.Min(65536,bytes.Length-consumed);await TransferPolicyService.PayloadAsync(false,take,c.RequestAborted);
                                await c.Request.Body.ReadExactlyAsync(bytes.AsMemory(consumed,take),c.RequestAborted);consumed+=take;
                            }
                            var hash=Convert.ToHexString(SHA256.HashData(bytes));
                            if(!string.Equals(hash,c.Request.Headers["X-Chunk-SHA256"],StringComparison.OrdinalIgnoreCase)){c.Response.StatusCode=422;return;}
                            var partial=System.IO.Path.Combine(_root,key+".part");
                            if(new DriveInfo(System.IO.Path.GetPathRoot(_root)!).AvailableFreeSpace<length+1048576)throw new IOException("电脑磁盘空间不足");
                            using(var file=new FileStream(partial,FileMode.OpenOrCreate,FileAccess.Write,FileShare.None)) {
                                if(file.Length<m.Offset)throw new IOException("电脑断点文件缺失，请取消并重新发送");
                                file.SetLength(m.Offset);file.Position=m.Offset;file.Write(bytes);file.Flush(true);
                            }
                            m=m with{Offset=offset+length};Save(key,m);
                        }else if(m.Direction=="download" && c.Request.Method=="GET") {
                            var current=ItemLookup?.Invoke(m.ItemId);
                            if(m.SourceRevision>0 && (current==null || current.SourceRevision!=m.SourceRevision || !current.HasFileSource || !System.IO.Path.GetFullPath(current.FilePath).Equals(m.Path,StringComparison.OrdinalIgnoreCase))){c.Response.StatusCode=412;return;}
                            var info=new FileInfo(m.Path);if(!info.Exists || info.Length!=m.Size || info.LastWriteTimeUtc.Ticks!=m.Modified){c.Response.StatusCode=412;return;}
                            var count=(int)Math.Min(1048576,m.Size-offset);var bytes=new byte[count];
                            await using(var input=File.OpenRead(m.Path)){input.Position=offset;await input.ReadExactlyAsync(bytes,c.RequestAborted);}
                            info.Refresh();if(info.Length!=m.Size || info.LastWriteTimeUtc.Ticks!=m.Modified){c.Response.StatusCode=412;return;}
                            c.Response.Headers["X-Chunk-SHA256"]=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                            c.Response.ContentLength=count;
                            using var admission=await TransferPolicyService.EnterPayloadAsync(true,c.RequestAborted);
                            for(var sent=0;sent<count;) {
                                var take=Math.Min(65536,count-sent);await TransferPolicyService.PayloadAsync(true,take,c.RequestAborted);
                                await c.Response.Body.WriteAsync(bytes.AsMemory(sent,take),c.RequestAborted);sent+=take;
                            }
                            return;
                        }else{c.Response.StatusCode=405;return;}
                    }else if(op=="ack" && m.Direction=="download" && c.Request.Method=="POST") {
                        if(!long.TryParse(c.Request.Query["offset"],out var offset) || offset<m.Offset || offset>m.Size)throw new ArgumentException();
                        m=m with{Offset=offset};Save(key,m);
                    }else if(op=="complete" && c.Request.Method=="POST" && m.State!="stored") {
                        if(m.Direction=="upload") {
                            if(m.Offset!=m.Size){c.Response.StatusCode=409;return;}
                            var partial=System.IO.Path.Combine(_root,key+".part");
                            var source=File.Exists(partial)?partial:m.Path;
                            await using(var input=File.OpenRead(source)) {
                                if(input.Length!=m.Size || Convert.ToHexString(await SHA256.HashDataAsync(input,c.RequestAborted)).ToLowerInvariant()!=m.Sha256){c.Response.StatusCode=422;return;}
                            }
                            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(m.Path)!);
                            if(File.Exists(partial))File.Move(partial,m.Path,true);
                            if(FileReceiver==null)throw new IOException("素材登记服务尚未就绪");
                            await _inbox!.RegisterAsync("file:"+key,m.Sha256,receipt=>FileReceiver(receipt,m.Path,m.Name,m.Origin,m.Device));
                        }else {
                            if(m.Offset!=m.Size || c.Request.Headers["X-File-SHA256"]!=m.Sha256){c.Response.StatusCode=409;return;}
                        }
                        m=m with{State="stored",Offset=m.Size};Save(key,m);
                    }
                }
                c.Response.ContentType="application/json";
                await JsonSerializer.SerializeAsync(c.Response.Body,new{m.Id,m.Size,m.Sha256,m.Offset,m.State,m.Name,ServerId,m.ControlRevision,m.ControlSource,m.ClientSequence,m.SourceRevision},_json,c.RequestAborted);
            }finally{gate.Release();}
        }catch(ArgumentException){if(!c.Response.HasStarted)c.Response.StatusCode=400;}
        catch(OperationCanceledException){ }
        catch(Exception ex){Utils.Logger.Error("Resumable transfer failed: {0}",ex.GetType().Name);if(!c.Response.HasStarted)c.Response.StatusCode=503;}
    }
    public void Dispose()
    {
        var app=_app;_app=null;if(app==null)return;
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try {app.StopAsync(stop.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){ }
        finally {app.DisposeAsync().AsTask().GetAwaiter().GetResult();}
    }
}
