using System.Text.Json;

namespace Liuzhuan.Services.Lan;

/// <summary>Bounded owner-thread journal. An expired cursor always falls back to a full snapshot.</summary>
public sealed class LibraryChangeJournal
{
    public const int MaxEntries=4096;
    public const long MaxBytes=16*1024*1024;
    public string ServerRun {get;}=Guid.NewGuid().ToString("D");
    private sealed record Entry(long Sequence,string Kind,string Id,object? Item,int Bytes);
    private readonly Queue<Entry> _entries=new();
    private long _floor,_bytes;
    public LibraryChangeJournal(DataStore store)
    {
        _floor=store.CurrentSequence;
        store.ItemAdded+=x=>Append(store.CurrentSequence,"upsert",x.Id,OfflinePayload(store.GetSummary(x)));
        store.ItemUpdated+=x=>Append(store.CurrentSequence,"upsert",x.Id,OfflinePayload(store.GetSummary(x)));
        store.ItemRemoved+=id=>Append(store.CurrentSequence,"delete",id,null);
        store.ItemCleared+=()=>Append(store.CurrentSequence,"clear","",null);
    }
    public static object OfflinePayload(LanItemSummary x)=>new {x.Id,x.Type,x.Name,x.Size,x.AddedTime,x.Sequence,x.Group,x.Tags,x.Note,x.HasFile,x.Favorite,x.Revision,x.SourceMissing,x.Source,x.SourceDevice,x.SourceRevision,
        offlineText=!x.HasFile&&x.SearchText.Length<=16000?x.SearchText:"",textComplete=!x.HasFile&&x.SearchText.Length<=16000};
    private void Append(long sequence,string kind,string id,object? item)
    {
        var size=JsonSerializer.SerializeToUtf8Bytes(item).Length+id.Length*2+64;
        _entries.Enqueue(new(sequence,kind,id,item,size));_bytes+=size;
        while(_entries.Count>MaxEntries||_bytes>MaxBytes){var old=_entries.Dequeue();_bytes-=old.Bytes;_floor=old.Sequence;}
    }
    public object Read(string library,string run,long since,long until,long sequence,int total)
    {
        bool reset=run!=ServerRun||since<_floor||since<0||since>sequence||(until>=0&&until!=sequence);
        var pending=_entries.Where(x=>x.Sequence>since&&x.Sequence<=sequence).ToArray();
        long expected=since;
        foreach(var entry in pending){if(entry.Sequence!=++expected){reset=true;break;}}
        if(expected!=sequence)reset=true;
        if(reset)return new{libraryId=library,serverRun=ServerRun,requiresFull=true,sequence,total};
        var page=pending.Take(100).ToArray();
        return new{libraryId=library,serverRun=ServerRun,requiresFull=false,sequence,total,
            nextSequence=page.Length>0?page[^1].Sequence:sequence,hasMore=pending.Length>page.Length,
            changes=page.Select(x=>new{x.Sequence,x.Kind,x.Id,x.Item}).ToArray()};
    }
}
