using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Liuzhuan.Models;

namespace Liuzhuan.Utils;

/// <summary>
/// 日志系统 — 同时输出到文件和控制台
/// 运行日志：logs/run.log（运行时事件、错误、状态）
/// 开发日志：logs/dev.log（开发修改记录）
/// </summary>
public static class Logger
{
    private static readonly object _lock = new();
    private static string _runLogDir = string.Empty;
    private static string _devLogDir = string.Empty;

    public static void Init(string baseDir)
    {
        _runLogDir = Path.Combine(baseDir, "logs");
        _devLogDir = Path.Combine(baseDir, "logs");
        Directory.CreateDirectory(_runLogDir);
    }

    /// <summary>写入运行日志</summary>
    public static void Run(string message, params object[] args)
    {
        WriteLog("run.log", "[RUN]", message, args);
    }

    /// <summary>写入开发日志</summary>
    public static void Dev(string message, params object[] args)
    {
        WriteLog("dev.log", "[DEV]", message, args);
    }

    /// <summary>写入错误日志（同时进 run.log）</summary>
    public static void Error(string message, params object[] args)
    {
        WriteLog("run.log", "[ERROR]", message, args);
    }

    private static void WriteLog(string fileName, string tag, string message, params object[] args)
    {
        try
        {
            var formatted = args.Length > 0 ? string.Format(message, args) : message;
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var line = $"{timestamp} {tag} {formatted}{Environment.NewLine}";

            lock (_lock)
            {
                var path = Path.Combine(_runLogDir, fileName);
                File.AppendAllText(path, line);
            }
        }
        catch
        {
            // 日志自身不能抛异常导致程序崩溃
        }
    }
}
