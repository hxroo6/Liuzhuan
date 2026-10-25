using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Liuzhuan;

/// <summary>Drives the real debounce/background-query/dispatcher path without forcing RefreshView.</summary>
internal static class DesktopQueryWait
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void UntilApplied(MainWindow window, Func<bool> expected, string reason, int timeoutMs = 5000)
    {
        var clock = Stopwatch.StartNew();
        while (!CurrentFilterIsRendered(window) || !expected())
        {
            if (clock.ElapsedMilliseconds >= timeoutMs)
                throw new TimeoutException("Production query did not finish: " + reason);
            Pump();
        }
    }

    private static bool CurrentFilterIsRendered(MainWindow window)
    {
        object? Field(string name) => typeof(MainWindow).GetField(name, Private)!.GetValue(window);
        var expected = JsonSerializer.Serialize(new[] { (string?)Field("_currentTab"), (string?)Field("_searchKeyword"),
            (string?)Field("_groupFilter"), (string?)Field("_tagFilter") });
        return expected == (string?)Field("_renderedFilter");
    }

    public static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
