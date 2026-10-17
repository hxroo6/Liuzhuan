namespace Liuzhuan.Services.Lan;

/// <summary>Reconnect storms must not repeatedly interrupt the desktop. No device identifiers retained.</summary>
public sealed class ConnectionNoticePolicy
{
    private long? _lastNotice;
    public bool ShouldNotify(long nowMs)
    {
        if (_lastNotice is long previous && nowMs - previous < 15_000) return false;
        _lastNotice = nowMs;
        return true;
    }
}
