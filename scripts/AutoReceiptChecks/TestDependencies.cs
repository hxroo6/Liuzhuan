namespace Liuzhuan.Utils {
    public static class Logger { public static void Run(string format, params object?[] values) {} public static void Error(string format, params object?[] values) {} }
}
namespace Liuzhuan.Models {
    public enum MaterialType { Text, Image }
    public class MaterialItem { public string Id {get;set;}="";public MaterialType Type {get;set;}=MaterialType.Text;public string DisplayName {get;set;}="";
        public bool IsFile {get;set;} public string FilePath {get;set;}=""; public long Size {get;set;} public string TextContent {get;set;}=""; }
}
namespace Liuzhuan.Services {
    public static class ConfigService {private static readonly string Root=System.IO.Path.Combine("F:/appdata/Temp/liuzhuan-receipt-hosts",Guid.NewGuid().ToString("N"));public static string GetEffectiveDataDir()=>Root;}
    public record TransferEntry(string Id,string Name,string Direction,long Done,long Total,string State,string Error,double BytesPerSecond);
    public static class TransferJournal { public static void Report(TransferEntry entry){} }
}
namespace Liuzhuan.Services.Lan {
    public static class LanConfig { public static string PasswordHash {get;set;}=""; public static int Port {get;set;} }
    public static class LanNetUtil {public static string GetLanIp()=>"127.0.0.1";}
}
