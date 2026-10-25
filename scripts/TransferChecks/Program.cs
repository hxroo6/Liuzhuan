using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Liuzhuan;
using Liuzhuan.Models;
using Liuzhuan.Services;
using Liuzhuan.Services.Lan;

static class Program
{
    static void Check(bool condition,string message) { if(!condition) throw new Exception(message);Console.WriteLine("PASS "+message); }
    static int FreePort() { var socket=new TcpListener(IPAddress.Loopback,0); socket.Start();int p=((IPEndPoint)socket.LocalEndpoint).Port;socket.Stop();return p; }
    static async Task Main(string[] args)
    {
        string output=Path.Combine(Path.GetTempPath(),"TransferChecks-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(output);
        typeof(App).GetProperty(nameof(App.DataDir))!.SetValue(null,output);Liuzhuan.Utils.Logger.Init(output);
        LanConfig.Password="test-only";
        var image=new MaterialItem {Id="photo",Type=MaterialType.Image,FilePath=Path.GetFullPath(args[0]),DisplayName="sample.png"};
        using var server=new FileHttpServer {ItemLookup=id=>id=="photo"?image:null};
        string? uploaded=null;int uploads=0;
        server.FileUploaded+=(path,_)=>{uploaded=path;uploads++;};
        int port=FreePort();server.Start(port);
        using var http=new HttpClient { BaseAddress=new Uri($"http://127.0.0.1:{port}"),Timeout=TimeSpan.FromSeconds(30) };
        string auth=LanConfig.PasswordHash;
        var bytes=File.ReadAllBytes(image.FilePath);
        var response=await http.PostAsync($"/upload?name=sample.png&auth={auth}",new ByteArrayContent(bytes));
        Check(response.IsSuccessStatusCode && uploaded!=null && File.ReadAllBytes(uploaded).SequenceEqual(bytes),"upload exact bytes before success");
        using(var socket=new TcpClient())
        {
            await socket.ConnectAsync(IPAddress.Loopback,port);var stream=socket.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"POST /upload?name=partial.png&auth={auth} HTTP/1.1\r\nContent-Length: 100\r\n\r\nabc"));socket.Client.Shutdown(SocketShutdown.Send);
            using var reader=new StreamReader(stream);string result=await reader.ReadToEndAsync();
            Check(result.StartsWith("HTTP/1.1 400"),"truncated upload rejected");
        }
        Check(uploads==1 && !Directory.GetFiles(Path.Combine(output,"uploads"),"*.part").Any(),"partial never registered; residue removed");
        Check((await http.GetAsync("/thumbnail/photo?auth=bad")).StatusCode==HttpStatusCode.Forbidden,"thumbnail authentication");
        var thumb=await http.GetByteArrayAsync($"/thumbnail/photo?auth={auth}");
        Check(thumb.Length>24 && thumb[0]==137 && Encoding.ASCII.GetString(thumb,1,3)=="PNG","thumbnail actual PNG encoding");
        int width=IPAddress.NetworkToHostOrder(BitConverter.ToInt32(thumb,16));
        Check(width<=256,"thumbnail bounded width");
        var download=await http.GetByteArrayAsync($"/file/photo?auth={auth}");Check(download.SequenceEqual(bytes),"download exact bytes");
        Check(TransferJournal.Entries.Any(x=>x.State=="失败") && TransferJournal.Entries.Any(x=>x.State=="已接收") && TransferJournal.Entries.Any(x=>x.State=="已发送"),"task journal receives failure and completion");
        Check(TransferJournal.Entries.Any(x=>x.State=="已接收" && x.SavedPath==uploaded && x.CanOpenFolder),"received task exposes its actual saved path");
        var launch=FolderLauncher.CreateStartInfo(new("custom.exe","/O /T /L={folder} \"fixed option\""),"F:\\素材 文件夹\\");
        Check(launch.ArgumentList.SequenceEqual(new[]{"/O","/T","/L=F:\\素材 文件夹\\","fixed option"}) && !launch.UseShellExecute,"custom manager receives directory as one safe argument, including spaces and trailing slash");
        bool rejected=false;try {FolderLauncher.BuildArguments("/O","sample");}catch(ArgumentException){rejected=true;}
        Check(rejected,"manager template without directory placeholder is rejected");
        using(var control=new TransferControl(()=>{},()=>{})) {
            control.TogglePause();var waiting=control.WaitAsync();await Task.Delay(30);
            Check(!waiting.IsCompleted,"desktop pause blocks the next I/O boundary");
            control.TogglePause();await waiting.WaitAsync(TimeSpan.FromSeconds(2));
            Check(!control.IsPaused,"desktop continue opens the same connection gate");
            control.TogglePause();waiting=control.WaitAsync();control.Cancel();
            try{await waiting;throw new Exception("cancellation not observed");}catch(OperationCanceledException){}
            Check(control.IsCancelled,"desktop cancel interrupts a paused gate");
        }
        using(var socket=new TcpClient()) {
            await socket.ConnectAsync(IPAddress.Loopback,port);var stream=socket.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"POST /upload?name=controlled.bin&auth={auth} HTTP/1.1\r\nContent-Length: 100000\r\n\r\na"));
            async Task<TransferEntry> AwaitEntry(Func<TransferEntry,bool> predicate) {
                using var deadline=new CancellationTokenSource(5000);
                while(true) {var entry=TransferJournal.Entries.FirstOrDefault(x=>x.Name=="controlled.bin" && predicate(x));if(entry!=null)return entry;await Task.Delay(10,deadline.Token);}
            }
            var active=await AwaitEntry(x=>x.CanControl);TransferJournal.TogglePause(active.Id);
            await stream.WriteAsync(new byte[]{2});var paused=await AwaitEntry(x=>x.IsPaused);
            Check(paused.PauseLabel=="继续" && paused.State=="已暂停","real HTTP upload publishes paused controls");
            TransferJournal.TogglePause(active.Id);await stream.WriteAsync(new byte[]{3});await AwaitEntry(x=>!x.IsPaused && x.CanControl);
            TransferJournal.Cancel(active.Id);await AwaitEntry(x=>x.State=="已取消" && !x.CanControl);
            Check(uploads==1 && !Directory.GetFiles(Path.Combine(output,"uploads"),"*.part").Any(),"desktop cancel closes actual upload and removes incomplete source before registration");
        }
        using(var control=new TransferControl(()=>{},()=>{},pauseTimeout:TimeSpan.FromMilliseconds(20))) {
            control.TogglePause();bool expired=false;try{await control.WaitAsync();}catch(TimeoutException){expired=true;}
            Check(expired,"paused connection has a finite lease instead of retaining dead sockets indefinitely");
        }
        var leaseClosed=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using(var control=new TransferControl(()=>leaseClosed.TrySetResult(true),()=>{},pauseTimeout:TimeSpan.FromMilliseconds(20))) {
            control.TogglePause();await leaseClosed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(control.IsPauseExpired && control.Token.IsCancellationRequested,"pause expiry closes even the current blocked I/O instead of waiting for its next boundary");
        }
        using(var lifetime=new CancellationTokenSource()) using(var control=new TransferControl(()=>{},()=>{},lifetime.Token)) {
            control.TogglePause();var waiting=control.WaitAsync();lifetime.Cancel();
            bool stopped=false;try{await waiting;}catch(OperationCanceledException){stopped=true;}
            Check(stopped,"stopping LAN service also cancels a paused transfer gate");
        }
        async Task<string> ReadConnection(TcpClient peer) {
            try {var b=new byte[1024];int n=await peer.GetStream().ReadAsync(b).AsTask().WaitAsync(TimeSpan.FromSeconds(3));return Encoding.ASCII.GetString(b,0,n);}
            catch(IOException){return "";}
        }
        using(var bounded=new FileHttpServer(TimeSpan.FromMilliseconds(250),maxConnections:2)) {
            int boundedPort=FreePort();bounded.Start(boundedPort);
            using(var slow=new TcpClient()) {
                await slow.ConnectAsync(IPAddress.Loopback,boundedPort);await slow.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET /"));
                Check(await ReadConnection(slow)=="","incomplete request headers expire and close the socket");
            }
            foreach(var malformed in new[]{"GET /x HTTP/1.1\r\nX: "+new string('a',9000)+"\r\n\r\n",
                "GET /x HTTP/1.1\r\n"+string.Concat(Enumerable.Range(0,101).Select(i=>$"X-{i}: 1\r\n"))+"\r\n",
                "GET /x HTTP/1.1\r\n"+string.Concat(Enumerable.Range(0,70).Select(i=>$"X-{i}: {new string('a',1000)}\r\n"))+"\r\n",
                "POST /upload HTTP/1.1\r\nContent-Length: 1\r\nContent-Length: 2\r\n\r\n"}) {
                using var bad=new TcpClient();await bad.ConnectAsync(IPAddress.Loopback,boundedPort);
                try {await bad.GetStream().WriteAsync(Encoding.ASCII.GetBytes(malformed));}catch(IOException) { }
                var rejectedHeader=await ReadConnection(bad);Check(rejectedHeader.Length==0 || rejectedHeader.StartsWith("HTTP/1.1 400"),"request header line, count, total, and conflicting-length budgets reject malformed input");
            }
            using(var body=new TcpClient()) {
                await body.ConnectAsync(IPAddress.Loopback,boundedPort);
                await body.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"POST /upload?name=slow-body.bin&auth={auth} HTTP/1.1\r\nContent-Length: 2\r\n\r\na"));
                await Task.Delay(450);await body.GetStream().WriteAsync(new byte[]{(byte)'b'});
                Check((await ReadConnection(body)).StartsWith("HTTP/1.1 200"),"header timeout is removed before a legitimate slow upload body");
            }
            using var idleOne=new TcpClient();using var idleTwo=new TcpClient();
            await idleOne.ConnectAsync(IPAddress.Loopback,boundedPort);await idleTwo.ConnectAsync(IPAddress.Loopback,boundedPort);
            await idleOne.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET /"));await idleTwo.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET /"));await Task.Delay(50);
            using(var excess=new TcpClient()) {await excess.ConnectAsync(IPAddress.Loopback,boundedPort);Check(await ReadConnection(excess)=="","accepted handlers have a hard concurrent connection limit");}
            bounded.Stop();Check(await ReadConnection(idleOne)=="" && await ReadConnection(idleTwo)=="","stopping HTTP service closes accepted clients still waiting for request headers");
            bounded.Start(boundedPort);
            using var afterRestart=new TcpClient();await afterRestart.ConnectAsync(IPAddress.Loopback,boundedPort);await afterRestart.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET /missing HTTP/1.1\r\n\r\n"));
            Check((await ReadConnection(afterRestart)).StartsWith("HTTP/1.1 404"),"HTTP service can restart after cancelling half-open clients");
        }
        var hub=new WsHub();int wsPort=FreePort(); bool stored=false;hub.TextReceived+=(_,_)=>stored=true;hub.Start(wsPort,auth);
        try
        {
            using var ws=new ClientWebSocket();await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{wsPort}"),CancellationToken.None);
            async Task Send(object message)=>await ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)),WebSocketMessageType.Text,true,CancellationToken.None);
            async Task<JsonDocument> Read() {var b=new byte[8192];using var ct=new CancellationTokenSource(10000);var r=await ws.ReceiveAsync(b,ct.Token);return JsonDocument.Parse(b.AsMemory(0,r.Count));}
            await Send(new {v=1,type="hello",id="h",device="test",data=new {auth,ts=DateTimeOffset.UtcNow.ToUnixTimeSeconds()}});using var welcome=await Read();
            Check(welcome.RootElement.GetProperty("type").GetString()=="welcome","authenticated websocket");
            await Send(new {v=1,type="sync_text",id="task-1",device="test",data=new {content="test"}});using var ack=await Read();var data=ack.RootElement.GetProperty("data");
            Check(stored && data.GetProperty("status").GetString()=="stored" && data.GetProperty("requestId").GetString()=="task-1","ack follows storage and correlates task");
        }
        finally {hub.Stop();}
        Console.WriteLine("Artifacts: "+output);
    }
}
