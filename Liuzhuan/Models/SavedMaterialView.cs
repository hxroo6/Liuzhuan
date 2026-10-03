using System.Text.Json.Serialization;

namespace Liuzhuan.Models;

/// <summary>A reusable filter, containing criteria rather than a fixed set of material IDs.</summary>
public sealed record SavedMaterialView
{
    public string Name { get; init; } = "";
    public string Tab { get; init; } = "Recent";
    public string Keyword { get; init; } = "";
    public string? GroupName { get; init; }
    public string? Tag { get; init; }

    public bool Matches(string tab, string keyword, string? group, string? tag) =>
        Tab == tab && string.Equals(Keyword, keyword.Trim(), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(GroupName, group?.Trim(), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Tag, string.IsNullOrWhiteSpace(tag) ? null : tag.Trim(), StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public string Summary => string.Join(" · ", new[]
    {
        Tab switch { "Favorites" => "收藏", "Image" => "图片", "Text" => "文字", "Video" => "视频", "Audio" => "音频", "Other" => "其他", _ => "全部" },
        GroupName == null ? "" : GroupName.Length == 0 ? "未分组" : "分组：" + GroupName,
        Tag == null ? "" : "标签：" + Tag,
        Keyword.Length == 0 ? "" : "搜索：" + Keyword
    }.Where(x => x.Length > 0));
}
