using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Liuzhuan.Models;

namespace Liuzhuan;

public partial class MainWindow
{
    private SavedMaterialView CurrentSavedView(string name) => new()
    { Name = name, Tab = _currentTab, Keyword = _searchKeyword, GroupName = _groupFilter, Tag = _tagFilter };

    private void UpdateSavedViewIndicator()
    {
        if (_savedViews == null || SavedViewText == null) return;
        var current = _savedViews.Views.FirstOrDefault(x => x.Matches(_currentTab, _searchKeyword, _groupFilter, _tagFilter));
        SavedViewText.Text = current?.Name ?? "常用";
        SavedViewText.Foreground = (System.Windows.Media.Brush)FindResource(current == null ? "TextDimBrush" : "AccentBrush");
        SavedViewButton.ToolTip = current == null ? "保存及打开常用筛选" : current.Name + "\n" + current.Summary;
    }

    private void ApplySavedView(SavedMaterialView view)
    {
        _groupFilter = view.GroupName; _tagFilter = view.Tag; _currentTab = view.Tab;
        SearchBox.Text = view.Keyword;
        var button = FindName("Tab" + view.Tab) as RadioButton;
        if (button != null) button.IsChecked = true;
        RefreshView();
        ShowFeedback("已打开常用筛选：" + view.Name);
    }

    private void SavedViews_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = SavedViewButton, Placement = PlacementMode.Bottom };
        foreach (var view in _savedViews.Views)
        {
            var count = _dataStore.Search(view.Keyword, view.Tab, view.GroupName, view.Tag).Count();
            var option = new MenuItem { Header = view.Name + $" · {count}", ToolTip = view.Summary, IsCheckable = true,
                IsChecked = view.Matches(_currentTab, _searchKeyword, _groupFilter, _tagFilter) };
            option.Click += (_, _) => ApplySavedView(view);
            menu.Items.Add(option);
        }
        if (_savedViews.Views.Count == 0) menu.Items.Add(new MenuItem { Header = "把常用的分类、分组、标签和搜索存起来", IsEnabled = false });
        menu.Items.Add(new Separator());
        var save = new MenuItem { Header = "保存当前筛选…", IsEnabled = _savedViews.LoadError == null };
        save.Click += (_, _) => NameSavedView(null);
        menu.Items.Add(save);
        if (_savedViews.LoadError != null) menu.Items.Add(new MenuItem { Header = "常用筛选读取失败，原文件已保留", ToolTip = _savedViews.LoadError, IsEnabled = false });
        if (_savedViews.Views.Count > 0)
        {
            var manage = new MenuItem { Header = "管理常用筛选" };
            foreach (var view in _savedViews.Views)
            {
                var entry = new MenuItem { Header = view.Name };
                var update = new MenuItem { Header = "更新为当前筛选条件…" };
                update.Click += (_, _) =>
                {
                    if (MessageBox.Show(this, $"把「{view.Name}」更新为当前的分类、分组、标签和搜索吗？\n素材保持不变。", "更新常用筛选", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                        ChangeSavedView(() => _savedViews.Replace(view.Name, CurrentSavedView(view.Name)), "已更新常用筛选");
                };
                var rename = new MenuItem { Header = "重命名…" }; rename.Click += (_, _) => NameSavedView(view);
                var remove = new MenuItem { Header = "删除这个筛选…" };
                remove.Click += (_, _) =>
                {
                    if (MessageBox.Show(this, $"删除常用筛选「{view.Name}」吗？\n只删除筛选条件，素材保持不变。", "删除常用筛选", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                        ChangeSavedView(() => _savedViews.Remove(view.Name), "已删除常用筛选，素材保留");
                };
                entry.Items.Add(update); entry.Items.Add(rename); entry.Items.Add(remove); manage.Items.Add(entry);
            }
            menu.Items.Add(manage);
        }
        OpenMenu(menu);
    }

    private void NameSavedView(SavedMaterialView? existing)
    {
        var draft = existing ?? CurrentSavedView("当前筛选");
        var dialog = new Views.SavedViewNameWindow(draft.Summary, existing?.Name ?? "", name =>
        {
            if (existing == null) _savedViews.Add(CurrentSavedView(name));
            else _savedViews.Replace(existing.Name, existing with { Name = name });
        }, existing != null) { Owner = this };
        ShowToolDialog(dialog);
        if (dialog.Saved) { UpdateSavedViewIndicator(); ShowFeedback("已保存常用筛选"); }
    }
    private void ChangeSavedView(Action change, string feedback)
    {
        try { change(); UpdateSavedViewIndicator(); ShowFeedback(feedback); }
        catch (Exception ex) { ShowFeedback("未修改常用筛选：" + ex.Message); }
    }
}
