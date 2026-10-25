package com.liuzhuan.app.audio

/** No exception text, address, invitation or PCM enters recovery diagnostics. */
enum class MicIssue(val title: String, val action: String) {
    NONE("", ""),
    NETWORK("音频连接中断", "确认素材连接正常，再点下方重新连接当前电脑，无需重扫二维码。"),
    DRIVER("电脑音频服务未就绪", "在电脑麦克风设置检查虚拟设备，点“按原设备重试”，然后回这里重新连接。"),
    PERMISSION("录音权限不足", "在系统应用设置允许麦克风，回到流转前台重试。"),
    SYSTEM_BLOCKED("系统阻断了麦克风", "关闭系统麦克风静音，或结束通话及其他录音，再在流转前台重新连接。"),
    CAPTURE("手机采音不可用", "检查内置麦克风和当前录音占用；恢复后重新连接，不影响素材同步。"),
    PROTOCOL("音频协议不兼容", "请同时更新电脑端和安卓端，再重新连接。")
}

/** Persisted preference, never capture authorization. Economy uses only the existing material connection. */
object MicrophonePowerPolicy {
    fun shouldStartOnScan(energySaving: Boolean) = !energySaving
    fun batchFrames(quiet: Boolean, observed: Boolean, forwarding: Boolean) =
        if (quiet || !observed && !forwarding) 5 else 1
}
