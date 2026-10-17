namespace Liuzhuan.Services.Audio;

/// <summary>Pure demand state machine. A completed idle/active/idle calibration binds one process incarnation.</summary>
public sealed class FollowDemandPolicy
{
    private int _calibration = -1;
    private string _owner = "";
    private long _idleAt = -1;
    private long _connection, _stopRevision;
    public bool Calibrated => _calibration == 3;
    public void Reset() { Disable(); _calibration = -1; _owner = ""; }
    public bool Enabled { get; private set; }
    public string Status { get; private set; } = "请先开启会话观察，再开始校准。";
    public void BeginCalibration()
    {
        Disable("校准中：先保持听写关闭，再点一次原听写，约三秒后结束。校准不会自动转发音频。");
        _calibration = 0; _owner = ""; _idleAt = -1;
    }
    public void Observe(CaptureObservation observation, long nowMs)
    {
        if (!observation.Healthy || observation.UnknownActive > 0)
        {
            _idleAt = -1;
            if (_calibration is >= 0 and < 3) { _calibration = 0; _owner = ""; }
            if (Enabled) Disable("录音归属或端点不可验证，已停止自动跟随。");
            return;
        }
        var owners = observation.ActiveOwners;
        if (_calibration == 0 && owners.Length == 0)
        {
            if (_idleAt < 0) _idleAt = nowMs;
            if (nowMs - _idleAt >= 500) { _calibration = 1; _idleAt = -1; Status = "已确认空闲，请点 Codex 原听写，约三秒后结束。"; }
        }
        else if (_calibration == 1 && owners.Length == 1)
        { _owner = owners[0]; _calibration = 2; Status = "已识别独立 Codex 录音会话，请结束这次听写。"; }
        else if (_calibration == 2)
        {
            if (owners.Any(x => x != _owner)) { _calibration = 0; _idleAt = -1; _owner = ""; return; }
            if (owners.Length > 0) _idleAt = -1;
            else
            {
                if (_idleAt < 0) _idleAt = nowMs;
                if (nowMs - _idleAt >= 400) { _calibration = 3; _idleAt = -1; Status = "校准通过。手机开启会话后，可勾选自动跟随。"; }
            }
        }
    }
    public void Enable(MicSnapshot phone)
    {
        if (!Calibrated || !phone.Ready || phone.ConnectionId == 0 || phone.Requested)
            throw new InvalidOperationException("请完成校准，并让手机采音待命；开启自动跟随前先停止手动转发。");
        _connection = phone.ConnectionId; _stopRevision = phone.StopRevision; _idleAt = -1;
        Enabled = true; Status = "自动跟随已开启：等待 Codex 录音；手机仍由你控制采音许可。";
    }
    public void Disable(string status = "自动跟随已关闭。") { Enabled = false; _idleAt = -1; Status = status; }
    public bool? Evaluate(CaptureObservation observation, MicSnapshot phone, long nowMs, bool fresh)
    {
        if (!Enabled) return null;
        if (!fresh || !observation.Healthy || observation.UnknownActive > 0 || !phone.Ready ||
            phone.ConnectionId != _connection || phone.StopRevision != _stopRevision || observation.ActiveOwners.Any(x => x != _owner))
        { Disable("手机暂停、连接变化或录音身份变化，已停止自动跟随；重新确认后再开启。"); return false; }
        if (observation.ActiveOwners.Contains(_owner))
        { _idleAt = -1; return phone.Requested ? null : true; }
        if (_idleAt < 0) _idleAt = nowMs;
        return phone.Requested && nowMs - _idleAt >= 400 ? false : null;
    }
}
