namespace Liuzhuan.Services;

// 保持现有公开命名空间；独立数据定义可供预设校验使用，无需载入 WPF 编解码组件。
public enum ImageOutputFormat { Jpeg, Png }
public sealed record ImageProcessingOptions(ImageOutputFormat Format = ImageOutputFormat.Jpeg, int? MaxEdge = 1920, int JpegQuality = 85);
