using System.IO;
using System.Text.Json;
using Liuzhuan.Models;

namespace Liuzhuan.Services;

/// <summary>Small library-local rule store; write succeeds before the in-memory list changes.</summary>
public sealed class SavedViewStore
{
    public const int MaxViews = 30;
    public const int MaxNameLength = 40;
    public const int MaxKeywordLength = 256;
    private readonly string _path;
    private List<SavedMaterialView> _views = new();
    public IReadOnlyList<SavedMaterialView> Views => _views.AsReadOnly();
    public string? LoadError { get; private set; }
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public SavedViewStore(string directory)
    {
        _path = Path.Combine(directory, "savedviews.json");
        try
        {
            if (!File.Exists(_path)) return;
            if (new FileInfo(_path).Length > 128 * 1024) throw new InvalidDataException("常用筛选文件过大。");
            _views = NormalizeAll(JsonSerializer.Deserialize<List<SavedMaterialView>>(File.ReadAllText(_path), Json));
        }
        catch (Exception ex) { LoadError = ex.Message; }
    }

    public static SavedMaterialView Normalize(SavedMaterialView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var name = view.Name?.Trim() ?? "";
        if (name.Length is 0 or > MaxNameLength || name.Any(char.IsControl))
            throw new ArgumentException($"筛选名称需为 1–{MaxNameLength} 个字符，且不能换行。");
        if (view.Tab is not ("Recent" or "Favorites" or "Video" or "Image" or "Text" or "Audio" or "Other"))
            throw new ArgumentException("筛选分类无效。");
        var keyword = view.Keyword?.Trim() ?? "";
        if (keyword.Length > MaxKeywordLength || keyword.Any(char.IsControl))
            throw new ArgumentException($"搜索词最多 {MaxKeywordLength} 个字符，且不能换行。");
        var metadata = MaterialOrganization.Normalize(new MaterialOrganizationPatch
        {
            GroupName = view.GroupName,
            Tags = string.IsNullOrWhiteSpace(view.Tag) ? null : new[] { view.Tag }
        });
        return view with { Name = name, Keyword = keyword, GroupName = metadata.GroupName, Tag = metadata.Tags?.Single() };
    }

    public static List<SavedMaterialView> NormalizeAll(IEnumerable<SavedMaterialView>? views)
    {
        var result = (views ?? Array.Empty<SavedMaterialView>()).Select(Normalize).ToList();
        if (result.Count > MaxViews) throw new ArgumentException($"最多保存 {MaxViews} 个常用筛选。");
        if (result.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Count)
            throw new ArgumentException("常用筛选名称不能重复。");
        return result;
    }

    public void Add(SavedMaterialView view) => Commit(_views.Append(view));
    public void Replace(string name, SavedMaterialView view)
    {
        var index = _views.FindIndex(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) throw new InvalidOperationException("该常用筛选已被移除。");
        var next = _views.ToList(); next[index] = view; Commit(next);
    }
    public void Remove(string name) => Commit(_views.Where(x => !x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));

    private void Commit(IEnumerable<SavedMaterialView> views)
    {
        if (LoadError != null) throw new IOException("原常用筛选未能读取，请先保留并检查 savedviews.json：" + LoadError);
        var next = NormalizeAll(views);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(next, Json));
            File.Move(temp, _path, true);
            _views = next;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
