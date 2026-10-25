namespace Liuzhuan.Services;

public enum TransferTaskCategory { All, Active, Attention, Finished }
public enum TransferTaskDirection { All, Incoming, Outgoing }
public sealed record TransferTaskOverview(int Active, int Attention, int Succeeded, int Cancelled)
{
    public int Total => Active + Attention + Succeeded + Cancelled;
    public string Summary => $"{Active} 项进行中 · {Attention} 项待处理 · {Succeeded} 项完成 · {Cancelled} 项取消";
}

/// <summary>展示查询不改变任务、控制器或可靠恢复记录；未知状态保守归为待处理。</summary>
public static class TransferTaskQuery
{
    public static TransferTaskCategory Category(TransferEntry entry)
    {
        if (entry.State is "正在暂停…" or "正在取消…") return TransferTaskCategory.Active;
        if (entry.IsPaused || entry.State is "已暂停" or "失败" or "已过期") return TransferTaskCategory.Attention;
        if (entry.State is "已接收" or "已发送" or "已取消") return TransferTaskCategory.Finished;
        return entry.State is "传输中" or "处理中" or "等待确认" ? TransferTaskCategory.Active : TransferTaskCategory.Attention;
    }
    public static TransferTaskOverview Overview(IEnumerable<TransferEntry> entries)
    {
        var active=0;var attention=0;var succeeded=0;var cancelled=0;
        foreach(var entry in entries)
            switch(Category(entry)) {
                case TransferTaskCategory.Active:active++;break;
                case TransferTaskCategory.Attention:attention++;break;
                default:if(entry.State=="已取消")cancelled++;else succeeded++;break;
            }
        return new(active,attention,succeeded,cancelled);
    }
    public static bool Matches(TransferEntry entry, TransferTaskCategory category=TransferTaskCategory.All,
        TransferTaskDirection direction=TransferTaskDirection.All, string? query=null)
    {
        if(category!=TransferTaskCategory.All && Category(entry)!=category)return false;
        if(direction==TransferTaskDirection.Incoming && entry.Direction!="手机 → 电脑" ||
            direction==TransferTaskDirection.Outgoing && entry.Direction!="电脑 → 手机")return false;
        var text=query?.Trim();
        return string.IsNullOrEmpty(text) || entry.Name.Contains(text,StringComparison.OrdinalIgnoreCase) ||
            entry.Direction.Contains(text,StringComparison.OrdinalIgnoreCase) || entry.State.Contains(text,StringComparison.OrdinalIgnoreCase);
    }
}
