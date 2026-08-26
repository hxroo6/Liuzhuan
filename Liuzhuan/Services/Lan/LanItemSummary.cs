namespace Liuzhuan.Services.Lan;

/// <summary>
/// 素材摘要（LAN 列表同步用，只传元信息，不传内容/文件）
/// </summary>
public class LanItemSummary
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "Text";
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public long AddedTime { get; set; } // Unix 秒
    public long Sequence { get; set; } // 服务器自增序号（去重 / gap 检测 / 保序）
}
