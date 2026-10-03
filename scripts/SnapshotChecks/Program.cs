using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Liuzhuan.Services.Lan;

var root=Path.Combine(Path.GetTempPath(),"liuzhuan-snapshot-check-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
typeof(Liuzhuan.App).GetProperty(nameof(Liuzhuan.App.DataDir))!.SetValue(null,root);
Liuzhuan.Utils.Logger.Init(root);
var checks=0;
void Check(bool ok,string message){if(!ok)throw new Exception(message);checks++;Console.WriteLine("PASS "+message);}
var reserve=new TcpListener(IPAddress.Loopback,0);reserve.Start();var port=((IPEndPoint)reserve.LocalEndpoint).Port;reserve.Stop();
long sequence=40;var calls=0;var dispatched=false;var ownerThreadId=0;var capturedThreadId=0;
var items=new List<LanItemSummary>();
var ready=new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
var ownerThread=new Thread(()=>{ownerThreadId=Environment.CurrentManagedThreadId;ready.SetResult(Dispatcher.CurrentDispatcher);Dispatcher.Run();}){IsBackground=true};
ownerThread.SetApartmentState(ApartmentState.STA);ownerThread.Start();var owner=await ready.Task;
var hub=new WsHub{SnapshotProvider=()=>{calls++;capturedThreadId=Environment.CurrentManagedThreadId;return new LanListSnapshot(items,sequence);}};
hub.SnapshotDispatcher=action=>owner.Invoke(()=>{dispatched=true;action();});
LanConfig.Password="isolated-only";
var auth=LanConfig.PasswordHash;
hub.Start(port,auth);
try
{
    using var socket=new ClientWebSocket();await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"),CancellationToken.None);
    async Task Send(object message)=>await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)),WebSocketMessageType.Text,true,CancellationToken.None);
    async Task<JsonDocument> Read()
    {
        var buffer=new byte[8192];using var timeout=new CancellationTokenSource(5000);var result=await socket.ReceiveAsync(buffer,timeout.Token);
        return JsonDocument.Parse(buffer.AsMemory(0,result.Count));
    }
    await Send(new{v=1,type="hello",id="h",device="isolated-snapshot",data=new{auth,ts=DateTimeOffset.UtcNow.ToUnixTimeSeconds()}});
    using(var welcome=await Read())Check(welcome.RootElement.GetProperty("type").GetString()=="welcome","snapshot session authenticates normally");
    async Task<JsonDocument> Snapshot(){await Send(new{v=1,type="list_sync",id="s",device="isolated-snapshot",data=new{}});return await Read();}
    using(var empty=await Snapshot())
    {
        var data=empty.RootElement.GetProperty("data");
        Check(data.GetProperty("items").GetArrayLength()==0 && data.GetProperty("sequence").GetInt64()==40,"empty libraries retain the global sequence in actual WS snapshots");
    }
    Check(calls==1 && dispatched,"snapshot capture and reply pass through the owner's dispatch together");
    Check(capturedThreadId==ownerThreadId,"actual WS snapshot capture runs on the configured owner dispatcher");
    items=new(){new LanItemSummary{Id="older",Name="material",Type="Text",Sequence=5,AddedTime=100}};sequence=42;
    using(var nonempty=await Snapshot())
    {
        var data=nonempty.RootElement.GetProperty("data");
        Check(data.GetProperty("sequence").GetInt64()==42 && data.GetProperty("items")[0].GetProperty("sequence").GetInt64()==5,"snapshot sequence comes from the library rather than its first item");
    }
    hub.BroadcastItemDeleted("older",43);
    using(var deletion=await Read())Check(deletion.RootElement.GetProperty("type").GetString()=="item_deleted" && deletion.RootElement.GetProperty("data").GetProperty("sequence").GetInt64()==43,"deletion retains its existing sequence field on the wire");
    hub.BroadcastItemCleared(44);
    using(var clearing=await Read())Check(clearing.RootElement.GetProperty("type").GetString()=="item_cleared" && clearing.RootElement.GetProperty("data").GetProperty("sequence").GetInt64()==44,"clear retains its existing sequence field on the wire");
    sequence=0;items=new();
    using(var restarted=await Snapshot())Check(restarted.RootElement.GetProperty("data").GetProperty("sequence").GetInt64()==0,"fresh server epoch can explicitly publish sequence zero");
    hub.SnapshotDispatcher=action=>owner.Invoke(()=>
    {
        action();
        hub.BroadcastItemAdded(new LanItemSummary{Id="next",Name="next",Type="Text",AddedTime=200,Sequence=1});
    });
    using(var ordered=await Snapshot())Check(ordered.RootElement.GetProperty("type").GetString()=="list_data","snapshot reply is queued before the next owner-thread mutation");
    using(var next=await Read())Check(next.RootElement.GetProperty("type").GetString()=="item_added" && next.RootElement.GetProperty("data").GetProperty("sequence").GetInt64()==1,"next delta follows the snapshot without being lost");
    hub.SnapshotDispatcher=action=>owner.Invoke(action);
    hub.SnapshotProvider=null;hub.ListProvider=()=>new(){new LanItemSummary{Id="legacy",Sequence=7}};
    using(var legacy=await Snapshot())Check(legacy.RootElement.GetProperty("data").GetProperty("sequence").GetInt64()==7,"legacy list-provider integration remains supported");
}
finally{hub.Stop();owner.BeginInvokeShutdown(DispatcherPriority.Send);ownerThread.Join(5000);}
Console.WriteLine($"PASS {checks} actual WS snapshot checks; isolated artifacts: {root}");
