using System.Diagnostics;
using System.Text.Json;
using Liuzhuan.Services;

var fixtureRoot=Path.Combine(Path.GetTempPath(),"liuzhuan-transfer-policy-"+Guid.NewGuid().ToString("N"));
ConfigService.Root=fixtureRoot;
var checks=0;
void Verify(bool ok,string label){if(!ok)throw new Exception(label);checks++;Console.WriteLine("PASS "+label);}
TransferPolicyService.EnsureLoaded();
await TransferPolicyService.SaveAsync(new("send",1,0,true));
var persisted=JsonSerializer.Deserialize<TransferPolicyOptions>(File.ReadAllText(Path.Combine(fixtureRoot,"transfer-policy.json")));
Verify(persisted is {Priority:"send",SendMiB:1,YieldToAudio:true},"settings are durably saved before becoming effective");
TransferPolicyService.AudioActiveProvider=()=>true;
Verify(TransferPolicyService.Limit(true)==1 && TransferPolicyService.Limit(false)==1,"audio yielding constrains both payload directions without modifying the saved preference");
TransferPolicyService.AudioActiveProvider=()=>false;
Verify(TransferPolicyService.Limit(false)==0,"ending audio restores the unlimited receive budget");
var first=await TransferPolicyService.EnterPayloadAsync(true,default);
var second=await TransferPolicyService.EnterPayloadAsync(false,default);
using var cancel=new CancellationTokenSource();
var discarded=TransferPolicyService.EnterPayloadAsync(false,cancel.Token);cancel.Cancel();
try {using var unexpected=await discarded;throw new Exception("queue cancellation was ignored");}catch(OperationCanceledException){}
Verify(TransferPolicyService.Queued==0,"cancelled payload admission leaves no queued entry");
var order=new List<string>();
var receive=Task.Run(async()=>{using var lease=await TransferPolicyService.EnterPayloadAsync(false,default);lock(order)order.Add("receive");});
while(TransferPolicyService.Queued<1)await Task.Delay(1);
var send=Task.Run(async()=>{using var lease=await TransferPolicyService.EnterPayloadAsync(true,default);lock(order)order.Add("send");});
while(TransferPolicyService.Queued<2)await Task.Delay(1);
first.Dispose();await Task.WhenAll(receive,send);second.Dispose();
Verify(order.SequenceEqual(new[]{"send","receive"}),"saved sending priority is applied when a payload slot becomes available");
var watch=Stopwatch.StartNew();
await Task.WhenAll(TransferPolicyService.PayloadAsync(true,262144,default),TransferPolicyService.PayloadAsync(true,262144,default));
Verify(watch.ElapsedMilliseconds is >=400 and <4000,"parallel uploads share the configured aggregate direction budget");
using var throttleCancel=new CancellationTokenSource();
var pending=TransferPolicyService.PayloadAsync(true,int.MaxValue,throttleCancel.Token);throttleCancel.Cancel();watch.Restart();
try {await pending;throw new Exception("rate wait ignored cancellation");}catch(OperationCanceledException){}
Verify(watch.ElapsedMilliseconds<500,"cancellation promptly interrupts bandwidth waits");
Console.WriteLine($"All {checks} PC transfer policy checks passed; fixture={fixtureRoot}");

namespace Liuzhuan.Services {
    public static class ConfigService {public static string Root="";public static string GetEffectiveDataDir(){Directory.CreateDirectory(Root);return Root;}}
}
