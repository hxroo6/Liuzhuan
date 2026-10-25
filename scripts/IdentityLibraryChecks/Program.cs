using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Liuzhuan;
using Liuzhuan.Models;
using Liuzhuan.Services;
using Liuzhuan.Services.Lan;

static class Program
{
    static int checks;
    static void Check(bool ok,string label){if(!ok)throw new Exception(label);Console.WriteLine("PASS "+label);checks++;}
    static JsonElement Json(object data)=>JsonSerializer.SerializeToElement(data,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
    static async Task<JsonElement> Read(ClientWebSocket ws,string type,CancellationToken ct=default) {
        var buffer=new byte[1024*1024];using var bytes=new MemoryStream();
        while(true){var r=await ws.ReceiveAsync(new ArraySegment<byte>(buffer),ct);bytes.Write(buffer,0,r.Count);if(!r.EndOfMessage)continue;
            var value=JsonDocument.Parse(bytes.ToArray()).RootElement.Clone();bytes.SetLength(0);if(value.GetProperty("type").GetString()==type)return value.GetProperty("data");}
    }
    static Task Send(ClientWebSocket ws,object value)=>ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))),WebSocketMessageType.Text,true,CancellationToken.None);
    static int Port(){var s=new TcpListener(IPAddress.Loopback,0);s.Start();int p=((IPEndPoint)s.LocalEndpoint).Port;s.Stop();return p;}
    static async Task Main(string[] args) {
        var root=Path.Combine(Path.GetFullPath(args[0]),"run-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        typeof(App).GetProperty(nameof(App.DataDir))!.SetValue(null,root);Liuzhuan.Utils.Logger.Init(root);
        var identity=DeviceIdentity.Load(root);Check(identity==DeviceIdentity.Load(root),"installation ID survives reloading");
        DeviceIdentity.Rename(root,"Fixture workstation");Check(DeviceIdentity.Load(root).Id==identity.Id && DeviceIdentity.Load(root).Name=="Fixture workstation","rename preserves identity");
        var other=DeviceIdentity.Load(Path.Combine(root,"other"));Check(other.Id!=identity.Id,"new installation has distinct ID");
        Check(Task.Run(()=>{try{DeviceIdentity.Rename(root,"bad\nname");return false;}catch(ArgumentException){return true;}}).Result,"control characters rejected");
        var damaged=Path.Combine(root,"damaged");Directory.CreateDirectory(damaged);File.WriteAllText(Path.Combine(damaged,"device-identity.json"),"broken");
        Check(Task.Run(()=>{try{DeviceIdentity.Load(damaged);return false;}catch{return File.ReadAllText(Path.Combine(damaged,"device-identity.json"))=="broken";}}).Result,"corrupt identity preserved, never silently replaced");
        var nonce=Guid.NewGuid().ToString("N");var name=Convert.ToBase64String(Encoding.UTF8.GetBytes("Fixture"));var auth="test-only-auth";
        var proof=DeviceIdentity.Proof(auth,nonce,identity.Id,8899,name);
        Check(proof!=DeviceIdentity.Proof(auth,Guid.NewGuid().ToString("N"),identity.Id,8899,name),"fresh nonce prevents replaying a previous proof");
        DurableJson.Write(Path.Combine(root,"identity-fixture.json"),new{nonce,id=identity.Id,port=8899,name,auth,proof});
        var libraryIdentity=ResumableTransferServer.LoadIdentity(root);
        Check(libraryIdentity==ResumableTransferServer.LoadIdentity(root),"library identity available independently of transfer listener");
        var corruptLibrary=Path.Combine(root,"bad-library");Directory.CreateDirectory(Path.Combine(corruptLibrary,"transfers-v1"));
        var corruptIdentity=Path.Combine(corruptLibrary,"transfers-v1","server-id.json");File.WriteAllText(corruptIdentity,"null");
        bool corruptBlocked=false;try{ResumableTransferServer.LoadIdentity(corruptLibrary);}catch(InvalidDataException){corruptBlocked=true;}
        Check(corruptBlocked && File.ReadAllText(corruptIdentity)=="null","corrupt library identity never silently regenerates");
        LanConfig.Password="isolated-library-fixture";
        using var store=new DataStore();var gate=new object();
        for(int i=0;i<260;i++)store.Add(new(){Id=$"fixture-{i:D3}",DisplayName=$"Fixture {i:D3}",TextContent=$"fixture body {i:D3}",Type=MaterialType.Text,
            AddedTime=DateTime.Now.AddSeconds(i),GroupName=i%2==0?"Even":"Odd",Tags=i%3==0?new[]{"tri"}:Array.Empty<string>(),Note=i==200?"deep searchable note":"",IsFavorite=i%5==0,CollectionDevice="Fixture phone"});
        await store.FlushAsync();
        var lib=Guid.NewGuid().ToString("D");
        var service=new MaterialLibraryService(store,action=>{lock(gate)action();return Task.CompletedTask;},()=>lib);
        async Task<JsonElement> Request(object o)=>Json(await service.HandleAsync(Json(o)));
        var page=await Request(new{action="query",libraryId=lib,offset=0});
        Check(page.GetProperty("total").GetInt32()==260 && page.GetProperty("items").GetArrayLength()==100,"first page bounded, complete count beyond recent 50");
        var seq=page.GetProperty("sequence").GetInt64();
        var second=await Request(new{action="query",libraryId=lib,offset=100,sequence=seq});
        var third=await Request(new{action="query",libraryId=lib,offset=200,sequence=seq});
        var ids=new[]{page,second,third}.SelectMany(x=>x.GetProperty("items").EnumerateArray()).Select(x=>x.GetProperty("id").GetString()).ToArray();
        Check(ids.Length==260 && ids.Distinct().Count()==260,"all pages accessible without duplicate or skipped items");
        var deep=await Request(new{action="query",libraryId=lib,search="deep searchable note"});
        Check(deep.GetProperty("total").GetInt32()==1,"search covers older items and notes on server");
        var filtered=await Request(new{action="query",libraryId=lib,favorite=true,group="Even",tag="tri"});
        Check(filtered.GetProperty("total").GetInt32()==9,"favorite group tag intersection covers full library");
        var wrong=await Request(new{action="query",libraryId=Guid.NewGuid().ToString("D")});Check(wrong.GetProperty("code").GetString()=="target_changed","different library rejected");
        var item=store.Items.First(x=>x.Id=="fixture-200");int index=store.Items.IndexOf(item),updates=0;store.ItemUpdated+=_=>updates++;
        object Edit(long revision,string note)=>new{action="edit",libraryId=lib,id=item.Id,revision,favorite=true,group="Edited",tags=new[]{"mobile"},note};
        var saved=await Request(Edit(item.MetadataRevision,"from phone"));var after=item.MetadataRevision;
        Check(saved.GetProperty("item").GetProperty("note").GetString()=="from phone" && item.GroupName=="Edited" && item.IsFavorite,"mobile metadata edit applied");
        Check(after>0 && updates>0 && store.Items.IndexOf(item)==index,"metadata revision and delta advance without moving item");
        using(var indexJson=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"data.json"))))Check(indexJson.RootElement.EnumerateArray().First(x=>x.GetProperty("Id").GetString()==item.Id).GetProperty("Note").GetString()=="from phone","success returned only after material index flush");
        var conflict=await Request(Edit(0,"stale clobber"));Check(conflict.GetProperty("code").GetString()=="conflict" && item.Note=="from phone","stale mobile edit cannot overwrite desktop or other phone");
        store.SetFavorite(item.Id,false);Check(item.MetadataRevision>after,"desktop favorite changes increment item revision");
        var stale=await Request(new{action="query",libraryId=lib,offset=100,sequence=seq});Check(stale.GetProperty("stale").GetBoolean(),"page revision mismatch requires fresh snapshot");
        var before=item.Note;try{await Request(new{action="edit",libraryId=lib,id=item.Id,revision=item.MetadataRevision,favorite=false,group="x",tags=Enumerable.Range(0,21).Select(x=>"tag"+x).ToArray(),note="invalid"});throw new Exception("accepted invalid tags");}catch(ArgumentException){}
        Check(item.Note==before,"invalid metadata rejected before mutation");
        object Meta(MaterialItem x)=>new{favorite=x.IsFavorite,group=x.GroupName,tags=x.Tags.ToArray(),note=x.Note};
        object Merge(string id,object baseline,object desired)=>new{id,@base=baseline,desired};
        async Task<JsonElement> Batch(params object[] edits)=>await Request(new{action="merge_batch",libraryId=lib,edits});
        var baseline=Meta(item);var oldNote=item.Note;
        store.UpdateOrganization(new[]{item.Id},MaterialOrganization.Normalize(item.GroupName,item.Tags,"desktop independent note"));
        var merged=await Batch(Merge(item.Id,baseline,new{favorite=item.IsFavorite,group="Mobile batch",tags=item.Tags,note=oldNote}));
        Check(merged.GetProperty("results")[0].GetProperty("code").GetString()=="saved"&&item.GroupName=="Mobile batch"&&item.Note=="desktop independent note","three-way merge keeps unrelated desktop note");
        var revisionAfter=item.MetadataRevision;
        await Batch(Merge(item.Id,baseline,new{favorite=item.IsFavorite,group="Mobile batch",tags=item.Tags,note=oldNote}));
        Check(item.MetadataRevision==revisionAfter,"lost acknowledgement retry is idempotent without revision churn");
        baseline=Meta(item);
        store.UpdateOrganization(new[]{item.Id},MaterialOrganization.Normalize("Desktop conflict",item.Tags,item.Note));
        var secondItem=store.Items.First(x=>x.Id!="fixture-200");var secondBase=Meta(secondItem);
        var mixed=await Batch(Merge(item.Id,baseline,new{favorite=item.IsFavorite,group="Offline conflict",tags=item.Tags,note=item.Note}),
            Merge(secondItem.Id,secondBase,new{favorite=true,group="Batch success",tags=secondItem.Tags,note=secondItem.Note}));
        Check(mixed.GetProperty("results")[0].GetProperty("code").GetString()=="conflict"&&item.GroupName=="Desktop conflict"&&secondItem.GroupName=="Batch success","batch explicitly separates conflict and saved items");
        Check(mixed.GetProperty("results")[0].GetProperty("fields")[0].GetString()=="group","conflict identifies changed field and current authoritative item");
        var guard=secondItem.GroupName;
        try {await Batch(Merge(secondItem.Id,Meta(secondItem),new{favorite=false,group="Must not apply",tags=secondItem.Tags,note=secondItem.Note}),
            Merge(item.Id,Meta(item),new{favorite=false,group="Bad",tags=item.Tags,note=new string('x',4001)}));throw new Exception("invalid batch accepted");}catch(ArgumentException){}
        Check(secondItem.GroupName==guard,"whole batch validated before first mutation");
        var deleted=await Batch(Merge("deleted-id",Meta(item),Meta(item)));
        Check(deleted.GetProperty("results")[0].GetProperty("code").GetString()=="deleted","offline edit cannot resurrect deleted material");
        var textPage=await Request(new{action="query",libraryId=lib,includeText=true,search="Fixture 001"});
        Check(textPage.GetProperty("items")[0].GetProperty("textComplete").GetBoolean()&&textPage.GetProperty("items")[0].GetProperty("offlineText").GetString()=="fixture body 001","explicit offline query includes complete short pure text");
        Check(!page.GetProperty("items")[0].TryGetProperty("offlineText",out _),"ordinary page does not automatically transfer text bodies");
        var port=Port();var hub=new WsHub{DeviceId=identity.Id,DeviceName=()=>"Fixture",ServerId=lib,LibraryProvider=service.HandleAsync};hub.Start(port,LanConfig.PasswordHash);
        try {
            using(var anonymous=new ClientWebSocket()) {
                await anonymous.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"),CancellationToken.None);
                await Send(anonymous,new{type="library_request",id=Guid.NewGuid().ToString(),data=new{action="query",libraryId=lib}});
                using var timeout=new CancellationTokenSource(250);bool blocked=false;try{await Read(anonymous,"library_result",timeout.Token);}catch(OperationCanceledException){blocked=true;}Check(blocked,"unauthenticated library query has no access");
            }
            using var socket=new ClientWebSocket();await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"),CancellationToken.None);
            await Send(socket,new{type="hello",device="Fixture",data=new{auth=LanConfig.PasswordHash,ts=DateTimeOffset.UtcNow.ToUnixTimeSeconds(),clientId=Guid.NewGuid().ToString()}});
            var welcome=await Read(socket,"welcome");Check(welcome.GetProperty("deviceId").GetString()==identity.Id && welcome.GetProperty("features").EnumerateArray().Any(x=>x.GetString()==LanMessage.LibraryEditFeature),"authenticated welcome negotiates stable identity and enhanced library");
            var requestId=Guid.NewGuid().ToString();await Send(socket,new{type="library_request",id=requestId,data=new{action="query",libraryId=lib,offset=100,sequence=store.CurrentSequence}});
            var result=await Read(socket,"library_result");Check(result.GetProperty("requestId").GetString()==requestId && result.GetProperty("result").GetProperty("items").GetArrayLength()==100,"real WebSocket correlation and second page");
            await Send(socket,new{type="library_request",id=Guid.NewGuid().ToString(),data=new{action="query",libraryId=lib,expectedDeviceId=Guid.NewGuid().ToString()}});
            var refused=await Read(socket,"library_result");Check(refused.GetProperty("result").GetProperty("code").GetString()=="target_changed","authenticated request still rejects wrong expected computer identity");
        }finally{hub.Stop();}
        Console.WriteLine($"PASS {checks} identity and library checks");
    }
}
