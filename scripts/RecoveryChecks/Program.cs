using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Liuzhuan;
using Liuzhuan.Models;
using Liuzhuan.Services;
using Liuzhuan.Services.Lan;

internal static class Program
{
    static int checks;
    static void Check(bool ok,string label){if(!ok)throw new Exception(label);checks++;Console.WriteLine("PASS "+label);}
    static int Port(){var s=new TcpListener(IPAddress.Loopback,0);s.Start();int p=((IPEndPoint)s.LocalEndpoint).Port;s.Stop();return p;}
    static string Hash(byte[] data)=>Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    static async Task Main(string[] args)
    {
        var host=args.Contains("--host");
        var root=host?Path.GetFullPath(args[0]):Path.Combine(Path.GetFullPath(args[0]),"run-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        typeof(App).GetProperty(nameof(App.DataDir))!.SetValue(null,root);Liuzhuan.Utils.Logger.Init(root);
        LanConfig.Password="isolated-recovery-test";TransferJournal.Initialize(root);
        using var store=new DataStore();var storeGate=new object();int registered=0;bool failAfterSave=false;
        Task Register(string key,string text)
        {
            lock(storeGate){
                if(!store.Items.Any(x=>x.RegistrationKeys.Contains(key))){store.Add(new(){Type=MaterialType.Text,TextContent=text,DisplayName="Fixture",RegistrationKeys=new[]{key}});registered++;}
                var flush=store.FlushAsync();return Complete(flush);
            }
            async Task Complete(Task flush){await flush;if(failAfterSave){failAfterSave=false;throw new IOException("injected crash after material commit");}}
        }
        var source=Path.Combine(root,"source.bin");if(!File.Exists(source))File.WriteAllBytes(source,RandomNumberGenerator.GetBytes((host?32:2)*1048576+123));var sourceBytes=File.ReadAllBytes(source);
        if(!store.Items.Any(x=>x.Id=="fixture-source"))store.Add(new(){Id="fixture-source",Type=MaterialType.Other,FilePath=source,DisplayName="source.bin"});await store.FlushAsync();
        var port=host?int.Parse(args[2]):Port();var wsPort=host?int.Parse(args[3]):Port();ResumableTransferServer server=null!;WsHub hub=null!;
        void Start(){server=new(){ItemLookup=id=>{lock(storeGate)return store.Items.FirstOrDefault(x=>x.Id==id);},FileReceiver=(key,path,name,origin,device)=>Register(key,path)};
            server.Start(port);hub=new(){ServerId=server.ServerId,ResumePort=port,DurableTextReceiver=(key,text,device)=>Register(key,text)};server.ControlChanged=hub.SendTransferState;hub.Start(wsPort,LanConfig.PasswordHash);}
        void Stop(){hub.Stop();server.Dispose();}
        Start();var initialServer=server.ServerId;
        if(host) {
            DurableJson.Write(args[4],new{port,serverId=server.ServerId});
            while(!File.Exists(args[5]))await Task.Delay(100);
            Stop();TransferJournal.Flush();return;
        }
        using var http=new HttpClient{BaseAddress=new Uri($"http://127.0.0.1:{port}"),Timeout=TimeSpan.FromSeconds(30)};
        http.DefaultRequestHeaders.Add("X-Liuzhuan-Auth",LanConfig.PasswordHash);
        var owner=Guid.NewGuid().ToString();http.DefaultRequestHeaders.Add("X-Liuzhuan-Client",owner);
        var controlSequences=new System.Collections.Concurrent.ConcurrentDictionary<string,long>();
        async Task<HttpResponseMessage> Request(string id,string op,HttpMethod method,byte[]? bytes=null,long? offset=null,string? sha=null,long? sequence=null)
        {
            var req=new HttpRequestMessage(method,$"/v1/tasks/{id}?op={op}"+(offset.HasValue?$"&offset={offset}":""));
            if(op is "pause" or "resume" or "cancel")req.Headers.Add("X-Control-Sequence",(sequence??controlSequences.AddOrUpdate(id,1,(_,old)=>old+1)).ToString());
            if(method!=HttpMethod.Get)req.Content=new ByteArrayContent(bytes??Array.Empty<byte>());
            if(bytes!=null && op=="chunk")req.Headers.Add("X-Chunk-SHA256",sha??Hash(bytes));
            if(sha!=null && op=="complete")req.Headers.Add("X-File-SHA256",sha);
            return await http.SendAsync(req);
        }
        async Task<JsonElement> Json(HttpResponseMessage r){var data=await r.Content.ReadAsStringAsync();if(!r.IsSuccessStatusCode)throw new Exception($"HTTP {(int)r.StatusCode}: {data}");return JsonDocument.Parse(data).RootElement.Clone();}
        byte[] Create(string direction,byte[] data,string item="")=>JsonSerializer.SerializeToUtf8Bytes(new{direction,name="fixture.bin",size=data.Length,sha256=Hash(data),itemId=item});
        try {
            using(var noAuth=new HttpClient())Check((await noAuth.GetAsync($"http://127.0.0.1:{port}/v1/tasks/{Guid.NewGuid()}?op=status")).StatusCode==HttpStatusCode.Forbidden,"resumable endpoint rejects unauthenticated requests");
            var id=Guid.NewGuid().ToString();var bytes=RandomNumberGenerator.GetBytes(2*1048576+71);
            var created=await Json(await Request(id,"create",HttpMethod.Post,Create("upload",bytes)));
            Check(created.GetProperty("offset").GetInt64()==0,"new upload begins at a durable zero offset");
            Check((await Request(id,"chunk",HttpMethod.Put,bytes[..1048576],0,new string('0',64))).StatusCode==(HttpStatusCode)422,"bad chunk checksum never advances committed offset");
            Check((await Request(id,"chunk",HttpMethod.Put,bytes[..1048576],1)).StatusCode==HttpStatusCode.Conflict,"offset mismatch is refused before writing");
            var first=await Json(await Request(id,"chunk",HttpMethod.Put,bytes[..1048576],0));
            Check(first.GetProperty("offset").GetInt64()==1048576,"first block is flushed and acknowledged");
            var partial=Directory.GetFiles(Path.Combine(root,"transfers-v1"),"*.part").Single();File.AppendAllText(partial,"uncommitted-tail");
            Stop();Start();
            var recovered=await Json(await Request(id,"status",HttpMethod.Get));
            Check(server.ServerId==initialServer && recovered.GetProperty("offset").GetInt64()==1048576,"fresh server instance restores identity and committed offset");
            await Json(await Request(id,"pause",HttpMethod.Post));
            Check((await Request(id,"chunk",HttpMethod.Put,bytes[1048576..2097152],1048576)).StatusCode==(HttpStatusCode)423,"remote pause prevents further chunk commits");
            Stop();Start();Check((await Json(await Request(id,"status",HttpMethod.Get))).GetProperty("state").GetString()=="paused","manual pause survives service restart");
            await Json(await Request(id,"resume",HttpMethod.Post));
            await Json(await Request(id,"chunk",HttpMethod.Put,bytes[1048576..2097152],1048576));
            await Json(await Request(id,"chunk",HttpMethod.Put,bytes[2097152..],2097152));
            failAfterSave=true;
            Check((await Request(id,"complete",HttpMethod.Post)).StatusCode==HttpStatusCode.ServiceUnavailable,"failure after material flush cannot send a stored receipt");
            var count=registered;Stop();Start();
            var completed=await Json(await Request(id,"complete",HttpMethod.Post));
            Check(completed.GetProperty("state").GetString()=="stored" && registered==count,"pending receipt replay after restart registers material only once");
            var final=Directory.GetFiles(Path.Combine(root,"uploads")).Single();Check(File.ReadAllBytes(final).SequenceEqual(bytes),"recovery truncates uncommitted tail and preserves exact file bytes");
            await Json(await Request(id,"create",HttpMethod.Post,Create("upload",bytes)));
            Check(registered==count,"lost completion reply can be retried without duplicate registration");
            Check((await Request(id,"create",HttpMethod.Post,Create("upload",new byte[7]))).StatusCode==HttpStatusCode.Conflict,"same task ID cannot be reused for different source content");
            var empty=Guid.NewGuid().ToString();await Json(await Request(empty,"create",HttpMethod.Post,Create("upload",Array.Empty<byte>())));await Json(await Request(empty,"complete",HttpMethod.Post));
            Check(Directory.GetFiles(Path.Combine(root,"uploads")).Any(f=>new FileInfo(f).Length==0),"zero byte files finish with checksum and material receipt");
            var longId=Guid.NewGuid().ToString();var longName=new string('n',230)+".bin";
            await Json(await Request(longId,"create",HttpMethod.Post,JsonSerializer.SerializeToUtf8Bytes(new{direction="upload",name=longName,size=0,sha256=Hash(Array.Empty<byte>())})));
            await Json(await Request(longId,"complete",HttpMethod.Post));
            var longKey=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(owner+":"+longId))).ToLowerInvariant();
            var longSaved=Directory.GetFiles(Path.Combine(root,"uploads"),longKey+"_*").Single();
            Check(Path.GetFileName(longSaved).Length<=185 && Path.GetExtension(longSaved)==".bin","long original names keep their extension without exceeding Windows filename limits");
            var cancel=Guid.NewGuid().ToString();await Json(await Request(cancel,"create",HttpMethod.Post,Create("upload",bytes)));await Json(await Request(cancel,"chunk",HttpMethod.Put,bytes[..1048576],0));await Json(await Request(cancel,"cancel",HttpMethod.Post));
            Stop();Start();Check((await Request(cancel,"chunk",HttpMethod.Put,bytes[..1048576],0)).StatusCode==HttpStatusCode.Gone,"cancel tombstone prevents automatic resurrection after restart");
            var download=Guid.NewGuid().ToString();var meta=await Json(await Request(download,"create",HttpMethod.Post,Create("download",Array.Empty<byte>(),"fixture-source")));
            Check(meta.GetProperty("sha256").GetString()==Hash(sourceBytes),"download captures immutable source checksum");
            using(var firstRead=await Request(download,"chunk",HttpMethod.Get,offset:0)) {
                var block=await firstRead.Content.ReadAsByteArrayAsync();Check(block.SequenceEqual(sourceBytes[..1048576]) && firstRead.Headers.GetValues("X-Chunk-SHA256").Single()==Hash(block),"download block and checksum describe the same bytes");
            }
            await Json(await Request(download,"ack",HttpMethod.Post,offset:1048576));Stop();Start();
            Check((await Json(await Request(download,"status",HttpMethod.Get))).GetProperty("offset").GetInt64()==1048576,"receiver durable offset acknowledgment survives restart");
            Check((await Request(download,"complete",HttpMethod.Post,sha:Hash(sourceBytes))).StatusCode==HttpStatusCode.Conflict,"bytes served are not treated as receiver saved receipt");
            File.AppendAllText(source,"changed");Check((await Request(download,"chunk",HttpMethod.Get,offset:1048576)).StatusCode==HttpStatusCode.PreconditionFailed,"changed PC source cannot be spliced into an existing download");
            async Task<ClientWebSocket> Connect(string? deviceId=null){var ws=new ClientWebSocket();await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{wsPort}"),default);
                await ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{type="hello",device="fixture-phone",data=new{auth=LanConfig.PasswordHash,ts=DateTimeOffset.UtcNow.ToUnixTimeSeconds(),clientId=deviceId??owner}})),WebSocketMessageType.Text,true,default);await Read(ws);return ws;}
            async Task<JsonElement> Read(ClientWebSocket ws){var b=new byte[65536];using var timeout=new CancellationTokenSource(10000);var r=await ws.ReceiveAsync(b,timeout.Token);return JsonDocument.Parse(b.AsMemory(0,r.Count)).RootElement.Clone();}
            async Task<JsonElement> Text(ClientWebSocket ws,string request,string content){await ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{type="clipboard_push",id=request,data=new{content,app="fixture"}})),WebSocketMessageType.Text,true,default);return (await Read(ws)).GetProperty("data");}
            var textId=Guid.NewGuid().ToString();using(var ws=await Connect()) {
                var ack=await Text(ws,textId,"durable text fixture");Check(ack.GetProperty("durable").GetBoolean() && ack.GetProperty("status").GetString()=="stored","text stored ACK follows a flushed material and durable receipt");
            }
            lock(storeGate){store.Remove(store.Items.Single(x=>x.TextContent=="durable text fixture").Id);}await store.FlushAsync();count=registered;
            Stop();Start();using(var ws=await Connect()) {
                Check((await Text(ws,textId,"durable text fixture")).GetProperty("status").GetString()=="stored" && registered==count && store.Items.All(x=>x.TextContent!="durable text fixture"),"late retry after restart does not resurrect intentionally deleted text");
                Check((await Text(ws,textId,"different content")).GetProperty("status").GetString()=="rejected","durable request fingerprint rejects ID conflicts across restart");
            }
            var controlled=Guid.NewGuid().ToString();await Json(await Request(controlled,"create",HttpMethod.Post,Create("upload",new byte[8])));
            var controlKey=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(owner+":"+controlled))).ToLowerInvariant();
            using(var ws=await Connect()) {
                TransferJournal.TogglePause("resume:"+controlKey);var pausedState=await Read(ws);
                Check(pausedState.GetProperty("type").GetString()=="transfer_state" && pausedState.GetProperty("data").GetProperty("state").GetString()=="paused","PC pause persists state and notifies the authenticated owner socket");
                TransferJournal.TogglePause("resume:"+controlKey);var activeState=await Read(ws);
                Check(activeState.GetProperty("data").GetProperty("state").GetString()=="active" && activeState.GetProperty("data").GetProperty("revision").GetInt64()==2,"PC continue sends a newer control revision without polling");
            }
            Stop();Start();var controlRecovered=await Json(await Request(controlled,"status",HttpMethod.Get));
            Check(controlRecovered.GetProperty("controlSource").GetString()=="desktop" && controlRecovered.GetProperty("controlRevision").GetInt64()==2,"missed desktop controls can be reconciled from durable status after reconnect");
            await Json(await Request(controlled,"resume",HttpMethod.Post,sequence:10));
            var stalePause=await Json(await Request(controlled,"pause",HttpMethod.Post,sequence:9));
            Check(stalePause.GetProperty("state").GetString()=="active","late client pause cannot undo a newer continue intent");
            Stop();Start();
            Check((await Json(await Request(controlled,"pause",HttpMethod.Post,sequence:9))).GetProperty("state").GetString()=="active","client control ordering survives server restart");
            using(var sameNameA=await Connect(Guid.NewGuid().ToString()))using(var sameNameB=await Connect(Guid.NewGuid().ToString())) {
                var a=await Text(sameNameA,"same-display-request","first device content");var b=await Text(sameNameB,"same-display-request","second device content");
                Check(a.GetProperty("status").GetString()=="stored" && b.GetProperty("status").GetString()=="stored","two authenticated UUIDs with identical friendly names retain independent sessions and receipts");
            }
            var receiver=new DurableInbox(root);var replayCount=0;await Task.WhenAll(Enumerable.Range(0,8).Select(_=>receiver.RegisterAsync("concurrent","hash",key=>{Interlocked.Increment(ref replayCount);return Task.CompletedTask;})));
            Check(replayCount==1,"concurrent duplicate requests share one durable registration");
            Console.WriteLine($"All {checks} recovery checks passed. Evidence: {root}");
        }finally{Stop();TransferJournal.Flush();}
    }
}
