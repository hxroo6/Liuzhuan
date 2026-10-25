using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Liuzhuan.Models;

namespace Liuzhuan.Services;

public sealed record CollectionRule
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "新规则";
    public bool Enabled { get; init; } = true;
    public string Type { get; init; } = "All";
    public string Source { get; init; } = "All";
    public string Contains { get; init; } = "";
    public string Device { get; init; } = "";
    public string Group { get; init; } = "";
    public string[] Tags { get; init; } = Array.Empty<string>();
}
public sealed record AutomationDocument
{
    [JsonRequired] public int Version { get; init; } = 1;
    [JsonRequired] public long Revision { get; init; }
    [JsonRequired] public List<CollectionRule> Rules { get; init; } = new();
}
public sealed record OrganizationResult(string Group, string[] Tags, string[] Matches);

/// <summary>One rule book per library, shared by local UI and authenticated Android sessions.</summary>
public sealed class AutomationStore
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private readonly string _path;
    private AutomationDocument _document = new();
    public string? LoadError { get; private set; }
    public AutomationStore(string directory)
    {
        _path = Path.Combine(directory, "automation.json");
        try { if (File.Exists(_path)) { if(new FileInfo(_path).Length>512*1024)throw new InvalidDataException(); _document = Normalize(JsonSerializer.Deserialize<AutomationDocument>(File.ReadAllText(_path), Json) ?? throw new InvalidDataException()); } }
        catch { LoadError = "整理规则无法读取，原文件已保留。请先检查 automation.json。"; }
    }
    public AutomationDocument Export() => _document with { Rules = _document.Rules.Select(x => x with { Tags = x.Tags.ToArray() }).ToList() };
    public static AutomationDocument Normalize(AutomationDocument doc)
    {
        if (doc.Version != 1 || doc.Revision < 0 || doc.Revision == long.MaxValue || doc.Rules == null || doc.Rules.Count > 30) throw new ArgumentException("最多保存 30 条规则，或规则版本不受支持。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var rules = new List<CollectionRule>();
        foreach (var r in doc.Rules)
        {
            if (r == null || string.IsNullOrWhiteSpace(r.Id) || r.Id.Length > 64 || !ids.Add(r.Id)) throw new ArgumentException("规则编号无效或重复。");
            if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 48 || r.Name.Any(char.IsControl) || r.Contains == null || r.Contains.Length > 120 || r.Contains.Any(char.IsControl)) throw new ArgumentException("名称最多 48 字，名称关键词最多 120 字。");
            if (r.Type != "All" && (!Enum.TryParse<MaterialType>(r.Type, out var type) || !Enum.IsDefined(typeof(MaterialType),type))) throw new ArgumentException("未知的素材类型。");
            if (r.Source is not ("All" or "Desktop" or "Android" or "Directory")) throw new ArgumentException("未知的收集来源。");
            if(r.Device == null || r.Device.Length>64 || r.Device.Any(char.IsControl)) throw new ArgumentException("设备名称关键词最多 64 字。");
            var patch = MaterialOrganization.Normalize(r.Group, r.Tags, "");
            if (string.IsNullOrEmpty(patch.GroupName) && patch.Tags!.Length == 0) throw new ArgumentException("请至少设置一个分组或标签。");
            rules.Add(r with { Name = r.Name.Trim(), Contains = r.Contains.Trim(), Device=r.Device.Trim(), Group = patch.GroupName!, Tags = patch.Tags! });
        }
        return doc with { Rules = rules };
    }
    public AutomationDocument Save(AutomationDocument proposed)
    {
        if (LoadError != null) throw new InvalidOperationException(LoadError);
        var valid = Normalize(proposed);
        if (valid.Revision != _document.Revision) throw new InvalidOperationException("规则已在另一端更改，请重新加载后再保存。");
        var next = valid with { Revision = checked(valid.Revision + 1) };
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(next, Json)); File.Move(temporary, _path, true); _document = next; }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return Export();
    }
    public static OrganizationResult Evaluate(AutomationDocument doc, MaterialItem item)
    {
        var group = item.GroupName;
        var tags = item.Tags.ToList();
        var matches = new List<string>();
        foreach (var rule in doc.Rules)
        {
            if (!rule.Enabled || rule.Type != "All" && rule.Type != item.Type.ToString() || rule.Source != "All" && rule.Source != item.CollectionSource || !item.DisplayName.Contains(rule.Contains, StringComparison.OrdinalIgnoreCase)) continue;
            if(!item.CollectionDevice.Contains(rule.Device,StringComparison.OrdinalIgnoreCase))continue;
            var merged = tags.Concat(rule.Tags).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            // Reject the whole conflicting action; never discard a user's existing tags.
            if (merged.Length > MaterialOrganization.MaxTags) continue;
            if (string.IsNullOrEmpty(group)) group = rule.Group;
            tags = merged.ToList(); matches.Add(rule.Name);
        }
        return new(group, tags.ToArray(), matches.ToArray());
    }
    public void Apply(MaterialItem item)
    {
        var result = Evaluate(_document, item);
        item.GroupName = result.Group; item.Tags = result.Tags;
    }
}
