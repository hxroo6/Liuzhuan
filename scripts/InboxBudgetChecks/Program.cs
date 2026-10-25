using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Liuzhuan.Services;
using Liuzhuan.Services.Lan;

static class Program
{
    static int checks;
    static void Check(bool ok,string label){if(!ok)throw new Exception(label);checks++;Console.WriteLine("PASS "+label);}
    static string Id(string key)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
    static string New(string root,string name){var path=Path.Combine(root,name);Directory.CreateDirectory(path);return path;}
    static async Task Refused(Func<Task> action,string message){bool refused=false;try{await action();}catch(Exception ex) when(ex is IOException or InvalidOperationException or InvalidDataException){refused=true;}Check(refused,message);}
    static async Task Main()
    {
        var root=New(Path.GetTempPath(),"liuzhuan-m54-inbox-"+Guid.NewGuid().ToString("N"));
        var now=DateTime.UtcNow;var normal=New(root,"normal");
        var first=new DurableInbox(normal,()=>now);var second=new DurableInbox(normal,()=>now);
        int registrations=0;var clock=Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0,200).Select(i=>(i%2==0?first:second).RegisterAsync("key-"+i,"hash-"+i,_=>{Interlocked.Increment(ref registrations);return Task.CompletedTask;})));
        clock.Stop();
        Check(registrations==200,"two service instances register all distinct text/file keys");
        Check(first.DirectoryScanCount==1&&second.DirectoryScanCount==1,"200 registrations share one directory scan rather than scanning for every receipt");
        await second.RegisterAsync("key-0","hash-0",_=>throw new Exception("stored callback must not run"));
        Check(registrations==200,"stored duplicate remains acknowledged without replaying registration");
        var existing=Path.Combine(normal,"receipts",Id("key-0")+".json");var original=File.ReadAllText(existing);
        await Refused(()=>first.RegisterAsync("key-0","different",_=>Task.CompletedTask),"conflicting fingerprint cannot replace a stored receipt");
        Check(File.ReadAllText(existing)==original,"fingerprint conflict preserves the stored bytes");
        var same=0;await Task.WhenAll(Enumerable.Range(0,8).Select(i=>(i%2==0?first:second).RegisterAsync("same-key","same-hash",async _=>{Interlocked.Increment(ref same);await Task.Delay(30);})));
        Check(same==1,"same-key registration is serialized across independent service instances");
        string? pendingId=null;var attempts=0;
        await Refused(()=>first.RegisterAsync("retry","hash",id=>{pendingId=id;attempts++;throw new IOException("fixture failure");}),"registration failure leaves an unconfirmed intent");
        await second.RegisterAsync("retry","hash",id=>{Check(id==pendingId,"retry preserves the stable registration key");attempts++;return Task.CompletedTask;});
        Check(attempts==2,"a pending receipt retries and confirms only after successful registration");
        var started=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var held=first.RegisterAsync("slow-key","hash",async _=>{started.SetResult(true);await release.Task;});
        await started.Task;
        try{await second.RegisterAsync("independent-key","hash",_=>Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(3));
            Check(!held.IsCompleted,"slow registration does not hold the shared directory gate or block unrelated keys");}
        finally{release.TrySetResult(true);}await held;
        var state=typeof(DurableInbox).GetField("_state",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(first)!;
        Check(((System.Collections.IDictionary)state.GetType().GetField("Keys")!.GetValue(state)!).Count==0,"completed requests release their per-key gate registry");

        var boundedRoot=New(root,"bounded");var boundedA=new DurableInbox(boundedRoot,()=>now,capacity:2);var boundedB=new DurableInbox(boundedRoot,()=>now,capacity:2);
        await boundedA.RegisterAsync("one","hash",_=>Task.CompletedTask);await boundedB.RegisterAsync("two","hash",_=>Task.CompletedTask);
        await Refused(()=>boundedA.RegisterAsync("three","hash",_=>Task.CompletedTask),"shared capacity cannot be exceeded by a second service instance");
        Check(Directory.GetFiles(Path.Combine(boundedRoot,"receipts"),"*.json").Length==2,"capacity rejection writes no extra intent");
        var scans=boundedA.DirectoryScanCount;
        await Refused(()=>boundedB.RegisterAsync("four","hash",_=>Task.CompletedTask),"full receipt budget keeps rejecting new records safely");
        Check(boundedA.DirectoryScanCount==scans,"repeated capacity pressure does not rescan within its cooldown");

        var staleRoot=New(root,"expiry");var receiptDir=New(staleRoot,"receipts");
        string Seed(string key,string raw){var path=Path.Combine(receiptDir,Id(key)+".json");File.WriteAllText(path,raw);File.SetLastWriteTimeUtc(path,now.AddDays(-9));return path;}
        var expired=Path.Combine(receiptDir,Id("expired")+".json");DurableJson.Write(expired,new DurableInbox.Receipt("expired","hash",true,now.AddDays(-9)));File.SetLastWriteTimeUtc(expired,now.AddDays(-9));
        var broken=Seed("broken","{broken");var nullFile=Seed("null","null");
        var oversized=Seed("oversized",new string('x',70000));
        var recent=Path.Combine(receiptDir,Id("recent")+".json");DurableJson.Write(recent,new DurableInbox.Receipt("recent","hash",true,now));File.SetLastWriteTimeUtc(recent,now.AddDays(-9));
        var expiry=new DurableInbox(staleRoot,()=>now,capacity:6);
        await expiry.RegisterAsync("fresh","hash",_=>Task.CompletedTask);
        Check(!File.Exists(expired),"valid idle receipts expire only after the eight-day retention window");
        Check(File.ReadAllText(broken)=="{broken"&&File.ReadAllText(nullFile)=="null","expired corrupt and null records are retained as recovery evidence");
        Check(new FileInfo(oversized).Length==70000,"oversized old records stay untouched without unbounded JSON allocation");
        Check(File.Exists(recent),"recent creation time protects a receipt with incorrectly old file timestamps");
        await Refused(()=>expiry.RegisterAsync("null","hash",_=>Task.CompletedTask),"null receipt refuses overwrite and false acknowledgement");
        Check(File.ReadAllText(nullFile)=="null","failed null recovery does not replace the original bytes");
        var busyRoot=New(root,"busy-expiry");var busy=new DurableInbox(busyRoot,()=>now,capacity:4);
        started=new(TaskCreationOptions.RunContinuationsAsynchronously);release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        var during=busy.RegisterAsync("in-flight","hash",async _=>{started.SetResult(true);await release.Task;});
        await started.Task;now=now.AddDays(9);
        try{await busy.RegisterAsync("trigger","hash",_=>Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(3));
            Check(File.Exists(Path.Combine(busyRoot,"receipts",Id("in-flight")+".json")),"expiry never deletes a request while its registration key is in use");}
        finally{release.TrySetResult(true);}await during;

        if(OperatingSystem.IsWindows()) {
            var caseRoot=New(root,"uppercase-gate");var caseInbox=new DurableInbox(caseRoot,()=>now,capacity:4);
            started=new(TaskCreationOptions.RunContinuationsAsynchronously);release=new(TaskCreationOptions.RunContinuationsAsynchronously);
            var caseWork=caseInbox.RegisterAsync("case-key","hash",async _=>{started.SetResult(true);await release.Task;});await started.Task;
            var lower=Path.Combine(caseRoot,"receipts",Id("case-key")+".json");var upper=Path.Combine(caseRoot,"receipts",Id("case-key").ToUpperInvariant()+".json");
            File.Move(lower,lower+".rename");File.Move(lower+".rename",upper);now=now.AddDays(9);
            try{await caseInbox.RegisterAsync("case-trigger","hash",_=>Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(3));
                Check(File.Exists(upper),"Windows filename casing cannot bypass an active registration gate during cleanup");}
            finally{release.TrySetResult(true);}await caseWork;
        }
        var outsideRoot=New(root,"reconcile");var outside=new DurableInbox(outsideRoot,()=>now,capacity:2);
        await outside.RegisterAsync("owned","hash",_=>Task.CompletedTask);
        var outsidePath=Path.Combine(outsideRoot,"receipts",Id("restored")+".json");DurableJson.Write(outsidePath,new DurableInbox.Receipt("restored","hash",true,now));
        now=now.AddMinutes(1).AddSeconds(1);
        await Refused(()=>outside.RegisterAsync("over","hash",_=>Task.CompletedTask),"activity-driven reconciliation counts externally restored receipts");
        File.Delete(outsidePath);now=now.AddSeconds(2);
        await outside.RegisterAsync("after-remove","hash",_=>Task.CompletedTask);
        Check(Directory.GetFiles(Path.Combine(outsideRoot,"receipts"),"*.json").Length==2,"capacity reconciliation releases only a genuinely free slot");
        Console.WriteLine($"PASS {checks} inbox budget checks; 200 registrations {clock.Elapsed.TotalMilliseconds:F2}ms, scans={first.DirectoryScanCount}; isolated artifacts: {root}");
    }
}
