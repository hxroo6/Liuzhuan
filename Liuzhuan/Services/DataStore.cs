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
    private readonly List<List<MaterialItem>> _undoStack = new();
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
            NextSequence();
            PushUndo(new List<MaterialItem> { item });
            _items.Remove(item);
            ScheduleSave();
            Logger.Run("DataStore: removed item Id={0} Name={1}", id, item.DisplayName);
            ItemRemoved?.Invoke(id);
        }
    }

    /// <summary>批量删除多条素材（记录撤回）</summary>
    public void RemoveMultiple(List<string> ids)
    {
        var removed = new List<MaterialItem>();
        foreach (var id in ids)
        {
            var item = _items.FirstOrDefault(x => x.Id == id);
            if (item != null)
            {
                removed.Add(item);
                _items.Remove(item);
            }
        }
        if (removed.Count > 0)
        {
            NextSequence();
            PushUndo(removed);
            ScheduleSave();
            Logger.Run("DataStore: removed {0} items (batch)", removed.Count);
            foreach (var item in removed)
                ItemRemoved?.Invoke(item.Id);
        }
    }

    /// <summary>撤回最后一次删除操作</summary>
    public bool Undo()
    {
        if (_undoStack.Count == 0) return false;

        var restored = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);

        foreach (var item in restored)
        {
            // 避免重复
            if (!_items.Any(x => x.Id == item.Id))
            {
                _items.Insert(0, item);
            }
        }

        ScheduleSave();
        Logger.Run("DataStore: undo restored {0} items", restored.Count);
        return true;
    }

    /// <summary>是否有可撤回的操作</summary>
    public bool CanUndo => _undoStack.Count > 0;

    /// <summary>获取可撤回的步数</summary>
    public int UndoCount => _undoStack.Count;

    private void PushUndo(List<MaterialItem> removedItems)
    {
        _undoStack.Add(removedItems);
        if (_undoStack.Count > MaxUndoSteps)
            _undoStack.RemoveAt(0);
    }

    /// <summary>清空指定分类的素材（记录撤回，收藏项不受影响）</summary>
    public void ClearByType(MaterialType type)
    {
        var toRemove = _items.Where(x => x.Type == type && !x.IsFavorite).ToList();
        if (toRemove.Count > 0)
        {
            NextSequence();
            PushUndo(toRemove);
            foreach (var item in toRemove)
                _items.Remove(item);
            ScheduleSave();
            Logger.Run("DataStore: cleared type={0}, count={1} (favorites kept)", type, toRemove.Count);
            ItemCleared?.Invoke();
        }
    }

    /// <summary>清空所有素材（记录撤回，收藏项不受影响）</summary>
    public void ClearAll()
    {
        var toRemove = _items.Where(x => !x.IsFavorite).ToList();
        if (toRemove.Count > 0)
        {
            NextSequence();
            PushUndo(toRemove);
            foreach (var item in toRemove)
                _items.Remove(item);
            ScheduleSave();
            Logger.Run("DataStore: cleared all, count={0} (favorites kept)", toRemove.Count);
            ItemCleared?.Invoke();
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

    /// <summary>获取过滤后的列表</summary>
    public IEnumerable<MaterialItem> GetFiltered(string tabKey)
    {
        return tabKey switch
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
    }

    /// <summary>搜索素材（按文件名、文件路径或文字内容匹配）</summary>
    public IEnumerable<MaterialItem> Search(string keyword, string tabKey)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return GetFiltered(tabKey);

        var filtered = GetFiltered(tabKey);
        return filtered.Where(x =>
            x.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
            (x.IsFile && x.FilePath.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
            (!x.IsFile && x.TextContent.Contains(keyword, StringComparison.OrdinalIgnoreCase)));
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
                // 验证文件类素材的源文件是否还存在
                foreach (var item in items)
                {
                    if (item.IsFile && !File.Exists(item.FilePath))
                    {
                        Logger.Run("DataStore: skipping missing file on load: {0}", item.FilePath);
                        continue;
                    }
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
