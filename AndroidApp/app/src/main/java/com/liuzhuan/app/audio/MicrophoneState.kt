package com.liuzhuan.app.audio

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow

data class MicStatus(
    val active: Boolean = false,
    val connected: Boolean = false,
    val forwarding: Boolean = false,
    val message: String = "麦克风已关闭",
    val secondsLeft: Int = 0,
    val frames: Long = 0,
    val localOnly: Boolean = false,
    val capturedFrames: Long = 0,
    val suppression: Boolean = false,
    val quiet: Boolean = false,
    val suppressedFrames: Long = 0,
    val paused: Boolean = false,
    val thresholdDb: Int = -42,
    val issue: MicIssue = MicIssue.NONE,
    val readCalls: Long = 0,
    val captureMs: Long = 0,
    val wakeHeld: Boolean = false,
    val energySaving: Boolean = false
)

object MicrophoneState {
    private val mutable = MutableStateFlow(MicStatus())
    val status = mutable.asStateFlow()
    internal fun update(value: MicStatus) { mutable.value = value }
}
