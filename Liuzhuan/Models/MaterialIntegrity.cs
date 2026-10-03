namespace Liuzhuan.Models;

public sealed record MaterialIntegrityBaseline(string Sha256, long Size, DateTimeOffset RecordedUtc, string SourcePath, MaterialType SourceType);
public enum MaterialIntegrityStatus { NotChecked, Normal, Changed, Missing, Unrecorded, ReadFailed }
