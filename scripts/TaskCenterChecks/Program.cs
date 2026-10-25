using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Liuzhuan.Services;

internal static class Program
{
    private static int checks;
    private static void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS "+message);checks++;}
    private static TransferEntry Entry(string id,string state="传输中",string name="demo.mp4",string direction="手机 → 电脑")=>new(id,name,direction,0,100,state,"private-error-text",0,"private-path");
    private static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}
    [STAThread] private static void Main()
    {
        var root=Path.Combine(Path.GetTempPath(),"liuzhuan-m55-task-center-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var application=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        TransferJournal.Initialize(root);
        Check(TransferTaskQuery.Category(Entry("a"))==TransferTaskCategory.Active,"live transfer is active");
        Check(TransferTaskQuery.Category(Entry("a") with {IsPaused=true})==TransferTaskCategory.Attention,"pause overrides active-looking state");
        Check(new[]{"失败","已过期","等待恢复","等待续传","future-state"}.All(s=>TransferTaskQuery.Category(Entry("a",s))==TransferTaskCategory.Attention),"failed, expired, waiting and unknown states stay actionable");
        Check(new[]{"已接收","已发送","已取消"}.All(s=>TransferTaskQuery.Category(Entry("a",s))==TransferTaskCategory.Finished),"finished filter includes successful and cancelled tasks");
        var summary=TransferTaskQuery.Overview(new[]{Entry("a"),Entry("b","失败"),Entry("c","已发送"),Entry("d","已取消")});
        Check(summary.Total==4&&summary.Active==1&&summary.Attention==1&&summary.Succeeded==1&&summary.Cancelled==1,"summary counts are mutually exclusive and do not label cancellation as success");
        Check(TransferTaskQuery.Matches(Entry("a"),query:" DEMO ")&&!TransferTaskQuery.Matches(Entry("a"),query:"private-"),"search trims and matches case-insensitively without private path or error contents");
        Check(TransferTaskQuery.Matches(Entry("a"),direction:TransferTaskDirection.Incoming)&&!TransferTaskQuery.Matches(Entry("a"),direction:TransferTaskDirection.Outgoing),"direction filter uses local receiving and sending semantics");
        Check(!TransferTaskQuery.Matches(Entry("a","已发送"),TransferTaskCategory.Active)&&TransferTaskQuery.Matches(Entry("a","已发送"),TransferTaskCategory.Finished,query:"已发送"),"status and text filters compose without changing entries");
        TransferJournal.RegisterControl("burst",()=>{});
        TransferJournal.Report(Entry("burst"));
        var posted=0;application.Dispatcher.Hooks.OperationPosted+=(_,args)=>{if(args.Operation.Priority==DispatcherPriority.Background)Interlocked.Increment(ref posted);};
        Task.Run(()=>{for(var i=0;i<10000;i++)TransferJournal.Report(Entry("burst") with {Done=i});}).GetAwaiter().GetResult();
        Check(posted==1,"10000 blocked-dispatcher reports schedule one coalesced UI operation");
        TransferJournal.Report(Entry("burst","已接收") with {Done=100,Total=100});TransferJournal.EndControl("burst");
        Pump();
        var terminal=TransferJournal.Entries.Single(x=>x.Id=="burst");
        Check(terminal.State=="已接收"&&!terminal.CanControl&&!terminal.IsPaused,"queued progress cannot revive controls or overwrite a UI-applied terminal state");
        TransferJournal.Flush();
        var persisted=JsonSerializer.Deserialize<List<TransferEntry>>(File.ReadAllText(Path.Combine(root,"transfer-history.json")))!;
        Check(persisted.Single(x=>x.Id=="burst").State=="已接收","persisted history agrees with terminal UI after draining stale progress");
        TransferJournal.RegisterPersistentControl("protected",()=>{},()=>{});
        TransferJournal.Report(Entry("protected","失败"));
        for(var i=0;i<55;i++)TransferJournal.Report(Entry("finished-"+i,"已接收"));
        Check(TransferJournal.Entries.Any(x=>x.Id=="protected"&&x.CanControl),"history trimming retains a failed task with a live recovery control");
        Check(TransferJournal.Entries.Count<=40,"only finished history is trimmed to the display budget");
        // Publish a terminal version on a worker, then replace it with a recoverable task on UI
        // before dispatch. The old closure previously deleted the newer persistence record.
        Task.Run(()=>TransferJournal.Report(Entry("replacement","已接收"))).GetAwaiter().GetResult();
        TransferJournal.RegisterPersistentControl("replacement",()=>{},()=>{});TransferJournal.Report(Entry("replacement","等待续传"));
        for(var i=0;i<45;i++)TransferJournal.Report(Entry("more-"+i,"已发送"));
        Pump();TransferJournal.Flush();
        persisted=JsonSerializer.Deserialize<List<TransferEntry>>(File.ReadAllText(Path.Combine(root,"transfer-history.json")))!;
        Check(persisted.Any(x=>x.Id=="replacement"&&x.State=="等待续传"&&x.CanControl),"stale eviction cannot drop a newer recoverable record from persistence");
        void DelayedControlNotification(string id,bool disposeBeforeRelease)
        {
            var control=TransferJournal.RegisterControl(id,()=>{});
            TransferJournal.Report(Entry(id));
            var gate=typeof(TransferControl).GetField("_gate",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(control)!;
            var changed=(Action)typeof(TransferControl).GetField("_changed",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(control)!;
            Exception? callbackError=null;
            var callback=new Thread(()=>{try{changed();}catch(Exception ex){callbackError=ex;}}){IsBackground=true};
            lock(gate)
            {
                callback.Start();
                // Pause the actual production notification after it captured the old Latest
                // but before ReportCore finishes reading this controller's pause state.
                if(!SpinWait.SpinUntil(()=>callback.ThreadState.HasFlag(System.Threading.ThreadState.WaitSleepJoin),3000))
                    throw new Exception("Production control callback did not reach its held state lock");
                TransferJournal.Report(Entry(id,"已接收") with {Done=100,Total=100});
                if(disposeBeforeRelease)TransferJournal.EndControl(id);
            }
            if(!callback.Join(3000))throw new Exception("Production control callback did not resume");
            if(callbackError!=null)throw new Exception("Production control callback failed",callbackError);
            Pump();TransferJournal.Flush();
            var visible=TransferJournal.Entries.Single(x=>x.Id==id);
            var saved=JsonSerializer.Deserialize<List<TransferEntry>>(File.ReadAllText(Path.Combine(root,"transfer-history.json")))!.Single(x=>x.Id==id);
            Check(visible.State=="已接收"&&!visible.CanControl&&!visible.IsPaused&&saved.State=="已接收"&&!saved.CanControl,
                disposeBeforeRelease?"old controller notification after terminal plus disposal cannot restore active controls in UI or persistence":
                    "old controller notification cannot replace a newer terminal entry even while its controller remains registered");
            if(!disposeBeforeRelease)TransferJournal.EndControl(id);
        }
        DelayedControlNotification("late-notify-terminal",false);
        DelayedControlNotification("late-notify-disposed",true);
        TransferJournal.EndPersistentControl("protected");TransferJournal.EndPersistentControl("replacement");
        Console.WriteLine($"All {checks} task center checks passed. Isolated evidence: {root}");application.Shutdown();
    }
}
namespace Liuzhuan.Utils
{
    // Formatting and logging wiring are unrelated to queue ordering and filters.
    internal static class FileClassifier {public static string FormatSize(long value)=>value+" B";}
    internal static class Logger {public static void Error(string format,params object[] values){}}
}
