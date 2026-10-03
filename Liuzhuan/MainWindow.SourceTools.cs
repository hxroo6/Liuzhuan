using System.Windows;
using Liuzhuan.Models;
using Liuzhuan.Services;

namespace Liuzhuan;

public partial class MainWindow
{
    private void ManageSources(string? selectedId = null)
    {
        CloseHoverPreview();
        ShowToolDialog(new Views.SourceRepairWindow(_dataStore, ReloadRelinkedThumbnails, selectedId) { Owner = this });
        RefreshView();
    }
    private void ReloadRelinkedThumbnails(IReadOnlyList<MaterialItem> items)
    {
        foreach (var item in items) DragDropService.LoadDeferredPropertiesAsync(item, RefreshView);
        RefreshView();
    }
    private void ProcessImages(IReadOnlyList<MaterialItem> items)
    {
        if (items.Count == 0 || items.Any(x => x.Type != MaterialType.Image))
        { ShowFeedback("请选择一张或多张图片后再处理"); return; }
        CloseHoverPreview();
        ShowToolDialog(new Views.ImageProcessingWindow(ImageProcessingService.Snapshot(items)) { Owner = this });
    }

    private void CheckMaterialIntegrity(IReadOnlyList<MaterialItem> items)
    {
        var files = items.Where(x => x.HasFileSource).ToList();
        if (files.Count == 0)
        { ShowFeedback("请选择带原文件的素材后检查内容"); return; }
        CloseHoverPreview();
        ShowToolDialog(new Views.MaterialIntegrityWindow(_dataStore, files) { Owner = this });
        RefreshView();
    }
}
