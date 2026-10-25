using Liuzhuan.Models;

namespace Liuzhuan.Services;

/// <summary>Capture on the owner thread. Background queries never read mutable material fields.</summary>
public sealed record DesktopMaterialRow(MaterialItem Item,DateTime Added,string Type,bool Favorite,string Name,
    string Path,string Text,string Group,string[] Tags,string Note);
public static class DesktopMaterialQuery
{
    public static DesktopMaterialRow[] Capture(IEnumerable<MaterialItem> items)=>items.Select(x=>new DesktopMaterialRow(x,x.AddedTime,x.Type.ToString(),x.IsFavorite,x.DisplayName,x.FilePath,x.TextContent,x.GroupName,x.Tags.ToArray(),x.Note)).ToArray();
    public static MaterialItem[] Find(DesktopMaterialRow[] rows,string tab,string keyword,string? group,string? tag,CancellationToken token=default)
    {
        var matched=new List<DesktopMaterialRow>();
        foreach(var row in rows)
        {
            token.ThrowIfCancellationRequested();
            if(tab=="Favorites"&&!row.Favorite)continue;
            if(tab!="Recent"&&tab!="Favorites"&&row.Type!=tab)continue;
            if(group!=null&&!string.Equals(row.Group,group.Trim(),StringComparison.OrdinalIgnoreCase))continue;
            if(!string.IsNullOrWhiteSpace(tag)&&!row.Tags.Contains(tag.Trim(),StringComparer.OrdinalIgnoreCase))continue;
            bool Contains(string value)=>value.Contains(keyword,StringComparison.OrdinalIgnoreCase);
            if(keyword.Length>0&&!Contains(row.Name)&&!Contains(row.Path)&&!Contains(row.Text)&&!Contains(row.Group)&&!row.Tags.Any(Contains)&&!Contains(row.Note))continue;
            matched.Add(row);
        }
        token.ThrowIfCancellationRequested();
        return matched.OrderByDescending(x=>x.Added).Select(x=>x.Item).ToArray();
    }
}
