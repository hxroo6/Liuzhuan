using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Liuzhuan;
using Liuzhuan.Models;
using Liuzhuan.Services;
using Liuzhuan.Services.Lan;

var root=Path.Combine(Path.GetTempPath(),"liuzhuan-automation-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var library=Path.Combine(root,"library");Directory.CreateDirectory(library);
typeof(App).GetProperty(nameof(App.DataDir))!.SetValue(null,library);
Liuzhuan.Utils.Logger.Init(root);
var checks=0;
void Check(bool ok,string name) {if(!ok)throw new Exception(name);checks++;Console.WriteLine("PASS "+name);}
void Reject(Action action,string name) {try {action();}catch(ArgumentException){Check(true,name);return;}catch(InvalidOperationException){Check(true,name);return;}throw new Exception("should reject: "+name);}
var rule=new CollectionRule { Name="Phone photos",Type="Image",Source="Android",Device="fixture",Group="照片",Tags=new[]{"手机","参考"} };
using var store=new DataStore();
var document=store.Automation.Save(new AutomationDocument { Rules=new(){rule} });
Check(document.Revision==1,"revision advances after durable save");
Check(new AutomationStore(library).Export().Rules.Single().Group=="照片","rulebook survives reload");
Reject(()=>store.Automation.Save(new AutomationDocument()),"stale revision rejected");
Reject(()=>AutomationStore.Normalize(document with { Rules=new(){rule with {Type="999"}} }),"invalid numeric type rejected");
Reject(()=>AutomationStore.Normalize(document with { Rules=new(){rule with {Group="",Tags=Array.Empty<string>()}} }),"empty action rejected");
Reject(()=>AutomationStore.Normalize(document with { Rules=Enumerable.Range(0,31).Select(i=>rule with { Id=i.ToString() }).ToList() }),"rule cap enforced");
Reject(()=>AutomationStore.Normalize(document with { Rules=new(){rule,rule} }),"duplicate ids rejected");
Reject(()=>AutomationStore.Normalize(document with { Rules=new(){rule with {Device="bad\nname"}} }),"device condition rejects control characters");
var photo=new MaterialItem {Type=MaterialType.Image,DisplayName="示例.jpg",CollectionSource="Android",CollectionDevice="Fixture phone"};
var result=AutomationStore.Evaluate(document,photo);
Check(result.Group=="照片" && result.Tags.Length==2,"type origin and device conditions match case insensitively");
Check(photo.GroupName=="" && photo.Tags.Length==0,"preview does not mutate materials");
Check(AutomationStore.Evaluate(document,withClone("Desktop")).Matches.Length==0,"other origin does not match");
MaterialItem withClone(string source)=>new(){Type=photo.Type,DisplayName=photo.DisplayName,CollectionSource=source,CollectionDevice=photo.CollectionDevice};
photo.GroupName="手动分组";photo.Tags=new[]{"原标签"};store.Automation.Apply(photo);
Check(photo.GroupName=="手动分组" && photo.Tags.Contains("原标签") && photo.Tags.Length==3,"rules preserve manual group and append tags");
var full=new MaterialItem {Type=MaterialType.Image,CollectionSource="Android",CollectionDevice="fixture",Tags=Enumerable.Range(0,20).Select(i=>"tag"+i).ToArray()};
Check(AutomationStore.Evaluate(document,full).Matches.Length==0 && full.Tags.Length==20,"conflicting tag overflow skips whole action");
var text=new MaterialItem {Type=MaterialType.Text,DisplayName="one",TextContent="unique"};store.Add(text);
text.GroupName="manual";store.Add(new MaterialItem{Type=MaterialType.Text,TextContent="unique",DisplayName="two"});
Check(store.Items.Count==1 && store.Items[0].GroupName=="manual","duplicate collection preserves manual organization");
var textOne=Path.Combine(root,"one.txt");var textTwo=Path.Combine(root,"two.txt");File.WriteAllText(textOne,"one");File.WriteAllText(textTwo,"two");
store.Add(new MaterialItem {Type=MaterialType.Text,FilePath=textOne,DisplayName="one.txt"});store.Add(new MaterialItem {Type=MaterialType.Text,FilePath=textTwo,DisplayName="two.txt"});
Check(store.Items.Count==3,"different file-backed text paths are never deduplicated as empty inline text");
Check(store.GetRecentSummaries(50).Count(x=>x.HasFile)==2,"text-file summaries retain file identity for Android save action");
File.WriteAllText(textOne,new string('x',1000000));var textPreview=DragDropService.CreateFromPath(textOne)!;
Check(textPreview.TextContent.Length<5100 && textPreview.Size==1000000,"large text import reads a bounded preview while preserving full source size");
var bad=Path.Combine(root,"bad");Directory.CreateDirectory(bad);File.WriteAllText(Path.Combine(bad,"automation.json"),"not json");var badStore=new AutomationStore(bad);
Reject(()=>badStore.Save(new AutomationDocument()),"corrupt rulebook cannot be overwritten");
Check(File.ReadAllText(Path.Combine(bad,"automation.json"))=="not json","corrupt source preserved");
var migration=Path.Combine(root,"portable.zip");
await MigrationService.ExportAsync(migration,MigrationService.Snapshot(Array.Empty<MaterialItem>(),new MigrationService.Settings { Automation=document }),null,CancellationToken.None);
var prepared=await MigrationService.PrepareImportAsync(migration,library,null,CancellationToken.None);
Check(new AutomationStore(prepared).Export().Rules.Single().Device=="fixture","migration restores portable rules with device condition");
Check(!File.Exists(Path.Combine(prepared,"collection-folders.json")),"migration does not carry local directory subscriptions");

var source=Path.Combine(root,"source");Directory.CreateDirectory(source);File.WriteAllText(Path.Combine(source,"old.txt"),"old");
var collected=new List<string>();using(var folders=new FolderCollectionService(library,path=>{lock(collected)collected.Add(path);})) {
    await folders.AddAsync(source);await folders.ScanAsync();Check(collected.Count==0,"subscription baselines existing files");
    File.WriteAllText(Path.Combine(source,"new.txt"),"new");File.WriteAllText(Path.Combine(source,"ignore.part"),"part");Directory.CreateDirectory(Path.Combine(source,"nested"));File.WriteAllText(Path.Combine(source,"nested","ignored.txt"),"nested");
    await folders.ScanAsync();Check(collected.Count==1 && collected[0].EndsWith("new.txt"),"collects stable direct files and ignores partials and subdirectories");
    await folders.ScanAsync();Check(collected.Count==1,"repeat scans do not duplicate collection");
    File.WriteAllText(Path.Combine(source,"new.txt"),"changed-content");await folders.ScanAsync();Check(collected.Count==2,"changed file collected again by new fingerprint");
    try {await folders.AddAsync(library);throw new Exception("expected unsafe directory rejection");}catch(ArgumentException){Check(true,"library/output directories rejected");}
    await folders.RemoveAsync(folders.Snapshot().Single().Id);File.WriteAllText(Path.Combine(source,"after.txt"),"after");await folders.ScanAsync();Check(collected.Count==2,"removed subscription stops collecting");
    Check(File.Exists(Path.Combine(source,"new.txt")),"stopping subscription preserves original files");
    await folders.AddAsync(source);folders.Start();File.WriteAllText(Path.Combine(source,"watched.txt"),"event driven");
    var deadline=DateTime.UtcNow.AddSeconds(8);while(DateTime.UtcNow<deadline) {lock(collected)if(collected.Any(x=>x.EndsWith("watched.txt")))break;await Task.Delay(100);}
    Check(collected.Any(x=>x.EndsWith("watched.txt")),"real FileSystemWatcher collects without polling");
}

LanConfig.Password="isolated-automation-test";
var httpReserve=new TcpListener(IPAddress.Loopback,0);httpReserve.Start();var httpPort=((IPEndPoint)httpReserve.LocalEndpoint).Port;httpReserve.Stop();
using(var files=new FileHttpServer {ItemLookup=id=>store.Items.FirstOrDefault(x=>x.Id==id)}) {
    MaterialItem? uploaded=null;
    files.FileCollected+=(path,name,origin,device)=>{uploaded=DragDropService.CreateFromPath(path)!;uploaded.DisplayName=name;uploaded.CollectionSource=origin;uploaded.CollectionDevice=device;store.Add(uploaded);};
    files.Start(httpPort);
    using var http=new System.Net.Http.HttpClient {BaseAddress=new Uri($"http://127.0.0.1:{httpPort}"),Timeout=TimeSpan.FromSeconds(5)};
    var bytes=Encoding.UTF8.GetBytes("complete text file\n"+new string('z',6000));
    var response=await http.PostAsync($"/upload?name=folder-text.txt&origin=directory&device=fixture&auth={LanConfig.PasswordHash}",new System.Net.Http.ByteArrayContent(bytes));
    Check(response.IsSuccessStatusCode && uploaded!=null,"real HTTP file-collected event registers a distinct text file");
    Check(uploaded!.CollectionSource=="Directory" && uploaded.CollectionDevice=="fixture","actual upload preserves bounded collection provenance");
    var downloaded=await http.GetByteArrayAsync($"/file/{uploaded.Id}?auth={LanConfig.PasswordHash}");
    Check(downloaded.SequenceEqual(bytes) && uploaded.TextContent.Length<5100,"text-file download returns full original bytes rather than truncated preview");
}
var reservation=new TcpListener(IPAddress.Loopback,0);reservation.Start();var port=((IPEndPoint)reservation.LocalEndpoint).Port;reservation.Stop();
var calls=0;var hub=new WsHub {AutomationProvider=input=>{Interlocked.Increment(ref calls);return new { document };},SnapshotProvider=()=>new(new(){new LanItemSummary { Id="fixture",Group="参考",Tags=new[]{"封面"},Note="note" }},0)};
hub.Start(port,LanConfig.PasswordHash);
async Task Send(ClientWebSocket socket,string type,object data) {await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{v=1,type,id="test-"+Guid.NewGuid(),device="fixture-phone",data})),WebSocketMessageType.Text,true,CancellationToken.None);}
async Task<JsonElement> Receive(ClientWebSocket socket) {using var limit=new CancellationTokenSource(4000);using var bytes=new MemoryStream();var buffer=new byte[4096];WebSocketReceiveResult part;do {part=await socket.ReceiveAsync(buffer,limit.Token);bytes.Write(buffer,0,part.Count);}while(!part.EndOfMessage);return JsonDocument.Parse(bytes.ToArray()).RootElement.Clone();}
try {
    using var socket=new ClientWebSocket();await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"),CancellationToken.None);
    await Send(socket,"automation_request",new{action="get"});await Task.Delay(150);Check(calls==0,"unauthenticated socket cannot access rules");
    await Send(socket,"hello",new{auth=LanConfig.PasswordHash,ts=DateTimeOffset.UtcNow.ToUnixTimeSeconds()});var welcome=await Receive(socket);
    Check(welcome.GetProperty("data").GetProperty("features").EnumerateArray().Any(x=>x.GetString()=="automation_v1"),"capability advertised only with provider");
    await Send(socket,"automation_request",new{action="get"});var reply=await Receive(socket);
    Check(reply.GetProperty("data").GetProperty("result").GetProperty("document").GetProperty("revision").GetInt64()==1,"wire reply uses Android-compatible camelCase nested fields");
    await Send(socket,"automation_request",new{action="get"});var throttle=await Receive(socket);Check(throttle.GetProperty("data").GetProperty("result").TryGetProperty("error",out _),"rapid rule requests receive explicit error");
    await Send(socket,"list_sync",new{});var snapshot=await Receive(socket);var item=snapshot.GetProperty("data").GetProperty("items")[0];Check(item.GetProperty("group").GetString()=="参考" && item.GetProperty("tags")[0].GetString()=="封面","organization metadata survives actual WebSocket snapshot");
    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure,"done",CancellationToken.None);
} finally {hub.Stop();}
Console.WriteLine($"{checks} automation checks passed. Isolated evidence: {root}");
