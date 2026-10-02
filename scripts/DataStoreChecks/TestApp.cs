namespace Liuzhuan;

// Only the directory contract is required by ConfigService. No real App startup, network or UI.
internal static class App
{
    public static string DataDir { get; private set; } = string.Empty;
}
