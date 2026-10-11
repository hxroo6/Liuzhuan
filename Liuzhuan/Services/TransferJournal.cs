using System.Collections.ObjectModel;
using System.Windows;
using System.Collections.Concurrent;

namespace Liuzhuan.Services;

public record TransferEntry(string Id, string Name, string Direction, long Done, long Total, string State, string Error, double BytesPerSecond,
    string SavedPath="", bool CanControl=false, bool IsPaused=false)
{
    public string Summary => $"{Direction} · {State} · {Utils.FileClassifier.FormatSize(Done)} / {Utils.FileClassifier.FormatSize(Total)}";
    public string Detail => Error.Length > 0 ? Error : $"{Utils.FileClassifier.FormatSize((long)BytesPerSecond)}/s";
    public bool CanOpenFolder => !string.IsNullOrWhiteSpace(SavedPath);
    public string PauseLabel => IsPaused ? "继续" : "暂停";
}

public static class TransferJournal
{
    public static ObservableCollection<TransferEntry> Entries { get; } = new();
    private static readonly ConcurrentDictionary<string,TransferControl> Controls=new();
    private static readonly ConcurrentDictionary<string,TransferEntry> Latest=new();
    public static TransferControl RegisterControl(string id, Action close, CancellationToken lifetime=default)
    {
        var control=new TransferControl(close,()=> { if(Latest.TryGetValue(id,out var entry)) Report(entry); },lifetime);
        Controls[id]=control;
        return control;
    }
    public static void TogglePause(string id) { if(Controls.TryGetValue(id,out var control)) control.TogglePause(); }
    public static void Cancel(string id) { if(Controls.TryGetValue(id,out var control)) control.Cancel(); }
    public static void EndControl(string id) {
        if(Controls.TryRemove(id,out var control)) control.Dispose();
        if(Latest.TryGetValue(id,out var entry)) Report(entry with {CanControl=false,IsPaused=false});
    }
    public static void Report(TransferEntry entry)
    {
        var terminal=entry.State is "失败" or "已取消" or "已接收" or "已发送" or "处理中";
        if(Controls.TryGetValue(entry.Id,out var control) && !terminal) {
            entry=entry with {CanControl=!control.IsCancelled, IsPaused=control.IsPaused,
                State=control.IsCancelled?"正在取消…":control.IsPaused?"已暂停":"传输中",
                BytesPerSecond=control.IsPaused?0:entry.BytesPerSecond};
        } else entry=entry with {CanControl=false,IsPaused=false};
        Latest[entry.Id]=entry;
        void Update()
        {
            var old = Entries.FirstOrDefault(x => x.Id == entry.Id);
            if (old != null) Entries[Entries.IndexOf(old)] = entry;
            else Entries.Insert(0, entry);
            // 仅淘汰已结束记录，不让仍在传输的任务消失。
            while (Entries.Count > 40)
            {
                var finished = Entries.LastOrDefault(x => x.State is "失败" or "已取消" or "已接收" or "已发送");
                if (finished == null) break;
                Entries.Remove(finished);
                Latest.TryRemove(finished.Id,out _);
            }
        }
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) Update();
        else dispatcher.BeginInvoke(Update);
    }
}
