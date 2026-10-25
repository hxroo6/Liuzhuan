using System.Text.Json.Serialization;

namespace Liuzhuan.Models;

/// <summary>Persistence-only fields. Reading an index or an untrusted manifest must never probe its source paths.</summary>
public sealed class MaterialRecord
{
    public long MetadataRevision { get; set; }
    public string CollectionSource { get; set; } = "Desktop";
    public string CollectionDevice { get; set; } = "本机";
    public string[] RegistrationKeys { get; set; } = Array.Empty<string>();
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public MaterialType Type { get; set; }
    public string FilePath { get; set; } = "";
    public MaterialIntegrityBaseline? IntegrityBaseline { get; set; }
    public string DisplayName { get; set; } = "";
    public string TextContent { get; set; } = "";
    public DateTime AddedTime { get; set; } = DateTime.Now;
    public long Size { get; set; }
    public string ThumbnailPath { get; set; } = "";
    public double Duration { get; set; }
    public bool IsFavorite { get; set; }
    public string GroupName { get; set; } = "";
    public string[] Tags { get; set; } = Array.Empty<string>();
    public string Note { get; set; } = "";
    [JsonIgnore] public bool HasFileSource => Type != MaterialType.Text || !string.IsNullOrWhiteSpace(FilePath);

    public static MaterialRecord Capture(MaterialItem item, bool portable = false) => new()
    {
        MetadataRevision = item.MetadataRevision, CollectionSource = item.CollectionSource, CollectionDevice = item.CollectionDevice,
        RegistrationKeys = portable ? Array.Empty<string>() : (item.RegistrationKeys ?? Array.Empty<string>()).ToArray(),
        Id = item.Id, Type = item.Type, FilePath = item.FilePath, IntegrityBaseline = item.IntegrityBaseline,
        DisplayName = item.DisplayName, TextContent = item.TextContent, AddedTime = item.AddedTime, Size = item.Size,
        ThumbnailPath = item.ThumbnailPath, Duration = item.Duration, IsFavorite = item.IsFavorite,
        GroupName = item.GroupName, Tags = item.Tags, Note = item.Note
    };

    public void Normalize(bool portable = false)
    {
        if (string.IsNullOrWhiteSpace(Id) || !Enum.IsDefined(typeof(MaterialType), Type) ||
            FilePath == null || DisplayName == null || TextContent == null || ThumbnailPath == null ||
            Size < 0 || MetadataRevision < 0 || !double.IsFinite(Duration) || Duration < 0)
            throw new System.IO.InvalidDataException("素材索引字段无效，原文件已保留。");
        var organization = MaterialOrganization.Normalize(GroupName, Tags, Note);
        GroupName = organization.GroupName!; Tags = organization.Tags!; Note = organization.Note!;
        RegistrationKeys = portable ? Array.Empty<string>() : (RegistrationKeys ?? Array.Empty<string>()).Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.Ordinal).ToArray();
        CollectionSource ??= "Desktop"; CollectionDevice ??= "本机";
    }

    public MaterialItem CreateMaterial() => MaterialItem.CreateWithoutSourceProbe(item =>
    {
        item.Id = Id; item.Type = Type; item.FilePath = FilePath; item.IntegrityBaseline = IntegrityBaseline;
        item.DisplayName = DisplayName; item.TextContent = TextContent; item.AddedTime = AddedTime; item.Size = Size;
        item.ThumbnailPath = ThumbnailPath; item.Duration = Duration; item.IsFavorite = IsFavorite;
        item.GroupName = GroupName; item.Tags = Tags; item.Note = Note; item.MetadataRevision = MetadataRevision;
        item.CollectionSource = CollectionSource; item.CollectionDevice = CollectionDevice;
        item.RegistrationKeys = (RegistrationKeys ?? Array.Empty<string>()).ToArray();
    });
}
