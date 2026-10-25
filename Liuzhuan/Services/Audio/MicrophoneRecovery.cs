namespace Liuzhuan.Services.Audio;

public enum MicrophoneIssue { None, Network, Driver, Sleep, Permission, SystemBlocked, Protocol }

/// <summary>Stable, non-sensitive failure categories. Recovery never changes endpoint IDs or capture consent.</summary>
public static class MicrophoneRecovery
{
    public static string Describe(MicrophoneIssue issue) => issue switch
    {
        MicrophoneIssue.Network => "网络或心跳中断。确认素材已连接，在手机点“重新连接当前电脑”，无需重扫。",
        MicrophoneIssue.Driver => "虚拟音频设备失效。恢复驱动后点“按原设备重试”；只使用已保存的设备 ID，不会改为扬声器。",
        MicrophoneIssue.Sleep => "电脑锁定或睡眠后已安全停音。解锁后点“按原设备重试”，再由手机前台重新连接。",
        MicrophoneIssue.Permission => "录音权限不足。到手机系统应用设置允许麦克风，再回流转重新连接。",
        MicrophoneIssue.SystemBlocked => "系统阻断了收音。关闭麦克风静音开关或结束其他录音，再在手机前台重试。",
        MicrophoneIssue.Protocol => "音频协议不兼容。请同时更新电脑和手机端。",
        _ => "素材连接与音频独立。手机前台可直接连接当前电脑；无需重复扫码。"
    };
}
