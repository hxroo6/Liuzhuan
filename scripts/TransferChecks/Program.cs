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
        string output=Path.Combine("F:/appdata/Temp","TransferChecks-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(output);
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
