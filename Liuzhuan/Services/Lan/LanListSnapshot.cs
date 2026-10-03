namespace Liuzhuan.Services.Lan;

/// <summary>Materials and their global sequence captured together on the library's owner thread.</summary>
public sealed record LanListSnapshot(List<LanItemSummary> Items, long Sequence);
