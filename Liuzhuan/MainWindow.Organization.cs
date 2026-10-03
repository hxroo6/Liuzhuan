using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Liuzhuan.Models;

namespace Liuzhuan;

public partial class MainWindow
{
    private string? _groupFilter;
    private string? _tagFilter;
    private bool _toolDialogOpen;
    private bool HasOrganizationFilter => _groupFilter != null || _tagFilter != null;

    private void UpdateOrganizationFilters()
    {
        if (GroupFilterText == null) return;
        GroupFilterText.Text = _groupFilter == null ? "分组：全部" : _groupFilter.Length == 0 ? "分组：未分组" : "分组：" + _groupFilter;
        TagFilterText.Text = _tagFilter == null ? "标签：全部" : "标签：" + _tagFilter;
        GroupFilterText.Foreground = (System.Windows.Media.Brush)FindResource(_groupFilter == null ? "TextDimBrush" : "AccentBrush");
        TagFilterText.Foreground = (System.Windows.Media.Brush)FindResource(_tagFilter == null ? "TextDimBrush" : "AccentBrush");
        GroupFilterButton.ToolTip = GroupFilterText.Text + " · 在整理窗口输入名称创建分组";
        TagFilterButton.ToolTip = TagFilterText.Text;
        OrganizationClearButton.Visibility = HasOrganizationFilter ? Visibility.Visible : Visibility.Collapsed;
    }

    private void GroupFilter_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = GroupFilterButton, Placement = PlacementMode.Bottom };
        void Add(string title, string? group, int count)
        {
            var option = new MenuItem { Header = title + $" · {count}", IsCheckable = true,
                IsChecked = string.Equals(_groupFilter, group, StringComparison.OrdinalIgnoreCase) };
            option.Click += (_, _) => { _groupFilter = group; RefreshView(); };
            menu.Items.Add(option);
        }
        Add("全部分组", null, _dataStore.Items.Count);
        Add("未分组", "", _dataStore.Items.Count(x => x.GroupName.Length == 0));
        menu.Items.Add(new Separator());
        foreach (var group in _dataStore.Items.Where(x => x.GroupName.Length > 0).GroupBy(x => x.GroupName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Key, StringComparer.CurrentCultureIgnoreCase)) Add(group.Key, group.Key, group.Count());
        OpenMenu(menu);
    }

    private void TagFilter_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = TagFilterButton, Placement = PlacementMode.Bottom };
        var all = new MenuItem { Header = "全部标签", IsCheckable = true, IsChecked = _tagFilter == null };
        all.Click += (_, _) => { _tagFilter = null; RefreshView(); };
        menu.Items.Add(all); menu.Items.Add(new Separator());
        foreach (var tag in _dataStore.Items.SelectMany(x => x.Tags).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase))
        {
            var option = new MenuItem { Header = tag, IsCheckable = true, IsChecked = string.Equals(_tagFilter, tag, StringComparison.OrdinalIgnoreCase) };
            option.Click += (_, _) => { _tagFilter = tag; RefreshView(); };
            menu.Items.Add(option);
        }
        if (menu.Items.Count == 2) menu.Items.Add(new MenuItem { Header = "还没有标签，选择素材后点击「整理」", IsEnabled = false });
        OpenMenu(menu);
    }

    private void ClearOrganizationFilters_Click(object sender, RoutedEventArgs e) => ClearOrganizationFilters();
    private void ClearOrganizationFilters() { _groupFilter = _tagFilter = null; RefreshView(); }
    private void OrganizeSelection_Click(object sender, RoutedEventArgs e) => OrganizeItems(GetSelectedItems());

    private void OrganizeItems(IReadOnlyList<MaterialItem> items)
    {
        if (items.Count == 0) { ShowFeedback("先选择需要整理的素材"); return; }
        var ids = items.Select(x => x.Id).ToArray();
        var groups = _dataStore.Items.Select(x => x.GroupName).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var tags = _dataStore.Items.SelectMany(x => x.Tags).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        CloseHoverPreview();
        var editor = new Views.MaterialOrganizationWindow(items, groups, tags, patch =>
        {
            if (!_dataStore.Items.Any(x => ids.Contains(x.Id))) throw new InvalidOperationException("选中素材已被移除，请关闭窗口后重新选择。");
            return _dataStore.UpdateOrganization(ids, patch);
        }) { Owner = this };
        ShowToolDialog(editor);
        if (editor.Saved)
        {
            RefreshView();
            ShowFeedback(editor.ChangedCount > 0 ? $"已整理 {editor.ChangedCount} 项素材" : "整理信息未变化");
        }
    }

    private void ShowToolDialog(Window dialog)
    {
        _toolDialogOpen = true;
        _collapseTimer?.Stop();
        try { dialog.ShowDialog(); }
        finally { _toolDialogOpen = false; _collapseTimer?.Start(); }
    }

    private List<MaterialItem> CaptureCurrentClearTargets() => _currentView.Where(x => _currentTab == "Favorites" || !x.IsFavorite).ToList();
    private void ApplyCurrentClearTargets(IReadOnlyList<MaterialItem> targets, bool favorites)
    {
        var ids = targets.Select(x => x.Id).ToList();
        if (favorites) _dataStore.SetFavoriteMultiple(ids, false);
        else _dataStore.RemoveMultiple(ids);
        RefreshView(); UpdateUndoButton();
        ShowFeedback(favorites ? $"已取消 {ids.Count} 项收藏" : $"已移除 {ids.Count} 项 · 可撤回，原文件保留");
    }
}
