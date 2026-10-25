using System.Text.Json;
using Liuzhuan.Models;

namespace Liuzhuan.Services.Lan;

/// <summary>PC-authoritative metadata, bounded pages and optimistic edits. Captures only on the owner thread.</summary>
public sealed class MaterialLibraryService
{
    private readonly DataStore _store;
    private readonly Func<Action,Task> _dispatch;
    private readonly Func<string> _libraryId;
    private readonly LibraryChangeJournal _changes;
    private readonly RemoteProjectPackageService _projects;
    private readonly SemaphoreSlim _queries=new(1);
    private long _cachedSequence=-1;
    private int _cachedCount=-1;
    private LanItemSummary[] _rows=Array.Empty<LanItemSummary>();
    private string[] _groups=Array.Empty<string>(),_tags=Array.Empty<string>();
    private string _filter="";
    private LanItemSummary[] _filtered=Array.Empty<LanItemSummary>();
    public long SnapshotBuilds {get;private set;}
    public MaterialLibraryService(DataStore store,Func<Action,Task> dispatch,Func<string> libraryId)
        { _store=store;_dispatch=dispatch;_libraryId=libraryId;_changes=new(store);_projects=new(store,dispatch,libraryId); }
    public async Task<object> HandleAsync(JsonElement request)
    {
        string Text(string key,string fallback="") => request.TryGetProperty(key,out var p) && p.ValueKind==JsonValueKind.String ? p.GetString()??fallback : fallback;
        long Number(string key,long fallback=0) => request.TryGetProperty(key,out var p)&&p.TryGetInt64(out var n)?n:fallback;
        var library=_libraryId();
        if (Text("libraryId")!=library || string.IsNullOrEmpty(library)) return new { error="素材库已变化，请重新连接。", code="target_changed" };
        if(Text("action") is "project_begin" or "project_status")return await _projects.HandleAsync(request);
        if(Text("action")=="changes")
        {
            object? result=null;
            await _dispatch(()=>{
                if(_libraryId()!=library||_store.LoadFailed)throw new InvalidOperationException("素材库已变化或不可读取。");
                result=_changes.Read(library,Text("serverRun"),Number("since",-1),Number("until",-1),_store.CurrentSequence,_store.Items.Count);
            });
            return result!;
        }
        if(Text("action")=="query")
        {
            await _queries.WaitAsync();try {
            var offset=Number("offset"); var query=Text("search").Trim();var type=Text("type","All");var group=Text("group");var tag=Text("tag");
            var favorite=request.TryGetProperty("favorite",out var fav)&&fav.ValueKind==JsonValueKind.True;
            if(offset<0 || offset>int.MaxValue || query.Length>256 || group.Length>48 || tag.Length>32 || !new[]{"All","Text","Image","Video","Audio","Other"}.Contains(type))
                throw new ArgumentException("筛选条件无效。");
            bool rebuilt=false;long sequence=0;
            await _dispatch(()=>{sequence=_store.CurrentSequence;if(_cachedSequence!=sequence||_cachedCount!=_store.Items.Count){_rows=_store.GetRecentSummaries(int.MaxValue).ToArray();_cachedSequence=sequence;_cachedCount=_rows.Length;rebuilt=true;SnapshotBuilds++;}});
            if(rebuilt){_groups=_rows.Select(x=>x.Group).Where(x=>x.Length>0).Distinct().OrderBy(x=>x).Take(256).ToArray();_tags=_rows.SelectMany(x=>x.Tags).Distinct().OrderBy(x=>x).Take(256).ToArray();_filter="";}
            if(offset>0 && Number("sequence",-1)!=sequence) return new { stale=true, sequence, libraryId=library };
            var filter=JsonSerializer.Serialize(new{query,type,group,tag,favorite});
            if(_filter!=filter){_filtered=_rows.Where(x=>(type=="All" || x.Type==type) && (!favorite || x.Favorite) && (group.Length==0 || x.Group==group) && (tag.Length==0 || x.Tags.Contains(tag)) &&
                (query.Length==0 || new[]{x.Name,x.Group,x.Note,x.Source,x.SourceDevice,x.SearchText}.Concat(x.Tags).Any(s=>s.Contains(query,StringComparison.OrdinalIgnoreCase)))).ToArray();_filter=filter;}
            const int size=100;
            var page=_filtered.Skip((int)offset).Take(size).ToArray();
            var offline=request.TryGetProperty("includeText",out var include)&&include.ValueKind==JsonValueKind.True;
            object[] payload=offline?page.Select(x=>(object)new {x.Id,x.Type,x.Name,x.Size,x.AddedTime,x.Sequence,x.Group,x.Tags,x.Note,x.HasFile,x.Favorite,x.Revision,x.SourceMissing,x.Source,x.SourceDevice,x.SourceRevision,
                offlineText=!x.HasFile&&x.SearchText.Length<=16000?x.SearchText:"",textComplete=!x.HasFile&&x.SearchText.Length<=16000}).ToArray():page.Cast<object>().ToArray();
            return new {libraryId=library, serverRun=_changes.ServerRun,changesSupported=true,sequence, total=_filtered.Length, offset, nextOffset=Math.Min(offset+size,_filtered.Length),
                items=payload, groups=_groups, tags=_tags};
            }finally{_queries.Release();}
        }
        if(Text("action")=="merge_batch")return await MergeAsync(request,library);
        if(Text("action")!="edit")throw new ArgumentException("未知素材库操作。");
        var id=Text("id");var revision=Number("revision",-1);
        var patch=MaterialOrganization.Normalize(Text("group"), request.GetProperty("tags").EnumerateArray().Select(x=>x.GetString()??""),Text("note"));
        if(!request.TryGetProperty("favorite",out var flag) || flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw new ArgumentException("收藏状态无效。");
        object? response=null;bool changed=false;
        await _dispatch(()=>{
            if(_store.LoadFailed)throw new InvalidOperationException("素材索引不可读取，已停止修改。");
            var item=_store.Items.FirstOrDefault(x=>x.Id==id);
            if(item==null){response=new{error="素材已被移除，请刷新。",code="deleted"};return;}
            if(item.MetadataRevision!=revision){response=new{error="电脑或其他手机已修改此素材。请重新加载后再保存；草稿仍保留。",code="conflict",item=_store.GetSummary(item)};return;}
            _store.UpdateOrganization(new[]{id},patch);
            _store.SetFavorite(id,flag.GetBoolean());
            response=new{item=_store.GetSummary(item),libraryId=library};changed=true;
        });
        if(changed)await _store.FlushAsync();
        return response!;
    }

    private sealed record Metadata(bool Favorite,string Group,string[] Tags,string Note);
    private static Metadata ParseMetadata(JsonElement value) {
        var favorite=value.GetProperty("favorite").GetBoolean();
        var patch=MaterialOrganization.Normalize(value.GetProperty("group").GetString(),value.GetProperty("tags").EnumerateArray().Select(x=>x.GetString()??""),value.GetProperty("note").GetString());
        return new(favorite,patch.GroupName!,patch.Tags!,patch.Note!);
    }
    private async Task<object> MergeAsync(JsonElement request,string library) {
        var entries=request.GetProperty("edits").EnumerateArray().Select(x=>(Id:x.GetProperty("id").GetString()??"",Base:ParseMetadata(x.GetProperty("base")),Desired:ParseMetadata(x.GetProperty("desired")))).ToArray();
        if(entries.Length is <1 or >100 || entries.Any(x=>x.Id.Length is <1 or >256)||entries.Select(x=>x.Id).Distinct().Count()!=entries.Length)throw new ArgumentException("每批需选择1–100条不同素材。");
        // Validate the whole request before mutating anything; results explicitly report each item.
        var results=new List<object>();
        await _dispatch(()=>{
            if(_libraryId()!=library||_store.LoadFailed)throw new InvalidOperationException("素材库已变化或索引不可读取，已停止修改。");
            foreach(var entry in entries) {
                var item=_store.Items.FirstOrDefault(x=>x.Id==entry.Id);
                if(item==null){results.Add(new{id=entry.Id,code="deleted"});continue;}
                var current=new Metadata(item.IsFavorite,item.GroupName,item.Tags,item.Note);
                var conflicts=new List<string>();
                T Merge<T>(string field,T before,T desired,T now,Func<T,T,bool>? equal=null) {
                    equal??=EqualityComparer<T>.Default.Equals;
                    if(equal(before,desired))return now;
                    if(!equal(now,before)&&!equal(now,desired))conflicts.Add(field);
                    return desired;
                }
                bool TagsEqual(string[] a,string[] b)=>a.SequenceEqual(b,StringComparer.OrdinalIgnoreCase);
                var favorite=Merge("favorite",entry.Base.Favorite,entry.Desired.Favorite,current.Favorite);
                var group=Merge("group",entry.Base.Group,entry.Desired.Group,current.Group);
                var tags=Merge("tags",entry.Base.Tags,entry.Desired.Tags,current.Tags,TagsEqual);
                var note=Merge("note",entry.Base.Note,entry.Desired.Note,current.Note);
                if(conflicts.Count>0){results.Add(new{id=entry.Id,code="conflict",fields=conflicts,item=_store.GetSummary(item)});continue;}
                _store.UpdateOrganization(new[]{item.Id},MaterialOrganization.Normalize(group,tags,note));
                _store.SetFavorite(item.Id,favorite);
                results.Add(new{id=entry.Id,code="saved",item=_store.GetSummary(item)});
            }
        });
        // Also flush an idempotent retry: an earlier attempt may have mutated memory before IO failed.
        await _store.FlushAsync();
        return new{libraryId=library,results};
    }
}
