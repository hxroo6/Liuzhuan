namespace Liuzhuan.Services.Lan;

/// <summary>
/// 素材摘要（LAN 列表同步用，只传元信息，不传内容/文件）
/// </summary>
public class LanItemSummary
{
    [System.Text.Json.Serialization.JsonIgnore] public string SearchText {get;set;}="";
    public bool Favorite { get; set; }
    public long Revision { get; set; }
    public long SourceRevision { get; set; }
    public bool SourceMissing { get; set; }
    public string Source { get; set; } = "";
    public string SourceDevice { get; set; } = "";
    public bool HasFile { get; set; }
    public string Group { get; set; } = "";
    public string[] Tags { get; set; } = Array.Empty<string>();
    public string Note { get; set; } = "";
    public string Id { get; set; } = "";
    public string Type { get; set; } = "Text";
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public long AddedTime { get; set; } // Unix 秒
    public long Sequence { get; set; } // 服务器自增序号（去重 / gap 检测 / 保序）
}
