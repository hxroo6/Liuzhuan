using System.Collections.ObjectModel;
using System.Windows;

namespace Liuzhuan.Services;

public record TransferEntry(string Id, string Name, string Direction, long Done, long Total, string State, string Error, double BytesPerSecond)
{
    public string Summary => $"{Direction} · {State} · {Utils.FileClassifier.FormatSize(Done)} / {Utils.FileClassifier.FormatSize(Total)}";
    public string Detail => Error.Length > 0 ? Error : $"{Utils.FileClassifier.FormatSize((long)BytesPerSecond)}/s";
}

public static class TransferJournal
{
    public static ObservableCollection<TransferEntry> Entries { get; } = new();
    public static void Report(TransferEntry entry)
    {
        void Update()
        {
            var old = Entries.FirstOrDefault(x => x.Id == entry.Id);
            if (old != null) Entries[Entries.IndexOf(old)] = entry;
            else Entries.Insert(0, entry);
            // 仅淘汰已结束记录，不让仍在传输的任务消失。
            while (Entries.Count > 40)
            {
                var finished = Entries.LastOrDefault(x => x.State is "失败" or "已接收" or "已发送");
                if (finished == null) break;
                Entries.Remove(finished);
            }
        }
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) Update();
        else dispatcher.BeginInvoke(Update);
    }
}
