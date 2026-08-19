using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Liuzhuan.Utils;

namespace Liuzhuan;

public partial class App : Application
{
    public static string AppDir { get; private set; } = string.Empty;

    /// <summary>数据目录：exe 同级 ../data 文件夹（无 C 盘依赖）</summary>
    public static string DataDir { get; private set; } = string.Empty;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 确定 app 目录（基于 exe 所在位置）
        AppDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        // 数据目录：app/../data（项目文件夹下，不占用 C 盘）
        DataDir = Path.GetFullPath(Path.Combine(AppDir, "..", "data"));

        // 日志目录：exe 同级 logs 文件夹
        Logger.Init(AppDir);
        Logger.Run("=== 流转 启动 ===");
        Logger.Run("AppDir: {0}", AppDir);
        Logger.Run("DataDir: {0}", DataDir);
        Logger.Run("OS: {0}", Environment.OSVersion.VersionString);
        Logger.Run(".NET Runtime: {0}", Environment.Version);

        // 全局异常处理 — 尽可能捕获所有未处理异常，写入日志
        DispatcherUnhandledException += (s, args) =>
        {
            Logger.Error("DispatcherUnhandledException: {0}\n{1}", args.Exception.Message, args.Exception.StackTrace ?? "");
            args.Handled = true; // 阻止崩溃
        };

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            Logger.Error("AppDomain.UnhandledException: {0}\n{1}",
                ex?.Message ?? "unknown", ex?.StackTrace ?? "");
        };

        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            Logger.Error("UnobservedTaskException: {0}\n{1}", args.Exception.Message, args.Exception.StackTrace ?? "");
            args.SetObserved();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Logger.Run("=== 流转 退出 ===");
        base.OnExit(e);
        // 确保进程完整终止，防止残留 zombie
        Environment.Exit(e.ApplicationExitCode);
    }
}
