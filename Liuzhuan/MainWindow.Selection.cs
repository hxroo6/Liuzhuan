using System.Windows;
using System.Windows.Controls;
using Liuzhuan.Models;
using Liuzhuan.Services;
using Liuzhuan.Utils;

namespace Liuzhuan;

public partial class MainWindow
{
    private bool _changingSelection;
    private void UpdateSelectionBar()
    {
        if (SelectionBar == null) return;
        var selected = GetSelectedItems();
        SelectionBar.Visibility = selected.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectionCountText.Text = $"已选 {selected.Count} 项";
        SelectionFavoriteButton.Content = selected.Count > 0 && selected.All(x => x.IsFavorite) ? "取消收藏" : "收藏";
        SelectionCopyButton.IsEnabled = selected.Count > 0 && (selected.All(x => x.IsFile) || selected.All(x => !x.IsFile));
        SelectionCopyButton.ToolTip = SelectionCopyButton.IsEnabled ? "批量复制 · Ctrl+C" : "文件与文字请分别复制，或一起另存到文件夹";
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => GetActiveListView()?.SelectAll();
    private void ClearSelection_Click(object sender, RoutedEventArgs e) => GetActiveListView()?.UnselectAll();

    private void InvertSelection_Click(object sender, RoutedEventArgs e)
    {
        var list = GetActiveListView();
        if (list == null) return;
        var previous = GetSelectedItems().Select(x => x.Id).ToHashSet();
        _changingSelection = true;
        try
        {
            list.UnselectAll();
            foreach (var item in _currentView.Where(x => !previous.Contains(x.Id))) list.SelectedItems.Add(item);
        }
        finally { _changingSelection = false; UpdateStatusText(); UpdateSelectionBar(); }
    }

    private void CopySelection_Click(object sender, RoutedEventArgs e) => CopySelection(GetSelectedItems());
    private void CopySelection(IReadOnlyList<MaterialItem> items)
    {
        if (items.Count == 0) return;
        if (items.Count == 1) { CopyItemToClipboard(items[0]); return; }
        if (!DragDropService.TryCreateSelectionData(items, out var data, out var error)) { ShowFeedback(error); return; }
        try
        {
            Clipboard.SetDataObject(data, true);
            ShowFeedback(items.All(x => x.IsFile) ? $"已复制 {items.Count} 个文件" : $"已复制 {items.Count} 段文字");
        }
        catch (Exception ex) { Logger.Error("Selection copy failed: {0}", ex.Message); ShowFeedback("复制失败，剪贴板可能正在被其他应用使用"); }
    }

    private void FavoriteSelection_Click(object sender, RoutedEventArgs e)
    {
        var items = GetSelectedItems();
        if (items.Count == 0) return;
        var favorite = !items.All(x => x.IsFavorite);
        _dataStore.SetFavoriteMultiple(items.Select(x => x.Id).ToList(), favorite);
        RefreshView();
        ShowFeedback(favorite ? $"已收藏 {items.Count} 项" : $"已取消 {items.Count} 项收藏");
    }

    private void RemoveSelection_Click(object sender, RoutedEventArgs e) => DeleteItems(GetSelectedItems());
    private void ExportSelection_Click(object sender, RoutedEventArgs e) => ExportSelection(GetSelectedItems());

    private void ExportSelection(IReadOnlyList<MaterialItem> items)
    {
        if (items.Count == 0) { ShowFeedback("先选择要另存的素材"); return; }
        CloseHoverPreview();
        _collapseTimer?.Stop();
        _menuOpen = true;
        try
        {
            var window = new Views.MaterialExportWindow(MaterialExportService.Snapshot(items)) { Owner = this };
            window.ShowDialog();
        }
        finally { _menuOpen = false; _collapseTimer?.Start(); }
    }
}
