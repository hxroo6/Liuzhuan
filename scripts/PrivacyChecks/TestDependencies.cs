namespace Liuzhuan { public static class App { public static string DataDir { get; set; } = ""; } }
namespace Liuzhuan.Services { public enum HeicConversionMode { Off, Png, Jpeg } }
namespace Liuzhuan.Utils { public static class Logger { public static void Run(string text, params object[] args) {} public static void Error(string text, params object[] args) {} } }
