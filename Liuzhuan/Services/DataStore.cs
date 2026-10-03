using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Liuzhuan.Models;
using Liuzhuan.Utils;

namespace Liuzhuan.Services;

/// <summary>
/// 数据持久化层 — JSON 文件本地存储
/// 数据目录：项目目录/data/
/// 数据文件：data.json
/// </summary>
public class DataStore : IDisposable
{
    private static string GetDataDir() => ConfigService.GetEffectiveDataDir();
    private static string DataFile => Path.Combine(GetDataDir(), "data.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ObservableCollection<MaterialItem> _items = new();
    private readonly object _saveLock = new();
    private Timer? _saveTimer;
    private bool _disposed;

    // 删除撤回历史（最多50步）
    private sealed record RemovedItem(MaterialItem Item, int OriginalIndex);
    private sealed record UndoStep(List<RemovedItem> Removed, string[] OriginalOrder);
    private readonly List<UndoStep> _undoStack = new();
    private const int MaxUndoSteps = 50;

    // 变更序号（用于 LAN 增量同步的 gap 检测；内存计数，重启归零）
    private long _sequence;

    /// <summary>当前变更序号</summary>
    public long CurrentSequence => _sequence;

    private long NextSequence() => System.Threading.Interlocked.Increment(ref _sequence);

    /// <summary>所有素材的 observable 集合</summary>
    public ObservableCollection<MaterialItem> Items => _items;

    /// <summary>最近 N 条素材摘要（LAN 列表同步用，轻量不传内容）</summary>
    public List<Services.Lan.LanItemSummary> GetRecentSummaries(int count)
    {
        var seq = _sequence;
        return _items
            .Take(count)
            .Select(x => new Services.Lan.LanItemSummary
            {
                Id = x.Id,
                Type = x.Type.ToString(),
                Name = x.DisplayName,
                Size = x.Size,
                AddedTime = new DateTimeOffset(x.AddedTime).ToUnixTimeSeconds(),
                Sequence = seq
            })
            .ToList();
    }

    public DataStore()
    {
        Directory.CreateDirectory(GetDataDir());
        Load();
    }

    /// <summary>素材新增/置顶事件（用于 LAN 广播）</summary>
    public event Action<MaterialItem>? ItemAdded;

    /// <summary>素材删除事件（用于 LAN 广播 item_deleted）</summary>
    public event Action<string>? ItemRemoved;

    /// <summary>素材清空事件（用于 LAN 广播 item_cleared）</summary>
    public event Action? ItemCleared;

    /// <summary>添加素材（去重：同路径/同文本内容不重复添加）</summary>
    public MaterialItem Add(MaterialItem item)
    {
        item.RefreshSourceStatus();
        NextSequence(); // 每次变更递增序号（去重置顶也算一次变更）
        // 去重检查
        if (item.IsFile)
        {
            var existing = _items.FirstOrDefault(x => x.IsFile &&
                string.Equals(x.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                // 已存在则更新时间，移到最前
                _items.Remove(existing);
                existing.AddedTime = DateTime.Now;
                _items.Insert(0, existing);
                ScheduleSave();
                Logger.Run("DataStore: duplicate file re-added (moved to top): {0}", item.FilePath);
                ItemAdded?.Invoke(existing);
                return existing;
            }
        }
        else
        {
            var existing = _items.FirstOrDefault(x => !x.IsFile && x.TextContent == item.TextContent);
            if (existing != null)
            {
                _items.Remove(existing);
                existing.AddedTime = DateTime.Now;
                _items.Insert(0, existing);
                ScheduleSave();
                Logger.Run("DataStore: duplicate text re-added (moved to top)");
                ItemAdded?.Invoke(existing);
                return existing;
            }
        }

        _items.Insert(0, item);
        ScheduleSave();
        Logger.Run("DataStore: added item Type={0} Name={1}", item.Type, item.DisplayName);
        ItemAdded?.Invoke(item);
        return item;
    }

    /// <summary>删除单条素材（仅删索引，不动源文件，记录撤回）</summary>
    public void Remove(string id)
    {
        var item = _items.FirstOrDefault(x => x.Id == id);
        if (item != null)
        {
            RemoveItems(new List<MaterialItem> { item });
            Logger.Run("DataStore: removed item Id={0} Name={1}", id, item.DisplayName);
        }
    }

    /// <summary>批量删除多条素材（记录撤回）</summary>
    public void RemoveMultiple(List<string> ids)
    {
        // Capture in library order, independently of selection order or repeated IDs.
        var selectedIds = ids.ToHashSet(StringComparer.Ordinal);
        var removed = _items.Where(x => selectedIds.Contains(x.Id)).ToList();
        if (removed.Count > 0)
        {
            RemoveItems(removed);
            Logger.Run("DataStore: removed {0} items (batch)", removed.Count);
        }
    }

    /// <summary>撤回最后一次删除操作</summary>
    public bool Undo()
    {
        if (_undoStack.Count == 0) return false;

        var step = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);

        var liveIds = _items.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var restoredCount = 0;
        // Older entries first keeps both insertion anchors and incremental broadcast order stable.
        foreach (var removed in step.Removed.AsEnumerable().Reverse())
        {
            var item = removed.Item;
            if (!liveIds.Add(item.Id)) continue;

            _items.Insert(GetRestoreIndex(step.OriginalOrder, removed.OriginalIndex), item);
            NextSequence();
            ItemAdded?.Invoke(item);
            restoredCount++;
        }

        if (restoredCount > 0) ScheduleSave();
        Logger.Run("DataStore: undo restored {0} items", restoredCount);
        return true;
    }

    /// <summary>是否有可撤回的操作</summary>
    public bool CanUndo => _undoStack.Count > 0;

    /// <summary>获取可撤回的步数</summary>
    public int UndoCount => _undoStack.Count;

    private void PushUndo(List<MaterialItem> removedItems)
    {
        var removedSet = removedItems.ToHashSet();
        var removed = _items.Select((item, index) => new RemovedItem(item, index))
            .Where(x => removedSet.Contains(x.Item)).ToList();
        _undoStack.Add(new UndoStep(removed, _items.Select(x => x.Id).ToArray()));
        if (_undoStack.Count > MaxUndoSteps)
            _undoStack.RemoveAt(0);
    }

    private int GetRestoreIndex(string[] originalOrder, int originalIndex)
    {
        // Surviving neighbours preserve the old order even when new materials arrived since deletion.
        for (var i = originalIndex + 1; i < originalOrder.Length; i++)
        {
            var next = _items.FirstOrDefault(x => x.Id == originalOrder[i]);
            if (next != null) return _items.IndexOf(next);
        }
        for (var i = originalIndex - 1; i >= 0; i--)
        {
            var previous = _items.FirstOrDefault(x => x.Id == originalOrder[i]);
            if (previous != null) return _items.IndexOf(previous) + 1;
        }
        return _items.Count;
    }

    private void RemoveItems(List<MaterialItem> removed, bool allowGlobalClear = false)
    {
        PushUndo(removed);
        foreach (var item in removed) _items.Remove(item);
        ScheduleSave();

        if (allowGlobalClear && _items.Count == 0)
        {
            NextSequence();
            ItemCleared?.Invoke();
            return;
        }

        // Every transmitted delta gets its own sequence; partial clears must keep other IDs on phones.
        foreach (var item in removed)
        {
            NextSequence();
            ItemRemoved?.Invoke(item.Id);
        }
    }

    /// <summary>清空指定分类的素材（记录撤回，收藏项不受影响）</summary>
    public void ClearByType(MaterialType type)
    {
        var toRemove = _items.Where(x => x.Type == type && !x.IsFavorite).ToList();
        if (toRemove.Count > 0)
        {
            RemoveItems(toRemove, allowGlobalClear: true);
            Logger.Run("DataStore: cleared type={0}, count={1} (favorites kept)", type, toRemove.Count);
        }
    }

    /// <summary>清空所有素材（记录撤回，收藏项不受影响）</summary>
    public void ClearAll()
    {
        var toRemove = _items.Where(x => !x.IsFavorite).ToList();
        if (toRemove.Count > 0)
        {
            RemoveItems(toRemove, allowGlobalClear: true);
            Logger.Run("DataStore: cleared all, count={0} (favorites kept)", toRemove.Count);
        }
    }

    /// <summary>收藏/取消收藏素材</summary>
    public void SetFavorite(string id, bool favorite)
    {
        var item = _items.FirstOrDefault(x => x.Id == id);
        if (item != null)
        {
            item.IsFavorite = favorite;
            ScheduleSave();
            Logger.Run("DataStore: set favorite={0} for {1}", favorite, item.DisplayName);
        }
    }

    /// <summary>批量收藏/取消收藏</summary>
    public void SetFavoriteMultiple(List<string> ids, bool favorite)
    {
        foreach (var id in ids)
        {
            var item = _items.FirstOrDefault(x => x.Id == id);
            if (item != null) item.IsFavorite = favorite;
        }
        ScheduleSave();
        Logger.Run("DataStore: set favorite={0} for {1} items", favorite, ids.Count);
    }

    /// <summary>修改电脑端组织信息；先验证整个批次，成功后保存，不产生 LAN 增量或调整顺序。</summary>
    public int UpdateOrganization(IEnumerable<string> ids, MaterialOrganizationPatch patch)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var normalized = MaterialOrganization.Normalize(patch);
        var selectedIds = ids.ToHashSet(StringComparer.Ordinal);
        var updates = new List<(MaterialItem Item, MaterialOrganizationPatch Value)>();
        foreach (var item in _items.Where(x => selectedIds.Contains(x.Id)))
        {
            var tags = normalized.Tags == null ? item.Tags : normalized.TagsMode switch
            {
                TagsMode.Add => item.Tags.Concat(normalized.Tags).ToArray(),
                TagsMode.Remove => item.Tags.Except(normalized.Tags, StringComparer.OrdinalIgnoreCase).ToArray(),
                _ => normalized.Tags
            };
            // Adding tags may exceed the limit on just one item; validate every result before touching any.
            var value = MaterialOrganization.Normalize(normalized.GroupName ?? item.GroupName, tags, normalized.Note ?? item.Note);
            if (item.GroupName != value.GroupName || item.Note != value.Note ||
                !item.Tags.SequenceEqual(value.Tags!, StringComparer.Ordinal))
                updates.Add((item, value));
        }

        foreach (var (item, value) in updates)
        {
            item.GroupName = value.GroupName!;
            item.Tags = value.Tags!;
            item.Note = value.Note!;
        }
        if (updates.Count > 0)
        {
            ScheduleSave();
            Logger.Run("DataStore: updated organization for {0} items", updates.Count);
        }
        return updates.Count;
    }

    /// <summary>主动检查来源并刷新卡片状态；失效素材保留索引和整理信息。</summary>
    public int RefreshSourceStatuses(IEnumerable<string>? ids = null)
    {
        var selectedIds = ids?.ToHashSet(StringComparer.Ordinal);
        var missing = 0;
        foreach (var item in _items.Where(x => selectedIds == null || selectedIds.Contains(x.Id)))
        {
            item.RefreshSourceStatus();
            if (item.IsSourceMissing) missing++;
        }
        return missing;
    }

    /// <summary>界面线程应用后台检查结果；过期路径不写回，也不重复访问磁盘。</summary>
    public int ApplySourceStatuses(IReadOnlyList<SourceRelinkStatus> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var missing = 0;
        var appliedIds = new HashSet<string>(StringComparer.Ordinal);
        var live = _items.ToLookup(x => x.Id, StringComparer.Ordinal);
        foreach (var result in results)
        {
            if (result == null) continue;
            var item = live[result.Id].FirstOrDefault();
            if (item == null || item.FilePath != result.OriginalPath || !appliedIds.Add(item.Id)) continue;
            item.ApplySourceStatus(result.Exists);
            if (item.IsSourceMissing) missing++;
        }
        return missing;
    }

    public int ApplyIntegrityResults(IReadOnlyList<IntegrityResult> results)
    {
        var live = _items.ToLookup(x => x.Id, StringComparer.Ordinal);
        var applied = 0;
        foreach (var result in results)
        {
            var item = live[result.Source.Id].FirstOrDefault();
            if (item == null || !IntegrityService.MatchesSource(item, result.Source)) continue;
            item.ApplyIntegrityStatus(result.Status, result.Message + $"\n检查时间：{result.CheckedUtc.LocalDateTime:yyyy-MM-dd HH:mm:ss}");
            applied++;
        }
        return applied;
    }

    /// <summary>仅用于用户确认后的结果；先完整验证来源版本、基线版本及当前文件元信息。</summary>
    public int SetIntegrityBaselines(IEnumerable<IntegrityResult> results)
    {
        var updates = new List<(MaterialItem Item, MaterialIntegrityBaseline Baseline)>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var result in results)
        {
            if (result == null || !result.CanRecord || !ids.Add(result.Source.Id)) throw new ArgumentException("请选择可记录且不重复的检查结果。");
            var item = _items.FirstOrDefault(x => x.Id == result.Source.Id);
            if (item == null || !IntegrityService.MatchesSource(item, result.Source)) throw new InvalidOperationException("素材来源或基线已变化，请重新检查。");
            var info = new FileInfo(item.FilePath);
            if (!info.Exists || info.Length != result.Size || info.LastWriteTimeUtc != result.LastWriteUtc)
                throw new InvalidOperationException("文件在检查后已变化或不可访问，请重新检查再记录。");
            var baseline = IntegrityService.NormalizeBaseline(new(result.Sha256!, result.Size, result.CheckedUtc, item.FilePath, item.Type))!;
            updates.Add((item, baseline));
        }
        foreach (var (item, baseline) in updates) item.IntegrityBaseline = baseline;
        if (updates.Count > 0) ScheduleSave();
        return updates.Count;
    }
    public int ClearIntegrityBaselines(IEnumerable<string> ids)
    {
        var selected = ids.ToHashSet(StringComparer.Ordinal);
        var changed = 0;
        foreach (var item in _items.Where(x => selected.Contains(x.Id) && x.IntegrityBaseline != null)) { item.IntegrityBaseline = null; changed++; }
        if (changed > 0) ScheduleSave();
        return changed;
    }

    /// <summary>用户确认后的来源修复；不移动素材、改 ID 或复制/删除来源。</summary>
    public MaterialItem RelinkSource(SourceRelinkPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        RelinkSources(new[] { plan });
        return _items.First(x => x.Id == plan.Source.Id);
    }

    /// <summary>先验证整个确认批次，再更新路径并逐条发既有 ItemAdded 增量。</summary>
    public int RelinkSources(IEnumerable<SourceRelinkPlan> plans)
    {
        ArgumentNullException.ThrowIfNull(plans);
        var requested = plans.ToArray();
        var updates = new List<(MaterialItem Item, SourceRelinkPlan Plan)>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plan in requested)
        {
            if (plan == null || plan.Source == null) throw new ArgumentException("来源修复计划无效，请重新选择。", nameof(plans));
            if (!ids.Add(plan.Source.Id)) throw new ArgumentException("同一条素材不能在一次操作中重复修复。", nameof(plans));
            var item = _items.FirstOrDefault(x => x.Id == plan.Source.Id)
                ?? throw new InvalidOperationException("素材已被移除，请重新选择。");
            if (item.Type != plan.Source.Type || item.Size != plan.Source.Size ||
                !string.Equals(item.FilePath, plan.Source.FilePath, StringComparison.Ordinal))
                throw new InvalidOperationException("素材来源已变化，请重新查找并确认。");
            var verified = SourceRelinkService.ValidatePlan(plan);
            if (!paths.Add(verified.CandidatePath))
                throw new InvalidOperationException("同一文件匹配多条素材，请逐条选择不同来源。");
            if (_items.Any(x => x.Id != item.Id && !string.IsNullOrWhiteSpace(x.FilePath) &&
                SourceRelinkService.PathsEqual(x.FilePath, verified.CandidatePath)))
                throw new InvalidOperationException("该文件已在素材库中，请选择其他来源或保留已有素材。");
            updates.Add((item, verified));
        }

        foreach (var (item, plan) in updates)
        {
            item.FilePath = plan.CandidatePath;
            item.Size = plan.CandidateSize;
            item.ThumbnailPath = string.Empty;
            item.ThumbnailSource = null;
            item.Duration = 0;
            item.RefreshSourceStatus();
        }
        if (updates.Count > 0)
        {
            ScheduleSave();
            foreach (var (item, _) in updates)
            {
                NextSequence();
                ItemAdded?.Invoke(item);
            }
            Logger.Run("DataStore: relinked {0} source files", updates.Count);
        }
        return updates.Count;
    }

    /// <summary>获取过滤列表：groupName null 不限、空字符串未分组；tag null/空不限。</summary>
    public IEnumerable<MaterialItem> GetFiltered(string tabKey, string? groupName = null, string? tag = null)
    {
        IEnumerable<MaterialItem> filtered = tabKey switch
        {
            "Recent" => _items.OrderByDescending(x => x.AddedTime),
            "Favorites" => _items.Where(x => x.IsFavorite).OrderByDescending(x => x.AddedTime),
            "Video" => _items.Where(x => x.Type == MaterialType.Video).OrderByDescending(x => x.AddedTime),
            "Text" => _items.Where(x => x.Type == MaterialType.Text).OrderByDescending(x => x.AddedTime),
            "Image" => _items.Where(x => x.Type == MaterialType.Image).OrderByDescending(x => x.AddedTime),
            "Audio" => _items.Where(x => x.Type == MaterialType.Audio).OrderByDescending(x => x.AddedTime),
            "Other" => _items.Where(x => x.Type == MaterialType.Other).OrderByDescending(x => x.AddedTime),
            _ => _items.OrderByDescending(x => x.AddedTime)
        };
        if (groupName != null)
            filtered = filtered.Where(x => string.Equals(x.GroupName, groupName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(tag))
            filtered = filtered.Where(x => x.Tags.Contains(tag.Trim(), StringComparer.OrdinalIgnoreCase));
        return filtered;
    }

    /// <summary>搜索素材（文件名、文件路径、文字内容及电脑端分组、标签、备注）。</summary>
    public IEnumerable<MaterialItem> Search(string keyword, string tabKey, string? groupName = null, string? tag = null)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return GetFiltered(tabKey, groupName, tag);

        var filtered = GetFiltered(tabKey, groupName, tag);
        return filtered.Where(x =>
            x.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
            (x.IsFile && x.FilePath.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
            (!x.IsFile && x.TextContent.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
            x.GroupName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
            x.Tags.Any(tagValue => tagValue.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
            x.Note.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(DataFile))
            {
                Logger.Run("DataStore: no existing data file, starting fresh");
                return;
            }

            var json = File.ReadAllText(DataFile);
            var items = JsonSerializer.Deserialize<List<MaterialItem>>(json, JsonOpts);
            if (items != null)
            {
                // 缺失来源也保留索引，避免收藏、标签、备注因文件移动被静默丢弃。
                foreach (var item in items)
                {
                    item.RefreshSourceStatus();
                    _items.Add(item);
                }

                // 按时间排序
                var sorted = _items.OrderByDescending(x => x.AddedTime).ToList();
                _items.Clear();
                foreach (var item in sorted)
                    _items.Add(item);

                Logger.Run("DataStore: loaded {0} items", _items.Count);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("DataStore.Load failed: {0}", ex.Message);
        }
    }

    /// <summary>延迟保存，避免频繁写入（防抖 500ms）</summary>
    private void ScheduleSave()
    {
        _saveTimer?.Dispose();
        _saveTimer = new Timer(_ => Save(), null, 500, Timeout.Infinite);
    }

    private void Save()
    {
        lock (_saveLock)
        {
            // 系统临时文件锁（杀毒扫描等）可能瞬时占用 → 重试 3 次，防数据丢失
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    var json = JsonSerializer.Serialize(_items.ToList(), JsonOpts);
                    // 先写临时文件再替换，防止写入中断损坏数据
                    var tempFile = DataFile + ".tmp";
                    File.WriteAllText(tempFile, json);
                    if (File.Exists(DataFile))
                        File.Replace(tempFile, DataFile, null);
                    else
                        File.Move(tempFile, DataFile);

                    Logger.Run("DataStore: saved {0} items to {1}", _items.Count, DataFile);
                    return;
                }
                catch (Exception ex)
                {
                    if (attempt >= 3)
                    {
                        Logger.Error("DataStore.Save failed after {0} attempts: {1}", attempt, ex.Message);
                    }
                    else
                    {
                        System.Threading.Thread.Sleep(800);
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _saveTimer?.Dispose();
            Save(); // 确保退出时保存
            _disposed = true;
        }
    }
}
