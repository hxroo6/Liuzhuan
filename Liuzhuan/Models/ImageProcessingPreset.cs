using System.Text.Json.Serialization;
using Liuzhuan.Services;

namespace Liuzhuan.Models;

/// <summary>只包含处理参数；没有来源、保存位置或用户素材路径。</summary>
public sealed record ImageProcessingPreset
{
    [JsonRequired] public string Name { get; init; } = "";
    [JsonRequired] public ImageOutputFormat Format { get; init; } = ImageOutputFormat.Jpeg;
    [JsonRequired] public int? MaxEdge { get; init; } = 1920;
    [JsonRequired] public int JpegQuality { get; init; } = 85;
    [JsonIgnore] public ImageProcessingOptions Options => new(Format, MaxEdge, JpegQuality);
    [JsonIgnore] public string Summary => (Format == ImageOutputFormat.Jpeg ? "JPEG" : "PNG")
        + (MaxEdge.HasValue ? $" · 最长边 {MaxEdge} px" : " · 原始尺寸")
        + (Format == ImageOutputFormat.Jpeg ? $" · 质量 {JpegQuality}" : " · 无损压缩");
}

/// <summary>磁盘与迁移共用 DTO；Version 为预设格式版本，和软件版本独立。</summary>
public sealed record ImageProcessingPresetDocument
{
    [JsonRequired] public int Version { get; init; } = 1;
    [JsonRequired] public List<ImageProcessingPreset> Presets { get; init; } = new();
}
