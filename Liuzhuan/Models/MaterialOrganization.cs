namespace Liuzhuan.Models;

public enum TagsMode
{
    Replace,
    Add,
    Remove
}

/// <summary>Null fields preserve existing values; empty values explicitly clear them.</summary>
public sealed record MaterialOrganizationPatch
{
    public string? GroupName { get; init; }
    public string[]? Tags { get; init; }
    public string? Note { get; init; }
    public TagsMode TagsMode { get; init; } = TagsMode.Replace;
}

/// <summary>Shared validation for local edits and migration packages; invalid values are never truncated.</summary>
public static class MaterialOrganization
{
    public const int MaxGroupLength = 48;
    public const int MaxTags = 20;
    public const int MaxTagLength = 32;
    public const int MaxNoteLength = 4000;

    public static MaterialOrganizationPatch Normalize(string? groupName, IEnumerable<string>? tags, string? note) =>
        Normalize(new MaterialOrganizationPatch
        {
            GroupName = groupName ?? string.Empty,
            Tags = tags?.ToArray() ?? Array.Empty<string>(),
            Note = note ?? string.Empty
        });

    public static MaterialOrganizationPatch Normalize(MaterialOrganizationPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (!Enum.IsDefined(typeof(TagsMode), patch.TagsMode))
            throw new ArgumentException("未知的标签修改方式。", nameof(patch));

        var group = patch.GroupName?.Trim();
        if (group?.Length > MaxGroupLength)
            throw new ArgumentException($"分组名称最多 {MaxGroupLength} 个字符。", nameof(patch));
        if (group?.Any(char.IsControl) == true)
            throw new ArgumentException("分组名称不能包含换行、制表符等控制字符。", nameof(patch));

        var tags = patch.Tags == null ? null : NormalizeTags(patch.Tags);
        if (tags?.Length > MaxTags)
            throw new ArgumentException($"每条素材最多 {MaxTags} 个标签。", nameof(patch));
        if (tags?.Any(x => x.Length > MaxTagLength) == true)
            throw new ArgumentException($"每个标签最多 {MaxTagLength} 个字符。", nameof(patch));
        if (tags?.Any(x => x.Any(c => char.IsControl(c) || c is ',' or '，' or ';' or '；')) == true)
            throw new ArgumentException("单个标签不能包含逗号、分号或控制字符。", nameof(patch));

        if (patch.Note?.Length > MaxNoteLength)
            throw new ArgumentException($"备注最多 {MaxNoteLength} 个字符。", nameof(patch));

        return patch with { GroupName = group, Tags = tags };
    }

    internal static string[] NormalizeTags(IEnumerable<string>? tags) => tags == null
        ? Array.Empty<string>()
        : tags.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
