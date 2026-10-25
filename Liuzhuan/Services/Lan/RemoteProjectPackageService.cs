using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Liuzhuan.Models;

namespace Liuzhuan.Services.Lan;

/// <summary>Authenticated library sub-actions start bounded jobs; expensive ZIP work never holds a WS reply.</summary>
public sealed class RemoteProjectPackageService
{
    private readonly DataStore _store;
    private readonly Func<Action,Task> _dispatch;
    private readonly Func<string> _libraryId;
    private readonly object _gate=new();
    private readonly Dictionary<string,Job> _jobs=new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _serial=new(1);
    private sealed class Job
    {
        public string Id="",Fingerprint="",Operation="",Library="",Root="",State="queued",Stage="等待电脑处理",Error="";
        public string[] Ids=Array.Empty<string>();public string ItemId="",Token="";
        public DateTime Created=DateTime.UtcNow;
        public object? Result;
        public ProjectPackagePrepared? Prepared;
        public string SourcePath="",SourceItem="";
        public long Sequence,SourceRevision;
        public string PreviewToken="";
    }
    private sealed class Stale:Exception { }
    public RemoteProjectPackageService(DataStore store,Func<Action,Task> dispatch,Func<string> libraryId)
    {_store=store;_dispatch=dispatch;_libraryId=libraryId;}
    private static string Text(JsonElement request,string key)=>request.TryGetProperty(key,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString()??"":"";
    public Task<object> HandleAsync(JsonElement request)
    {
        var library=Text(request,"libraryId");if(library.Length==0||library!=_libraryId())return Task.FromResult<object>(new{error="素材库已变化，请重新连接。",code="target_changed"});
        var action=Text(request,"action");
        lock(_gate) {
            foreach(var old in _jobs.Values.Where(x=>(x.State is "ready" or "failed") && DateTime.UtcNow-x.Created>TimeSpan.FromMinutes(15)).Select(x=>x.Id).ToArray())_jobs.Remove(old);
            if(action=="project_status") {
                var id=Text(request,"jobId");if(!_jobs.TryGetValue(id,out var found)||found.Library!=library)return Task.FromResult<object>(new{error="项目任务已过期，请重新开始。",code="expired"});
                return Task.FromResult(Status(found));
            }
            if(action!="project_begin")throw new ArgumentException("项目操作无效。");
            var operation=Text(request,"operation");var operationId=Text(request,"operationId");
            if(operation is not ("export" or "preview" or "apply") || !Guid.TryParseExact(operationId,"D",out _))throw new ArgumentException("项目任务编号或操作无效。");
            var ids=request.TryGetProperty("ids",out var selected)&&selected.ValueKind==JsonValueKind.Array?selected.EnumerateArray().Select(x=>x.GetString()??"").ToArray():Array.Empty<string>();
            if(ids.Length>100 || ids.Any(x=>x.Length is <1 or >256) || ids.Distinct(StringComparer.Ordinal).Count()!=ids.Length || operation=="export"&&ids.Length==0)throw new ArgumentException("每次请选择1–100项不同素材。");
            var itemId=Text(request,"itemId");var token=Text(request,"token");
            if(operation=="preview" && (itemId.Length is <1 or >256) || operation=="apply"&&!Guid.TryParseExact(token,"D",out _))throw new ArgumentException("请选择电脑库中的项目ZIP并先预览。");
            var fingerprint=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{operation,ids,itemId,token}))));
            if(_jobs.TryGetValue(operationId,out var existing)) {
                if(existing.Library!=library||existing.Fingerprint!=fingerprint)throw new InvalidOperationException("相同任务编号的内容不一致，请重新开始。");
                return Task.FromResult(Status(existing));
            }
            if(_jobs.Count>=4)throw new InvalidOperationException("项目任务最多保留4项，请等待完成；15分钟后可开始新的任务。");
            var job=new Job{Id=operationId,Fingerprint=fingerprint,Operation=operation,Library=library,Root=Path.GetFullPath(_store.DataRoot),Ids=ids,ItemId=itemId,Token=token};
            _jobs.Add(job.Id,job);_=Task.Run(()=>RunAsync(job));
            return Task.FromResult(Status(job));
        }
    }
    private object Status(Job job)=>new{jobId=job.Id,operation=job.Operation,state=job.State,stage=job.Stage,error=job.Error,result=job.Result,libraryId=job.Library};
    private void Verify(Job job)
    {
        if(_libraryId()!=job.Library || _store.LoadFailed || !Same(_store.DataRoot,job.Root) || !Same(ConfigService.GetEffectiveDataDir(),job.Root))throw new Stale();
    }
    private static bool Same(string a,string b)=>Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),StringComparison.OrdinalIgnoreCase);
    private async Task RunAsync(Job job)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(15));var token=deadline.Token;
        try {
            await _serial.WaitAsync(token);
            try {
                lock(_gate){job.State="running";job.Stage="电脑正在校验项目素材";}
                await _dispatch(()=>Verify(job));
                var progress=new Progress<ProjectPackageProgress>(value=>{lock(_gate){if(job.State=="running")job.Stage=$"{value.Stage} · {value.CompletedItems}/{value.TotalItems}";}});
                if(job.Operation=="export")await ExportAsync(job,progress,token);
                else if(job.Operation=="preview")await PreviewAsync(job,progress,token);
                else await ApplyAsync(job,progress,token);
                lock(_gate){job.State="ready";job.Stage=job.Operation=="export"?"项目包已保存到电脑，可下载":job.Operation=="preview"?"已完成校验，等待你确认合入":"项目内容已合入并保存";}
            }finally{_serial.Release();}
        }catch(Exception ex){lock(_gate){job.State="failed";job.Stage="未完成，原件与准备文件保留";job.Error=ex is Stale?"素材库或原件已变化，请刷新后重新开始。":ex is OperationCanceledException?"项目准备超过15分钟，已停止；当前库未自动回滚，请在电脑核对后重试。":"项目包未能完成，请在电脑检查素材和存储后重新预览或重试。";}}
    }
    private async Task ExportAsync(Job job,IProgress<ProjectPackageProgress> progress,CancellationToken token)
    {
        ProjectPackageSnapshot? snapshot=null;
        await _dispatch(()=>{Verify(job);var ids=job.Ids.ToHashSet(StringComparer.Ordinal);var rows=_store.Items.Where(x=>ids.Contains(x.Id)).ToArray();if(rows.Length!=ids.Count)throw new Stale();snapshot=ProjectPackageService.Snapshot(rows);});
        var directory=Path.Combine(job.Root,"temp");Directory.CreateDirectory(directory);
        var path=Path.Combine(directory,"project-package-"+job.Id+".liuzhuan-project.zip");
        var preview=await ProjectPackageService.ExportAsync(path,snapshot!,progress,token);token.ThrowIfCancellationRequested();
        MaterialItem? added=null;
        await _dispatch(()=>{Verify(job);added=_store.Add(new MaterialItem{Type=MaterialType.Other,FilePath=path,DisplayName=$"流转项目-{DateTime.Now:yyyyMMdd-HHmm}.liuzhuan-project.zip",Size=new FileInfo(path).Length,CollectionSource="ProjectPackage",CollectionDevice="手机项目打包"});});
        await _store.FlushAsync();
        lock(_gate)job.Result=new{itemId=added!.Id,name=added.DisplayName,count=preview.ItemCount,files=preview.FileCount,bytes=added.Size};
    }
    private async Task PreviewAsync(Job job,IProgress<ProjectPackageProgress> progress,CancellationToken token)
    {
        await _dispatch(()=>{Verify(job);var item=_store.Items.FirstOrDefault(x=>x.Id==job.ItemId);if(item==null||!item.HasFileSource||!Path.GetExtension(item.FilePath).Equals(".zip",StringComparison.OrdinalIgnoreCase))throw new Stale();job.SourcePath=item.FilePath;job.SourceItem=item.Id;job.SourceRevision=item.SourceRevision;});
        var prepared=await ProjectPackageService.PrepareImportAsync(job.SourcePath,job.Root,progress,token);token.ThrowIfCancellationRequested();
        ProjectImportBatchResult? counts=null;
        using var lease=ProjectStagingService.AcquirePreparationLease(job.Root);
        await ProjectPackageService.ValidatePreparedAsync(prepared,cancellationToken:token);
        await _dispatch(()=>{VerifySource(job);job.Sequence=_store.CurrentSequence;counts=_store.PreviewProjectBatch(prepared.CreateMaterials(),job.Sequence,job.Root);});
        lock(_gate){job.Prepared=prepared;job.PreviewToken=Guid.NewGuid().ToString("D");job.Result=new{token=job.PreviewToken,sequence=job.Sequence,count=prepared.Preview.ItemCount,files=prepared.Preview.FileCount,bytes=prepared.Preview.PayloadBytes,added=counts!.Added,already=counts.AlreadyImported,duplicateText=counts.DuplicateText,names=prepared.Preview.Entries.Take(30).Select(x=>x.Name).ToArray()};}
    }
    private void VerifySource(Job job)
    {
        Verify(job);var item=_store.Items.FirstOrDefault(x=>x.Id==job.SourceItem);
        if(item==null||!item.HasFileSource||item.SourceRevision!=job.SourceRevision||!Same(item.FilePath,job.SourcePath))throw new Stale();
    }
    private async Task ApplyAsync(Job job,IProgress<ProjectPackageProgress> progress,CancellationToken token)
    {
        Job source;
        lock(_gate)source=_jobs.Values.FirstOrDefault(x=>x.Library==job.Library && x.State=="ready" && x.PreviewToken==job.Token && x.Prepared!=null)??throw new Stale();
        await _dispatch(()=>{VerifySource(source);Verify(job);if(_store.CurrentSequence!=source.Sequence)throw new Stale();});
        var prepared=await ProjectPackageService.PrepareImportAsync(source.SourcePath,job.Root,progress,token,source.Prepared!.Preview);token.ThrowIfCancellationRequested();
        using var lease=ProjectStagingService.AcquirePreparationLease(job.Root);
        await ProjectPackageService.ValidatePreparedAsync(prepared,cancellationToken:token);token.ThrowIfCancellationRequested();
        ProjectImportBatchResult? result=null;
        await _dispatch(()=>{Verify(job);VerifySource(source);var rows=prepared.CreateMaterials();_store.PreviewProjectBatch(rows,source.Sequence,job.Root);result=_store.ImportProjectBatch(rows,source.Sequence,job.Root);});
        // Once the owner committed memory, finish the durable flush even if the preparation deadline passes.
        await _store.FlushAsync();
        lock(_gate)job.Result=new{added=result!.Added,already=result.AlreadyImported,duplicateText=result.DuplicateText};
    }
}
