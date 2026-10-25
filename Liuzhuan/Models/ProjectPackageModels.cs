using Liuzhuan.Services;

namespace Liuzhuan.Models;

public sealed record ProjectPackageSource(string EntryId, string Name, MaterialType Type, string FilePath,
    string TextContent, bool IsFavorite, string GroupName, IReadOnlyList<string> Tags, string Note, DateTimeOffset AddedTime);

/// <summary>Local export snapshot; source paths are never serialized into the public archive.</summary>
public sealed class ProjectPackageSnapshot
{
    public string PackageId { get; }
    public DateTimeOffset CreatedUtc { get; }
    public IReadOnlyList<ProjectPackageSource> Entries { get; }
    internal ProjectPackageSnapshot(string packageId, DateTimeOffset createdUtc, IEnumerable<ProjectPackageSource> entries)
    { PackageId = packageId; CreatedUtc = createdUtc; Entries = Array.AsReadOnly(entries.ToArray()); }
}

public sealed record ProjectPackageEntryPreview(string EntryId, string Name, MaterialType Type, string TextContent,
    bool HasFile, bool IsFavorite, string GroupName, IReadOnlyList<string> Tags, string Note, DateTimeOffset AddedTime);

public sealed record ProjectPackagePreview(string PackageId, string Fingerprint, DateTimeOffset CreatedUtc,
    int ItemCount, int FileCount, long PayloadBytes, IReadOnlyList<ProjectPackageEntryPreview> Entries);

public sealed record ProjectPackageProgress(string Stage, int CompletedItems, int TotalItems,
    long CopiedBytes, long TotalBytes, string CurrentName);

public sealed record ProjectPackageImportRow(string EntryId, string RegistrationKey, string Name, MaterialType Type,
    string TextContent, string FilePath, long Size, bool IsFavorite, string GroupName, IReadOnlyList<string> Tags,
    string Note, DateTime AddedTime);

internal sealed record ProjectPackagePayload(string Path, long Size, string Sha256);

/// <summary>Only created after every archive payload has been checked. Files remain owned recovery evidence.</summary>
public sealed class ProjectPackagePrepared
{
    public string DataRoot { get; }
    public string DirectoryPath { get; }
    public ProjectPackagePreview Preview { get; }
    public IReadOnlyList<ProjectPackageImportRow> Rows { get; }
    internal IReadOnlyList<ProjectPackagePayload> Payloads { get; }
    internal ProjectPackagePrepared(string root, string directory, ProjectPackagePreview preview,
        IEnumerable<ProjectPackageImportRow> rows, IEnumerable<ProjectPackagePayload> payloads)
    {
        DataRoot = root; DirectoryPath = directory; Preview = preview;
        Rows = Array.AsReadOnly(rows.ToArray()); Payloads = Array.AsReadOnly(payloads.ToArray());
    }
    /// <summary>Call on the DataStore owner thread. A fresh object is returned on each retry.</summary>
    public IReadOnlyList<MaterialItem> CreateMaterials() => Array.AsReadOnly(Rows.Select(row => new MaterialItem
    {
        DisplayName = row.Name, Type = row.Type, FilePath = row.FilePath, TextContent = row.TextContent,
        Size = row.Size, IsFavorite = row.IsFavorite, GroupName = row.GroupName, Tags = row.Tags.ToArray(),
        Note = row.Note, AddedTime = row.AddedTime, RegistrationKeys = new[] { row.RegistrationKey },
        CollectionSource = "ProjectPackage", CollectionDevice = "项目素材包"
    }).ToArray());
}
