using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Liuzhuan;
using Liuzhuan.Services;
using Liuzhuan.Services.Audio;
using Liuzhuan.Services.Lan;

static class ConcurrentChecks
{
    static int Port(){var s=new TcpListener(IPAddress.Loopback,0);s.Start();int port=((IPEndPoint)s.LocalEndpoint).Port;s.Stop();return port;}
    sealed class InterruptedContent:HttpContent
    {
        protected override bool TryComputeLength(out long length){length=1048576;return true;}
        protected override Task SerializeToStreamAsync(Stream stream,TransportContext? context)=>Write(stream,CancellationToken.None);
        protected override Task SerializeToStreamAsync(Stream stream,TransportContext? context,CancellationToken token)=>Write(stream,token);
        static async Task Write(Stream stream,CancellationToken token){await stream.WriteAsync(new byte[32768],token);await Task.Delay(500,token);await stream.WriteAsync(new byte[1048576-32768],token);}
    }
    public static async Task Run(string parent)
    {
        var root=Path.Combine(Path.GetFullPath(parent),"concurrent-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        typeof(App).GetProperty(nameof(App.DataDir))!.SetValue(null,root);Liuzhuan.Utils.Logger.Init(root);LanConfig.Password="isolated-scale-fixture";
        using var server=new ResumableTransferServer();int filePort=Port(),audioPort=Port();int registrations=0;
        server.FileReceiver=(_,_,_,_,_)=>{Interlocked.Increment(ref registrations);return Task.CompletedTask;};server.Start(filePort);
        await using var audio=new AudioTransportServer(new DiagnosticAudioSink());await audio.StartAsync(audioPort);
        using var payload=JsonDocument.Parse(await audio.PairingPayloadAsync("127.0.0.1"));
        using var ws=new ClientWebSocket();await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{audioPort}/audio"),CancellationToken.None);
        async Task Text(object value)=>await ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)),WebSocketMessageType.Text,true,CancellationToken.None);
        await Text(new{v=1,type="hello",token=payload.RootElement.GetProperty("token").GetString(),device="synthetic fixture"});
        async Task<JsonElement> Read(string wanted){var bytes=new byte[8192];using var deadline=new CancellationTokenSource(10000);while(true){var r=await ws.ReceiveAsync(bytes,deadline.Token);var j=JsonDocument.Parse(bytes.AsMemory(0,r.Count)).RootElement.Clone();if(j.GetProperty("type").GetString()==wanted)return j;}}
        await Read("welcome");await Text(new{v=1,type="status",generation=0,ready=true});
        for(int i=0;i<100&&!audio.Snapshot.Ready;i++)await Task.Delay(20);
        await audio.SetForwardingAsync(true);long generation=(await Read("start_forward")).GetProperty("generation").GetInt64();await Text(new{v=1,type="forward_started",generation});
        using var life=new CancellationTokenSource();
        var reader=Task.Run(async()=>{var bytes=new byte[8192];try{while(!life.IsCancellationRequested)await ws.ReceiveAsync(bytes,life.Token);}catch(OperationCanceledException){}catch(WebSocketException){} });
        var frames=Task.Run(async()=>{
            long sequence=0;using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(20));
            try{while(await timer.WaitForNextTickAsync(life.Token)){
                var frame=new byte[AudioProtocol.FrameSize];BinaryPrimitives.WriteUInt16BigEndian(frame,1);BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(4),(ulong)generation);
                BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(12),(ulong)sequence++);BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(36),320);BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(40),640);Array.Fill(frame,(byte)17,44,640);
                await ws.SendAsync(frame,WebSocketMessageType.Binary,true,life.Token);
                if(sequence%40==0)await Text(new{v=1,type="status",generation,ready=true});
            }}catch(OperationCanceledException){}
        });
        using var http=new HttpClient{BaseAddress=new Uri($"http://127.0.0.1:{filePort}"),Timeout=TimeSpan.FromSeconds(20)};
        http.DefaultRequestHeaders.Add("X-Liuzhuan-Auth",LanConfig.PasswordHash);http.DefaultRequestHeaders.Add("X-Liuzhuan-Client",Guid.NewGuid().ToString());
        var chunk=RandomNumberGenerator.GetBytes(1048576);const int chunks=128;
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);for(int i=0;i<chunks;i++)hash.AppendData(chunk);var digest=Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        var chunkHash=Convert.ToHexString(SHA256.HashData(chunk)).ToLowerInvariant();var id=Guid.NewGuid().ToString();
        async Task<JsonElement> Request(string op,HttpMethod method,HttpContent? content=null,long offset=0){using var req=new HttpRequestMessage(method,$"/v1/tasks/{id}?op={op}&offset={offset}"){Content=content};if(op=="chunk")req.Headers.Add("X-Chunk-SHA256",chunkHash);using var response=await http.SendAsync(req);response.EnsureSuccessStatusCode();return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();}
        var watch=Stopwatch.StartNew();
        try {
            await Request("create",HttpMethod.Post,new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new{direction="upload",name="concurrent-fixture.bin",size=(long)chunk.Length*chunks,sha256=digest})));
            for(int i=0;i<chunks;i++){
                if(i==8){using var deadline=new CancellationTokenSource(80);using var req=new HttpRequestMessage(HttpMethod.Put,$"/v1/tasks/{id}?op=chunk&offset={(long)i*chunk.Length}"){Content=new InterruptedContent()};req.Headers.Add("X-Chunk-SHA256",chunkHash);
                    try{using var unused=await http.SendAsync(req,deadline.Token);throw new Exception("Interrupted request unexpectedly completed");}catch(OperationCanceledException){}
                    var status=await Request("status",HttpMethod.Get);if(status.GetProperty("offset").GetInt64()!=(long)i*chunk.Length)throw new Exception("Interrupted chunk advanced durable offset");
                    Console.WriteLine("PASS interrupted slow chunk preserves the previously committed offset while audio remains connected");
                }
                await Request("chunk",HttpMethod.Put,new ByteArrayContent(chunk),(long)i*chunk.Length);await Task.Delay(15);
            }
            await Request("complete",HttpMethod.Post,new ByteArrayContent(Array.Empty<byte>()));
            var final=Directory.GetFiles(Path.Combine(root,"uploads")).Single();using var file=File.OpenRead(final);var actual=Convert.ToHexString(await SHA256.HashDataAsync(file)).ToLowerInvariant();
            if(actual!=digest||registrations!=1||audio.Snapshot.TotalFrames<30||!audio.Snapshot.Receiving)throw new Exception("Concurrent integrity/audio evidence failed");
            var received=audio.Snapshot.TotalFrames;await audio.SetForwardingAsync(false);await Task.Delay(200);
            if(audio.Snapshot.Requested||audio.Snapshot.Receiving)throw new Exception("Audio stop lost during file processing");
            var report=new{FileBytes=(long)chunk.Length*chunks,ReceivedFrames=received,ElapsedMs=watch.Elapsed.TotalMilliseconds,Registrations=registrations,LoopbackOnly=true,NoAudioSaved=true};
            File.WriteAllText(Path.Combine(parent,"concurrency.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine("PASS 128 MiB resumable upload with injected interruption and concurrent production PCM reception; full SHA-256, single registration and manual stop verified");
        }finally{life.Cancel();ws.Abort();try{await Task.WhenAll(reader,frames);}catch(OperationCanceledException){}catch(WebSocketException){} }
    }
}
