namespace Liuzhuan.Services.Audio;

/// <summary>Explicit, allowlisted metadata. Never includes identifiers, credentials or audio.</summary>
public static class MicrophoneDiagnostics
{
    public static string Format(MicSnapshot? current, MicSnapshot? previous = null)
    {
        bool connected = current?.ConnectionId > 0;
        var state = connected ? current : previous;
        var connection = current == null ? "服务已关闭" : connected ? "手机已连接" : "等待手机连接";
        if (state == null) return $"流转麦克风链路诊断\n连接：{connection}\n尚无音频会话。\n不包含地址、口令、设备标识或录音。";
        var mode = state.DiagnosticOnly ? "仅诊断收音，不向虚拟设备输出" : "选定的虚拟播放端";
        var activity = !connected ? "已结束" : state.Quiet ? "安静待命，仅心跳" : state.Receiving ? "正在接收音频" : state.Requested ? "等待有效音频" : state.Ready ? "采音待命，尚未转发" : "手机尚未准备或已暂停";
        var output = state.DiagnosticOnly ? "不输出" : state.ReadPcmBytes is long read
            ? $"{read} 字节（{read / (AudioProtocol.Rate * 2.0):F2} 秒PCM）" : "尚无可用统计";
        return $"流转麦克风链路诊断\n连接：{connection}\n统计范围：{(connected ? "当前" : "最近已结束")}手机会话\n状态：{activity}\n模式：{mode}\n持续：{state.SessionSeconds} 秒\n收到有效音频：{state.TotalFrames} 帧（{state.TotalFrames * AudioProtocol.Samples / (double)AudioProtocol.Rate:F2} 秒PCM）\n播放端已读取：{output}\n当前缓冲：{state.BufferedMs} ms\n服务累计丢旧帧：{state.Dropped}\n{(connected ? $"最近手机状态：{state.HeartbeatAgeMs} ms前\n" : "")}播放端读取不代表录音端或Codex已收到；识别文字需单独确认。\n不包含地址、口令、设备标识或录音。";
    }
}
