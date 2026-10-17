using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Liuzhuan.Models;
using Liuzhuan.Services.Lan;

var checks=0;
void Check(bool ok,string label){if(!ok)throw new Exception(label);checks++;Console.WriteLine("PASS "+label);}
var reservation=new TcpListener(IPAddress.Loopback,0);reservation.Start();var port=((IPEndPoint)reservation.LocalEndpoint).Port;reservation.Stop();
LanConfig.PasswordHash=AuthService.HashPassword("isolated-only");
if (args.Contains("--microphone-pairing")) { await MicrophonePairingChecks.Run(); return; }
var stored=0;var contents=new List<string>();
var hub=new WsHub();
hub.TextReceived+=(content,_)=>{ if(content=="fail-registration")throw new IOException("injected registration error"); Interlocked.Increment(ref stored);lock(contents)contents.Add(content); };
hub.ItemLookup=id=>id=="registered-count"?new MaterialItem{Id=id,DisplayName="count",TextContent=Volatile.Read(ref stored).ToString()}:null;
hub.Start(port,LanConfig.PasswordHash);
try {
    if(args.Length>0&&args[0]=="--serve"){
        await File.WriteAllTextAsync(args[1],port.ToString());
        using var time=new CancellationTokenSource(TimeSpan.FromMinutes(3));
        while(!File.Exists(args[2]))await Task.Delay(50,time.Token);
        Console.WriteLine("Isolated production WsHub registered "+stored+" messages.");return;
    }
    async Task Send(ClientWebSocket socket,string type,string id,string content="",string device="isolated-phone",string app="com.allowed.app") {
        object data=type=="hello"?new{auth=LanConfig.PasswordHash,ts=DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:(object)new{content,app,automatic=true};
        await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{v=1,type,id,device,data})),WebSocketMessageType.Text,true,CancellationToken.None);
    }
    async Task<JsonDocument> Read(ClientWebSocket socket){var buffer=new byte[8192];using var deadline=new CancellationTokenSource(5000);using var result=new MemoryStream();
        WebSocketReceiveResult frame;do{frame=await socket.ReceiveAsync(buffer,deadline.Token);result.Write(buffer,0,frame.Count);}while(!frame.EndOfMessage);return JsonDocument.Parse(result.ToArray());}
    async Task<ClientWebSocket> Connect(string device){var socket=new ClientWebSocket();await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"),CancellationToken.None);
        await Send(socket,"hello","hello",device:device);using var welcome=await Read(socket);
        Check(welcome.RootElement.GetProperty("type").GetString()=="welcome"&&welcome.RootElement.GetProperty("data").GetProperty("features").EnumerateArray().Any(x=>x.GetString()==LanMessage.ClipboardReceiptsFeature),"real welcome negotiates idempotent clipboard receipts");return socket;}
    bool Ack(JsonDocument frame,string id,string status)=>frame.RootElement.GetProperty("type").GetString()=="ack"&&frame.RootElement.GetProperty("data").GetProperty("requestId").GetString()==id&&frame.RootElement.GetProperty("data").GetProperty("status").GetString()==status;
    using var phone=await Connect("isolated-phone");
    await Send(phone,"clipboard_push","stable-1","copied-once");
    // 模拟客户端未能利用首个回执，再使用相同请求重发；两帧回执均读取以核对关联。
    await Send(phone,"clipboard_push","stable-1","copied-once");
    using(var first=await Read(phone))Check(Ack(first,"stable-1","stored")&&stored==1,"lost first acknowledgement followed by retry registers exactly one material");
    using(var again=await Read(phone))Check(Ack(again,"stable-1","stored")&&stored==1,"duplicate request returns the original stored receipt");
    await Send(phone,"clipboard_push","stable-1","different-payload");
    using(var conflict=await Read(phone))Check(Ack(conflict,"stable-1","rejected")&&conflict.RootElement.GetProperty("data").GetProperty("reason").GetString()=="request_conflict"&&stored==1,"same request with conflicting content is rejected without registration");
    await Send(phone,"clipboard_push","stable-1","copied-once",app:"com.other.app");
    using(var sourceConflict=await Read(phone))Check(Ack(sourceConflict,"stable-1","rejected")&&stored==1,"source metadata conflict cannot rewrite an existing request");
    await Send(phone,"clipboard_push","stable-2","copied-once");
    using(var distinct=await Read(phone))Check(Ack(distinct,"stable-2","stored")&&stored==2,"intentional new copy with identical content and new request remains a separate event");
    phone.Abort();
    using var reconnected=await Connect("isolated-phone");await Send(reconnected,"clipboard_push","stable-1","copied-once");
    using(var reconnect=await Read(reconnected))Check(Ack(reconnect,"stable-1","stored")&&stored==2,"receipt cache survives websocket reconnection within the running hub");
    using var other=await Connect("isolated-other");await Send(other,"clipboard_push","stable-1","another-phone",device:"isolated-phone");
    using(var independent=await Read(other))Check(Ack(independent,"stable-1","stored")&&stored==3,"request scope uses authenticated session device rather than spoofed message device");
    await Send(reconnected,"clipboard_push","failed-1","fail-registration");
    using(var failed=await Read(reconnected))Check(Ack(failed,"failed-1","rejected")&&stored==3,"registration failure cannot produce a stored acknowledgement");
    await Send(reconnected,"clipboard_push","failed-1","fail-registration");
    using(var repeatedFailure=await Read(reconnected))Check(Ack(repeatedFailure,"failed-1","rejected")&&stored==3,"failed registration outcome is stable during the deduplication window");
    await Send(reconnected,"sync_text","manual-1","manual-compatible");
    using(var manual=await Read(reconnected))Check(Ack(manual,"manual-1","stored")&&stored==4,"existing manual sync_text acknowledgement stays compatible");
    await Send(reconnected,"clipboard_push",new string('x',129),"too-long");
    using(var invalid=await Read(reconnected))Check(invalid.RootElement.GetProperty("data").GetProperty("status").GetString()=="rejected"&&stored==4,"oversized request identity is rejected before cache allocation");
    Console.WriteLine($"All {checks} production websocket receipt checks passed.");
} finally {hub.Stop();}
