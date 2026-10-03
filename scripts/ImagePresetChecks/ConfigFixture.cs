namespace Liuzhuan.Services;

// 仅替换默认目录解析；所有校验、序列化和文件操作均使用真实生产代码。
internal static class ConfigService
{
    public static string DirectoryPath { get; set; } = "";
    public static string GetEffectiveDataDir() => DirectoryPath;
}
