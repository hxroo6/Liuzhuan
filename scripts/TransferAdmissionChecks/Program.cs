using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Liuzhuan.Models;
using Liuzhuan.Services;
using Liuzhuan.Services.Lan;

internal static class Program {
    private const string Owner="00000000-0000-0000-0000-000000000123";
    private static int checks;
    private static void Check(bool ok,string label){if(!ok)throw new Exception(label);checks++;Console.WriteLine("PASS "+label);}
    private static string Hash(byte[] value)=>Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private static string Key(string id)=>Hash(Encoding.UTF8.GetBytes(Owner+":"+id));
    private static int Port(){var l=new TcpListener(IPAddress.Loopback,0);l.Start();int port=((IPEndPoint)l.LocalEndpoint).Port;l.Stop();return port;}
    private static async Task Until(Func<bool> condition){using var deadline=new CancellationTokenSource(10000);while(!condition())await Task.Delay(10,deadline.Token);}
    private static void Set(ResumableTransferServer server,string field,long value)=>typeof(ResumableTransferServer).GetField(field,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(server,value);
    private static int Reserved(ResumableTransferServer server)=>((System.Collections.Generic.HashSet<string>)typeof(ResumableTransferServer).GetField("_reservations",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(server)!).Count;
    private static string Seed(string root,string state="cancelled",DateTime? modified=null) {
        var id=Guid.NewGuid().ToString();var path=Path.Combine(root,"transfers-v1",Key(id)+".json");Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var record=new ResumableTransferServer.Manifest(id,Owner,"upload","seed.bin",0,Hash(Array.Empty<byte>()),Path.Combine(root,"uploads",id+".bin"),State:state,Updated:DateTime.UtcNow);
        File.WriteAllText(path,JsonSerializer.Serialize(record));if(modified.HasValue)File.SetLastWriteTimeUtc(path,modified.Value);return id;
    }
    private sealed class Fixture:IDisposable {
        public readonly ResumableTransferServer Server;
        public readonly HttpClient Http;
        public readonly Dictionary<string,MaterialItem> Items=new();
        public readonly string Root;
        public Fixture(string root,Func<Stream,CancellationToken,ValueTask<byte[]>>? hash=null) {
            Root=root;Directory.CreateDirectory(root);ConfigService.Root=root;
            Server=hash==null?new():new(hash);Server.ItemLookup=id=>{lock(Items)return Items.GetValueOrDefault(id);};
            int port=Port();Server.Start(port);Http=new(){BaseAddress=new Uri($"http://127.0.0.1:{port}"),Timeout=TimeSpan.FromSeconds(20)};
            Http.DefaultRequestHeaders.Add("X-Liuzhuan-Auth",LanConfig.PasswordHash);Http.DefaultRequestHeaders.Add("X-Liuzhuan-Client",Owner);
        }
        public string Source(string id) {var path=Path.Combine(Root,id+".bin");File.WriteAllBytes(path,Encoding.UTF8.GetBytes("actual SHA256 bytes: "+id));lock(Items)Items[id]=new(){FilePath=path,DisplayName=id+".bin"};return path;}
        public Task<HttpResponseMessage> Create(string id,string direction="upload",string item="",CancellationToken token=default)=>Http.PostAsync($"/v1/tasks/{id}?op=create",new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new{direction,name="fixture.bin",size=0,sha256=Hash(Array.Empty<byte>()),itemId=item})),token);
        public string Record(string id)=>Path.Combine(Root,"transfers-v1",Key(id)+".json");
        public void Dispose(){Http.Dispose();Server.Dispose();}
    }
    private static (IDisposable Lease,SemaphoreSlim Gate) Rent(Fixture f,string id) {
        var lease=(IDisposable)typeof(ResumableTransferServer).GetMethod("RentGate",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(f.Server,new object[]{Key(id)})!;
        return (lease,(SemaphoreSlim)lease.GetType().GetProperty("Gate")!.GetValue(lease)!);
    }
    private static async Task Restored(Fixture f,params string[] ids) {
        // Startup restoration also owns task gates. Finish it before testing a single explicit owner.
        await Until(()=>ids.All(id=>TransferJournal.Reports.Any(x=>x.Id=="resume:"+Key(id))));
        foreach(var id in ids) {
            var (lease,gate)=Rent(f,id);using(lease) {
                if(!await gate.WaitAsync(TimeSpan.FromSeconds(5)))throw new TimeoutException("Startup task gate did not finish");
                gate.Release();
            }
        }
    }
    private static async Task HashConcurrency(string root) {
        var entered=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelEntered=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);var changedEntered=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);var changedRelease=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);int hashCalls=0;
        async ValueTask<byte[]> HashDownload(Stream input,CancellationToken token) {
            Interlocked.Increment(ref hashCalls);var name=Path.GetFileName(((FileStream)input).Name);
            if(name=="blocked.bin"){entered.TrySetResult(true);await release.Task.WaitAsync(token);}
            if(name=="cancel.bin"){cancelEntered.TrySetResult(true);await Task.Delay(Timeout.Infinite,token);}
            if(name=="fail.bin")throw new IOException("injected hash failure");
            if(name=="changed.bin"){changedEntered.TrySetResult(true);await changedRelease.Task.WaitAsync(token);}
            return await SHA256.HashDataAsync(input,token);
        }
        using var f=new Fixture(root,HashDownload);var source=f.Source("blocked");var id=Guid.NewGuid().ToString();var slow=f.Create(id,"download","blocked");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using(var other=await f.Create(Guid.NewGuid().ToString()).WaitAsync(TimeSpan.FromSeconds(5)))Check(other.IsSuccessStatusCode && !slow.IsCompleted,"blocked download SHA256 does not hold capacity lock or delay unrelated creates");
        var duplicate=f.Create(id,"download","blocked");await Task.Delay(100);Check(hashCalls==1 && !duplicate.IsCompleted,"same task identity remains serialized while another identity proceeds");
        release.SetResult(true);using(var first=await slow)using(var second=await duplicate) {
            var doc=JsonDocument.Parse(await first.Content.ReadAsStringAsync());Check(first.IsSuccessStatusCode && second.IsSuccessStatusCode && doc.RootElement.GetProperty("sha256").GetString()==Hash(File.ReadAllBytes(source)),"commit retains full source SHA256 and duplicate task reuse");
        }
        Check(f.Server.CatalogReconciliations==1,"ordinary creates maintain their own catalog entries without rescanning transfer directory");
        f.Source("cancel");var cancelledId=Guid.NewGuid().ToString();using(var abort=new CancellationTokenSource()) {
            var cancelled=f.Create(cancelledId,"download","cancel",abort.Token);await cancelEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));abort.Cancel();try{using var ignored=await cancelled;}catch(OperationCanceledException) { }
        }
        await Until(()=>Reserved(f.Server)==0);Check(!File.Exists(f.Record(cancelledId)),"aborted hash releases its reservation without committing a manifest");
        f.Source("fail");var failedId=Guid.NewGuid().ToString();using(var failure=await f.Create(failedId,"download","fail"))Check(failure.StatusCode==HttpStatusCode.ServiceUnavailable && Reserved(f.Server)==0 && !File.Exists(f.Record(failedId)),"hash failure releases capacity and preserves missing task state");
        using(var missing=await f.Create(Guid.NewGuid().ToString(),"download","missing"))Check(missing.StatusCode==HttpStatusCode.NotFound && Reserved(f.Server)==0,"missing-source rejection releases its reservation");
        f.Source("changed");var replacement=f.Source("replacement");var changedId=Guid.NewGuid().ToString();var changing=f.Create(changedId,"download","changed");await changedEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));lock(f.Items)f.Items["changed"].FilePath=replacement;changedRelease.SetResult(true);
        using(var changed=await changing)Check(changed.StatusCode==HttpStatusCode.ServiceUnavailable && !File.Exists(f.Record(changedId)) && Reserved(f.Server)==0,"source binding is revalidated after hashing and before manifest commit");
    }
    private static async Task Capacity(string root) {
        Directory.CreateDirectory(root);for(int i=0;i<998;i++)Seed(root);
        var bad=Path.Combine(root,"transfers-v1","broken.json");File.WriteAllText(bad,"unreadable fixture");
        var entered=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async ValueTask<byte[]> Block(Stream input,CancellationToken token){entered.TrySetResult(true);await Task.Delay(Timeout.Infinite,token);return Array.Empty<byte>();}
        using var f=new Fixture(root,Block);f.Source("capacity");var id=Guid.NewGuid().ToString();using var abort=new CancellationTokenSource();var pending=f.Create(id,"download","capacity",abort.Token);await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using(var full=await f.Create(Guid.NewGuid().ToString()))Check((int)full.StatusCode==429 && Reserved(f.Server)==1,"pending hash reserves the final slot and prevents admission above 1000 tasks");
        Check(f.Server.CatalogReconciliations==2,"first capacity pressure reconciles external files without scanning on every normal create");
        abort.Cancel();try{using var ignored=await pending;}catch(OperationCanceledException) { }await Until(()=>Reserved(f.Server)==0);
        using(var accepted=await f.Create(Guid.NewGuid().ToString()))Check(accepted.IsSuccessStatusCode,"cancelled reservation frees its final capacity slot for a new task");
        Check(Directory.GetFiles(Path.Combine(root,"transfers-v1"),"*.json").Length==1001 && File.ReadAllText(bad)=="unreadable fixture","identity file is excluded while broken records are retained and counted");
    }
    private static async Task Expiry(string root) {
        Directory.CreateDirectory(root);var old=DateTime.UtcNow.AddDays(-9);var active=Seed(root,"active",old);var paused=Seed(root,"paused",old);var stored=Seed(root,"stored",old);var refreshed=Seed(root,"stored",old);
        var heldPartial=Path.Combine(root,"transfers-v1",Key(active)+".part");File.WriteAllText(heldPartial,"inflight bytes");
        var partial=Path.Combine(root,"transfers-v1",Key(paused)+".part");File.WriteAllText(partial,"expired bytes");
        var broken=Path.Combine(root,"transfers-v1","broken.json");File.WriteAllText(broken,"broken");File.SetLastWriteTimeUtc(broken,old);
        var nullId=Guid.NewGuid().ToString();var nullPath=Path.Combine(root,"transfers-v1",Key(nullId)+".json");File.WriteAllText(nullPath,"null");File.SetLastWriteTimeUtc(nullPath,old);
        var activeRecord=Path.Combine(root,"transfers-v1",Key(active)+".json");var rename=activeRecord+".fixture-rename";
        File.Move(activeRecord,rename);File.Move(rename,Path.Combine(root,"transfers-v1",Key(active).ToUpperInvariant()+".json"));
        using var f=new Fixture(root);await Restored(f,active,paused,stored,refreshed);File.SetLastWriteTimeUtc(f.Record(refreshed),DateTime.UtcNow);
        var (lease,gate)=Rent(f,active);await gate.WaitAsync();
        try {
            using var created=await f.Create(Guid.NewGuid().ToString());Check(created.IsSuccessStatusCode && File.Exists(f.Record(active)) && File.ReadAllText(heldPartial)=="inflight bytes","expiry shares case-insensitive Windows task gates and never deletes an in-flight manifest or partial");
            Set(f.Server,"_nextSweep",0);using var another=await f.Create(Guid.NewGuid().ToString());
            Check(!File.Exists(f.Record(paused)) && !File.Exists(f.Record(stored)) && !File.Exists(partial),"idle active/paused/terminal records retain the established eight-day expiry policy");
            Check(File.Exists(f.Record(refreshed)),"expiry rechecks live file timestamp instead of deleting from stale catalog metadata");
            Check(File.ReadAllText(broken)=="broken" && File.ReadAllText(nullPath)=="null","expired malformed and JSON-null records stay untouched and occupy capacity");
            using var corruptReuse=await f.Create(nullId);Check(corruptReuse.StatusCode==HttpStatusCode.ServiceUnavailable && File.ReadAllText(nullPath)=="null","JSON-null record cannot be silently overwritten by task creation");
        }finally{gate.Release();lease.Dispose();}
        Set(f.Server,"_nextSweep",0);using(var next=await f.Create(Guid.NewGuid().ToString()))Check(next.IsSuccessStatusCode && !File.Exists(f.Record(active)) && !File.Exists(heldPartial),"expired task becomes reclaimable only after its in-flight gate is released");
        var scans=f.Server.CatalogReconciliations;var external=Path.Combine(root,"transfers-v1","external-broken.json");File.WriteAllText(external,"preserved");Set(f.Server,"_nextReconcile",0);
        using(var next=await f.Create(Guid.NewGuid().ToString()))Check(next.IsSuccessStatusCode && f.Server.CatalogReconciliations==scans+1 && File.ReadAllText(external)=="preserved","scheduled catalog reconciliation discovers external records without deleting unknown data");
    }
    private static async Task FairExpiry(string root) {
        Directory.CreateDirectory(Path.Combine(root,"transfers-v1"));var old=DateTime.UtcNow.AddDays(-9);
        for(int i=0;i<129;i++){var path=Path.Combine(root,"transfers-v1","!broken-"+i.ToString("D3")+".json");File.WriteAllText(path,"broken");File.SetLastWriteTimeUtc(path,old);}
        for(int i=0;i<870;i++)Seed(root);
        var expired=Seed(root,"stored",old);using var f=new Fixture(root);await Restored(f,expired);
        using var created=await f.Create(Guid.NewGuid().ToString());
        Check(created.IsSuccessStatusCode && !File.Exists(f.Record(expired)) && Directory.GetFiles(Path.Combine(root,"transfers-v1"),"!broken-*.json").Length==129,
            "bounded expiry cursor advances beyond corrupt oldest records without deleting or starving them");
    }
    private static async Task Main(string[] args) {
        var root=Path.Combine(Path.GetFullPath(args.Length>0?args[0]:Path.GetTempPath()),"run-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        await HashConcurrency(Path.Combine(root,"hash"));await Capacity(Path.Combine(root,"capacity"));await Expiry(Path.Combine(root,"expiry"));await FairExpiry(Path.Combine(root,"fair-expiry"));
        Console.WriteLine($"All {checks} transfer admission checks passed. Evidence: {root}");
    }
}
