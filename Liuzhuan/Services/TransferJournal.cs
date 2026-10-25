using System.Collections.ObjectModel;
using System.Windows;
using System.Collections.Concurrent;

namespace Liuzhuan.Services;

public record TransferEntry(string Id, string Name, string Direction, long Done, long Total, string State, string Error, double BytesPerSecond,
    string SavedPath="", bool CanControl=false, bool IsPaused=false)
{
    public long UpdatedUtcMs {get;init;}=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public string Summary => $"{Direction} · {State} · {Utils.FileClassifier.FormatSize(Done)} / {Utils.FileClassifier.FormatSize(Total)}";
    public string Detail => Error.Length > 0 ? Error : $"{Utils.FileClassifier.FormatSize((long)BytesPerSecond)}/s";
    public bool CanOpenFolder => !string.IsNullOrWhiteSpace(SavedPath);
    public string PauseLabel => IsPaused ? "继续" : "暂停";
}

public static class TransferJournal
{
    private static string? _file;
    private static readonly object PersistenceGate=new();
    private static Timer? _saveTimer;
    private static bool _historyUnavailable;
    private static readonly ConcurrentDictionary<string,(Action Pause,Action Cancel)> PersistentControls=new();
    public static void Initialize(string directory)
    {
        lock(PersistenceGate) {
            if(_file!=null)return;
            _file=System.IO.Path.Combine(directory,"transfer-history.json");
            try {foreach(var entry in DurableJson.Read<List<TransferEntry>>(_file)??new()) {
                var interrupted=entry.State is not ("失败" or "已取消" or "已接收" or "已发送" or "已过期");
                var restored=entry with{CanControl=false,BytesPerSecond=0,State=interrupted?(entry.IsPaused?"已暂停":"等待恢复"):entry.State};
                Latest[entry.Id]=restored;Entries.Add(restored);
            }}catch(Exception ex){_historyUnavailable=true;Utils.Logger.Error("Transfer history unavailable: {0}",ex.GetType().Name);}
            _saveTimer=new Timer(_=>SaveHistory(),null,Timeout.Infinite,Timeout.Infinite);
        }
    }
    private static void SaveHistory()
    {
        lock(PersistenceGate) {
            if(_file==null || _historyUnavailable)return;
            try {DurableJson.Write(_file,Latest.Values.OrderByDescending(x=>x.UpdatedUtcMs).ToArray());}
            catch(Exception ex){Utils.Logger.Error("Transfer history save failed: {0}",ex.GetType().Name);}
        }
    }
    public static void Flush()=>SaveHistory();
    public static bool HasPending => Latest.Values.Any(x=>x.CanControl || x.State is "等待恢复" or "已暂停" or "处理中" or "传输中" or "等待确认");
    public static void RegisterPersistentControl(string id,Action pause,Action cancel)=>PersistentControls[id]=(pause,cancel);
    public static void EndPersistentControl(string id)=>PersistentControls.TryRemove(id,out _);
    public static ObservableCollection<TransferEntry> Entries { get; } = new();
    private static readonly ConcurrentDictionary<string,TransferControl> Controls=new();
    private static readonly ConcurrentDictionary<string,TransferEntry> Latest=new();
    private static readonly object UiUpdatesGate=new();
    private static readonly HashSet<string> PendingUiIds=new(StringComparer.Ordinal);
    private static bool _uiUpdateScheduled;
    public static TransferControl RegisterControl(string id, Action close, CancellationToken lifetime=default)
    {
        TransferControl? control=null;
        control=new TransferControl(close,()=> { if(Latest.TryGetValue(id,out var entry)) ReportCore(entry,entry,control); },lifetime);
        lock(UiUpdatesGate)Controls[id]=control;
        return control;
    }
    public static void TogglePause(string id) { if(PersistentControls.TryGetValue(id,out var actions))actions.Pause();else if(Controls.TryGetValue(id,out var control)) control.TogglePause(); }
    public static void Cancel(string id) { if(PersistentControls.TryGetValue(id,out var actions))actions.Cancel();else if(Controls.TryGetValue(id,out var control)) control.Cancel(); }
    public static void EndControl(string id) {
        TransferControl? control;
        lock(UiUpdatesGate)Controls.TryRemove(id,out control);
        control?.Dispose();
        if(Latest.TryGetValue(id,out var entry)) Report(entry with {CanControl=false,IsPaused=false});
    }
    public static void Report(TransferEntry entry)=>ReportCore(entry);
    private static void ReportCore(TransferEntry entry,TransferEntry? expected=null,TransferControl? expectedControl=null)
    {
        var terminal=entry.State is "失败" or "已取消" or "已接收" or "已发送" or "已过期" or "处理中";
        if(Controls.TryGetValue(entry.Id,out var control) && !terminal) {
            entry=entry with {CanControl=!control.IsCancelled, IsPaused=control.IsPaused,
                State=control.IsCancelled?"正在取消…":control.IsPaused?"已暂停":"传输中",
                BytesPerSecond=control.IsPaused?0:entry.BytesPerSecond};
        } else if(PersistentControls.ContainsKey(entry.Id))entry=entry with{CanControl=true};
        else entry=entry with {CanControl=false,IsPaused=false};
        entry=entry with{UpdatedUtcMs=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()};
        bool schedule;
        lock(UiUpdatesGate) {
            // A control notification may have read Latest before a terminal report or
            // disposal. Publish only if both its original entry and controller still own this ID.
            if(expected!=null && (!Latest.TryGetValue(entry.Id,out var current) || !ReferenceEquals(current,expected) ||
                !Controls.TryGetValue(entry.Id,out var owner) || !ReferenceEquals(owner,expectedControl)))return;
            Latest[entry.Id]=entry;
            PendingUiIds.Add(entry.Id);
            schedule=!_uiUpdateScheduled;
            _uiUpdateScheduled=true;
        }
        _saveTimer?.Change(terminal?0:1000,Timeout.Infinite);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) DrainUiUpdates();
        else if(schedule && !dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(DrainUiUpdates));
    }
    private static void DrainUiUpdates()
    {
        // One posted action handles the newest value per ID. A queued old progress event
        // must never restore stale controls or overwrite a terminal state applied on the UI.
        lock(UiUpdatesGate)
        {
            var ids=PendingUiIds.ToArray();PendingUiIds.Clear();_uiUpdateScheduled=false;
            foreach(var id in ids) {
                if(!Latest.TryGetValue(id,out var entry))continue;
                var old=Entries.FirstOrDefault(x=>x.Id==id);
                if(old!=null)Entries[Entries.IndexOf(old)]=entry;
                else Entries.Insert(0,entry);
            }
            // 仅淘汰已结束记录，不让仍在传输的任务消失。
            while (Entries.Count > 40)
            {
                var finished = Entries.LastOrDefault(x => !x.CanControl && !x.IsPaused && x.State is "失败" or "已取消" or "已接收" or "已发送" or "已过期");
                if (finished == null) break;
                Entries.Remove(finished);
                // Latest mutations and trimming share the same gate. A stale UI record
                // cannot evict a newly active version of this ID from persistence.
                Latest.TryRemove(finished.Id,out _);
            }
        }
    }
}
