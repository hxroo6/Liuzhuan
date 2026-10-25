using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Liuzhuan.Models;
using Liuzhuan.Utils;

namespace Liuzhuan.Services;

public sealed record ProjectImportBatchResult(int Added, int AlreadyImported, int DuplicateText);

/// <summary>
/// 数据持久化层 — JSON 文件本地存储
/// 数据目录：项目目录/data/
/// 数据文件：data.json
/// </summary>
public class DataStore : IDisposable
{
    public AutomationStore Automation { get; }
    public bool LoadFailed {get;private set;}
    public string DataRoot { get; }
    private string GetDataDir() => DataRoot;
    private string DataFile => Path.Combine(DataRoot, "data.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ResettableCollection<MaterialItem> _items = new();
    private MaterialItem[]? _orderedItems;
    private HashSet<MaterialItem>? _liveItems;
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
    private long _queryRevision;

    /// <summary>当前变更序号</summary>
    public long CurrentSequence => _sequence;
    /// <summary>Only fields used for ordering/filtering/search invalidate immutable desktop query snapshots.</summary>
    public long QueryRevision => Interlocked.Read(ref _queryRevision);

    private long NextSequence() => System.Threading.Interlocked.Increment(ref _sequence);

    /// <summary>所有素材的 observable 集合</summary>
    public ObservableCollection<MaterialItem> Items => _items;

    /// <summary>最近 N 条素材摘要（LAN 列表同步用，轻量不传内容）</summary>
    public List<Services.Lan.LanItemSummary> GetRecentSummaries(int count) => _items.Take(count).Select(GetSummary).ToList();
    public Services.Lan.LanItemSummary GetSummary(MaterialItem x) => new() {
        Id=x.Id,Type=x.Type.ToString(),Name=x.DisplayName,Size=x.Size,
        AddedTime=new DateTimeOffset(x.AddedTime).ToUnixTimeSeconds(),Sequence=_sequence,
        Group=x.GroupName,Tags=x.Tags.ToArray(),Note=x.Note,HasFile=x.HasFileSource,
        Favorite=x.IsFavorite,Revision=x.MetadataRevision,SourceMissing=x.IsSourceMissing,
        Source=x.CollectionSource,SourceDevice=x.CollectionDevice,SearchText=x.TextContent,SourceRevision=x.SourceRevision
    };
    public event Action<MaterialItem>? ItemUpdated;
    private void MetadataChanged(MaterialItem item, bool queryChanged = true) {
        item.MetadataRevision=checked(item.MetadataRevision+1);
        if(queryChanged)Interlocked.Increment(ref _queryRevision);
        NextSequence();ItemUpdated?.Invoke(item);ScheduleSave();
    }

    public DataStore(string? dataRoot = null)
    {
        DataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot ?? ConfigService.GetEffectiveDataDir()));
        _items.CollectionChanged+=(_,_)=>{_orderedItems=null;_liveItems=null;Interlocked.Increment(ref _queryRevision);};
        Directory.CreateDirectory(GetDataDir());
        Automation = new AutomationStore(GetDataDir());
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
        if (item.HasFileSource)
        {
            var existing = _items.FirstOrDefault(x => x.HasFileSource &&
                string.Equals(x.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.RegistrationKeys=existing.RegistrationKeys.Concat(item.RegistrationKeys).Distinct().ToArray();
                existing.Size = item.Size;
                existing.Type = item.Type;
                existing.TextContent = item.TextContent;
                existing.InvalidateSourceContent();
                existing.RefreshSourceStatus();
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
            var existing = _items.FirstOrDefault(x => !x.HasFileSource && x.TextContent == item.TextContent);
            if (existing != null)
            {
                existing.RegistrationKeys=existing.RegistrationKeys.Concat(item.RegistrationKeys).Distinct().ToArray();
                _items.Remove(existing);
                existing.AddedTime = DateTime.Now;
                _items.Insert(0, existing);
                ScheduleSave();
                Logger.Run("DataStore: duplicate text re-added (moved to top)");
                ItemAdded?.Invoke(existing);
                return existing;
            }
        }

        Automation.Apply(item);
        _items.Insert(0, item);
        ScheduleSave();
        Logger.Run("DataStore: added item Type={0} Name={1}", item.Type, item.Type==MaterialType.Text && !item.HasFileSource?"文字素材":item.DisplayName);
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
        if(step.Removed.Count>32)
        {
            var order=new LinkedList<MaterialItem>(_items);
            var nodes=new Dictionary<string,LinkedListNode<MaterialItem>>(StringComparer.Ordinal);
            for(var node=order.First;node!=null;node=node.Next)nodes[node.Value.Id]=node;
            var restored=new List<MaterialItem>();
            foreach(var removed in step.Removed.AsEnumerable().Reverse())
            {
                if(nodes.ContainsKey(removed.Item.Id))continue;
                LinkedListNode<MaterialItem>? anchor=null;
                for(int i=removed.OriginalIndex+1;i<step.OriginalOrder.Length;i++)if(nodes.TryGetValue(step.OriginalOrder[i],out anchor))break;
                LinkedListNode<MaterialItem> added;
                if(anchor!=null)added=order.AddBefore(anchor,removed.Item);
                else {
                    for(int i=removed.OriginalIndex-1;i>=0;i--)if(nodes.TryGetValue(step.OriginalOrder[i],out anchor))break;
                    added=anchor!=null?order.AddAfter(anchor,removed.Item):order.AddLast(removed.Item);
                }
                nodes[removed.Item.Id]=added;restored.Add(removed.Item);
            }
            _items.Replace(order.ToArray());
            foreach(var item in restored){NextSequence();ItemAdded?.Invoke(item);}
            if(restored.Count>0)ScheduleSave();return restored.Count>0;
        }
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
    public IEnumerable<MaterialItem> RetainedForUndo => _undoStack.SelectMany(x=>x.Removed.Select(r=>r.Item)).DistinctBy(x=>x.Id);

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
        if(removed.Count>32){var ids=removed.Select(x=>x.Id).ToHashSet();_items.Replace(_items.Where(x=>!ids.Contains(x.Id)).ToArray());}
        else foreach (var item in removed) _items.Remove(item);
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
        if (item != null && item.IsFavorite != favorite)
        {
            item.IsFavorite = favorite;
            MetadataChanged(item);
            ScheduleSave();
            Logger.Run("DataStore: set favorite={0} for {1}", favorite, item.DisplayName);
        }
    }

    /// <summary>批量收藏/取消收藏</summary>
    public void SetFavoriteMultiple(List<string> ids, bool favorite)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var selected = ids.ToHashSet(StringComparer.Ordinal);
        var changed = 0;
        foreach (var item in _items)
        {
            if (selected.Contains(item.Id) && item.IsFavorite != favorite)
            {
                item.IsFavorite = favorite;
                MetadataChanged(item);
                changed++;
            }
        }
        if (changed > 0) ScheduleSave();
        Logger.Run("DataStore: set favorite={0} for {1} changed items", favorite, changed);
    }

    /// <summary>Preview and commit use the same whole-batch validation. Caller must subsequently await FlushAsync.</summary>
    public ProjectImportBatchResult PreviewProjectBatch(IReadOnlyList<MaterialItem> incoming,long expectedSequence,string expectedDataRoot) =>
        PlanProjectBatch(incoming,expectedSequence,expectedDataRoot).Result;

    public ProjectImportBatchResult ImportProjectBatch(IReadOnlyList<MaterialItem> incoming,long expectedSequence,string expectedDataRoot)
    {
        var plan=PlanProjectBatch(incoming,expectedSequence,expectedDataRoot);
        // No live object is changed until every row, identity and source path has passed validation.
        foreach(var row in plan.Added) {
            row.Item.GroupName=row.Metadata.GroupName!;row.Item.Tags=row.Metadata.Tags!;row.Item.Note=row.Metadata.Note!;
            row.Item.MetadataRevision=0;row.Item.CollectionSource="ProjectPackage";row.Item.CollectionDevice="";
            row.Item.IntegrityBaseline=null;row.Item.ThumbnailPath="";row.Item.Duration=0;
        }
        foreach(var change in plan.Keys)change.Key.RegistrationKeys=change.Value.ToArray();
        if(plan.Added.Count>0)_items.Replace(plan.Added.Select(x=>x.Item).Concat(_items).ToArray());
        if(plan.Added.Count>0||plan.Keys.Count>0)ScheduleSave();
        foreach(var row in plan.Added){NextSequence();ItemAdded?.Invoke(row.Item);}
        return plan.Result;
    }
    private sealed record ProjectRow(MaterialItem Item,MaterialOrganizationPatch Metadata,string Key);
    private sealed record ProjectPlan(List<ProjectRow> Added,Dictionary<MaterialItem,List<string>> Keys,ProjectImportBatchResult Result);
    private static bool TryProjectKey(string? value,out string key)
    {
        key="";var parts=value?.Split(':');
        if(parts?.Length!=3||parts[0]!="project"||!Guid.TryParse(parts[1],out var package)||!Guid.TryParse(parts[2],out var entry))return false;
        key=$"project:{package:N}:{entry:N}";return true;
    }
    private ProjectPlan PlanProjectBatch(IReadOnlyList<MaterialItem> incoming,long expectedSequence,string expectedDataRoot)
    {
        System.Windows.Application.Current?.Dispatcher.VerifyAccess();
        if(_disposed)throw new ObjectDisposedException(nameof(DataStore));
        if(LoadFailed)throw new IOException("素材索引不可读取，不能合入项目包。原文件已保留。");
        ArgumentNullException.ThrowIfNull(incoming);
        if(incoming.Count is <1 or >1000)throw new ArgumentException("每次合入1–1000条素材。");
        var root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(GetDataDir()));
        if(!string.Equals(root,Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedDataRoot)),StringComparison.OrdinalIgnoreCase)||CurrentSequence!=expectedSequence)
            throw new InvalidOperationException("素材库已变化，请重新预览项目包。");
        var managed=Path.Combine(root,"project-imports")+Path.DirectorySeparatorChar;
        var live=_items.ToHashSet();var ids=new HashSet<string>(StringComparer.Ordinal);var rowKeys=new HashSet<string>(StringComparer.Ordinal);
        var rows=new List<ProjectRow>();
        foreach(var item in incoming) {
            if(item==null||live.Contains(item)||string.IsNullOrWhiteSpace(item.Id)||item.Id.Length>128||!ids.Add(item.Id)||
                !Enum.IsDefined(typeof(MaterialType),item.Type)||item.DisplayName==null||item.DisplayName.Length>256||item.TextContent.Length>262144||item.Size<0||
                item.AddedTime==default||item.RegistrationKeys?.Length!=1||!TryProjectKey(item.RegistrationKeys[0],out var key)||!rowKeys.Add(key))
                throw new ArgumentException("项目包批次或内部登记标记无效。");
            if(item.HasFileSource) {
                var path=Path.GetFullPath(item.FilePath);
                if(!path.StartsWith(managed,StringComparison.OrdinalIgnoreCase)||path.Length<=managed.Length)
                    throw new ArgumentException("项目包原件必须位于当前素材库的受控导入目录。");
            }else if(item.Type!=MaterialType.Text)throw new ArgumentException("项目包缺少原文件。");
            rows.Add(new(item,MaterialOrganization.Normalize(item.GroupName,item.Tags,item.Note),key));
        }
        var registered=new Dictionary<string,MaterialItem>(StringComparer.Ordinal);
        var text=new Dictionary<string,MaterialItem>(StringComparer.Ordinal);
        var liveIds=_items.Select(x=>x.Id).ToHashSet(StringComparer.Ordinal);
        foreach(var item in _items) {
            foreach(var saved in item.RegistrationKeys??Array.Empty<string>())if(TryProjectKey(saved,out var key))registered.TryAdd(key,item);
            if(!item.HasFileSource)text.TryAdd(item.TextContent,item);
        }
        var added=new List<ProjectRow>();var changes=new Dictionary<MaterialItem,List<string>>();
        var prior=0;var duplicates=0;
        void Attach(MaterialItem item,string key) {
            if(!changes.TryGetValue(item,out var keys))changes[item]=keys=(item.RegistrationKeys??Array.Empty<string>())
                .Select(value=>TryProjectKey(value,out var canonical)?canonical:value).Distinct(StringComparer.Ordinal).ToList();
            if(!keys.Contains(key,StringComparer.Ordinal))keys.Add(key);
            registered[key]=item;
        }
        foreach(var row in rows) {
            if(registered.ContainsKey(row.Key)){prior++;continue;}
            if(liveIds.Contains(row.Item.Id))throw new ArgumentException("项目包新条目与本机素材编号冲突，请重新预览。");
            if(!row.Item.HasFileSource&&text.TryGetValue(row.Item.TextContent,out var old)){duplicates++;Attach(old,row.Key);continue;}
            added.Add(row);Attach(row.Item,row.Key);liveIds.Add(row.Item.Id);
            if(!row.Item.HasFileSource)text[row.Item.TextContent]=row.Item;
        }
        return new(added,changes,new(added.Count,prior,duplicates));
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
            MetadataChanged(item);
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
            if(item.RefreshSourceStatus())MetadataChanged(item, queryChanged:false);
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
            if(item.ApplySourceStatus(result.Exists))MetadataChanged(item, queryChanged:false);
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
            Interlocked.Increment(ref _queryRevision);
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
        // Order changes only on collection mutations; category/metadata filters reuse the same stable ordering.
        var ordered=_orderedItems??= _items.OrderByDescending(x=>x.AddedTime).ToArray();
        IEnumerable<MaterialItem> filtered = tabKey switch
        {
            "Favorites" => ordered.Where(x => x.IsFavorite),
            "Video" => ordered.Where(x => x.Type == MaterialType.Video),
            "Text" => ordered.Where(x => x.Type == MaterialType.Text),
            "Image" => ordered.Where(x => x.Type == MaterialType.Image),
            "Audio" => ordered.Where(x => x.Type == MaterialType.Audio),
            "Other" => ordered.Where(x => x.Type == MaterialType.Other),
            _ => ordered
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
            var records = JsonSerializer.Deserialize<List<MaterialRecord>>(json, JsonOpts)
                ?? throw new InvalidDataException("素材索引必须是数组，不能为 null。");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var record in records) {
                if (record == null || !ids.Add(record.Id)) throw new InvalidDataException("素材索引包含重复或空条目。");
                record.Normalize();
            }
            // Complete pure-data validation before publishing any live model. Source probes run after the first frame.
            _items.Replace(records.OrderByDescending(x => x.AddedTime).Select(x => x.CreateMaterial()).ToArray());
            QueueLoadedSourceRefresh();
            Logger.Run("DataStore: loaded {0} items", _items.Count);
        }
        catch (Exception ex)
        {
            LoadFailed=true;
            Logger.Error("DataStore.Load failed: {0}", ex.Message);
        }
    }

    private void QueueLoadedSourceRefresh()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        var sources = _items.Where(x => x.HasFileSource).Select(x => (Item:x, Path:x.FilePath, Revision:x.SourceRevision)).ToArray();
        // Headless callers retain deterministic loaded status; WPF never performs these probes on its first frame.
        if (dispatcher == null) { foreach (var source in sources) source.Item.ApplySourceStatus(File.Exists(source.Path)); return; }
        _ = dispatcher.BeginInvoke(new Action(() => _ = RefreshAsync()), System.Windows.Threading.DispatcherPriority.Background);
        async Task RefreshAsync()
        {
            try {
                for (var offset = 0; offset < sources.Length && !_disposed; offset += 64) {
                    var batch = sources.Skip(offset).Take(64).ToArray();
                    var results = await Task.Run(() => batch.Select(x => (Source:x, Exists:File.Exists(x.Path))).ToArray());
                    if (_disposed || dispatcher.HasShutdownStarted) return;
                    var live = _liveItems ??= _items.ToHashSet();
                    foreach (var result in results) {
                        var source = result.Source;
                        if (live.Contains(source.Item) && source.Item.SourceRevision == source.Revision && source.Item.FilePath == source.Path && source.Item.ApplySourceStatus(result.Exists)) {
                            NextSequence(); ItemUpdated?.Invoke(source.Item);
                        }
                    }
                }
            } catch (Exception ex) { Logger.Error("DataStore source refresh failed: {0}", ex.GetType().Name); }
        }
    }

    /// <summary>延迟保存，避免频繁写入（防抖 500ms）</summary>
    private void ScheduleSave()
    {
        _saveTimer?.Dispose();
        _saveTimer = new Timer(_ => Save(), null, 500, Timeout.Infinite);
    }

    // Capture only immutable fields on the owner thread; serialization and fsync run in one worker.
    // A caller is completed only when a durable generation covers its own snapshot.
    private sealed record SavedItem(long MetadataRevision, string CollectionSource, string CollectionDevice,
        string[] RegistrationKeys, string Id, MaterialType Type, string FilePath,
        MaterialIntegrityBaseline? IntegrityBaseline, string DisplayName, string TextContent,
        DateTime AddedTime, long Size, string ThumbnailPath, double Duration, bool IsFavorite,
        string GroupName, string[] Tags, string Note, bool IsFile)
    {
        public static SavedItem Capture(MaterialItem x) => new(x.MetadataRevision, x.CollectionSource, x.CollectionDevice,
            (x.RegistrationKeys??Array.Empty<string>()).ToArray(), x.Id, x.Type, x.FilePath, x.IntegrityBaseline, x.DisplayName,
            x.TextContent, x.AddedTime, x.Size, x.ThumbnailPath, x.Duration, x.IsFavorite, x.GroupName, x.Tags, x.Note, x.IsFile);
    }
    private sealed record SaveSnapshot(long Version, string Path, SavedItem[] Items);
    private long _snapshotGeneration, _savedGeneration;
    private SaveSnapshot? _pendingSave;
    private bool _saving;
    private readonly List<(long Version, TaskCompletionSource<bool> Completion)> _saveWaiters = new();
    public Task FlushAsync()
    {
        if(LoadFailed)throw new IOException("素材索引无法读取，原文件已保留，不能确认登记。请恢复备份后重启。");
        SaveSnapshot Capture() => new(Interlocked.Increment(ref _snapshotGeneration), DataFile, _items.Select(SavedItem.Capture).ToArray());
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        var snapshot = dispatcher == null || dispatcher.CheckAccess() ? Capture() : dispatcher.Invoke(Capture);
        lock (_saveLock)
        {
            if (snapshot.Version <= _savedGeneration) return Task.CompletedTask;
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _saveWaiters.Add((snapshot.Version, completion));
            if (_pendingSave == null || snapshot.Version > _pendingSave.Version) _pendingSave = snapshot;
            if (!_saving) { _saving = true; _ = Task.Run(SavePending); }
            return completion.Task;
        }
    }
    private void SavePending()
    {
        while (true)
        {
            SaveSnapshot snapshot;
            lock (_saveLock)
            {
                if (_pendingSave == null) { _saving = false; return; }
                snapshot = _pendingSave;
                _pendingSave = null;
            }
            Exception? failure = null;
            try { LibraryRecoveryService.TryPreservePreviousIndex(DataRoot); DurableJson.WriteText(snapshot.Path, JsonSerializer.Serialize(snapshot.Items, JsonOpts)); }
            catch (Exception ex) { failure = ex; }
            lock (_saveLock)
            {
                if (failure == null) _savedGeneration = snapshot.Version;
                if (_pendingSave != null && _pendingSave.Version <= _savedGeneration) _pendingSave = null;
                foreach (var waiter in _saveWaiters.Where(x => x.Version <= snapshot.Version).ToArray())
                {
                    if (failure == null) waiter.Completion.TrySetResult(true);
                    else waiter.Completion.TrySetException(failure);
                    _saveWaiters.Remove(waiter);
                }
            }
        }
    }
    private void Save()
    {
        try { FlushAsync().GetAwaiter().GetResult(); }
        catch(Exception ex) { Logger.Error("DataStore.Save failed: {0}",ex.GetType().Name); }
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
