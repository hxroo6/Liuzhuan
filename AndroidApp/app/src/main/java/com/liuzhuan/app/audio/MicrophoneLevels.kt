package com.liuzhuan.app.audio

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow

data class MicLevelSnapshot(val level: PcmLevel = PcmLevel(), val history: List<Float> = List(36) { 0f }, val capturing: Boolean = false)

/** Separate, visible-only 10Hz meter; service/notification status stays low frequency. */
object MicrophoneLevels {
    private val mutable = MutableStateFlow(MicLevelSnapshot())
    val state = mutable.asStateFlow()
    internal val observed: Boolean get() = mutable.subscriptionCount.value > 0
    private var lastPublished = 0L
    private var maximumPeak = 0f
    @Synchronized internal fun accept(level: PcmLevel, nowMs: Long) {
        if (mutable.subscriptionCount.value == 0) { maximumPeak = 0f; return }
        maximumPeak = maxOf(maximumPeak, level.peak)
        if (nowMs - lastPublished < 100) return
        val peak = maximumPeak
        maximumPeak = 0f; lastPublished = nowMs
        mutable.value = MicLevelSnapshot(level.copy(peak = peak), mutable.value.history.drop(1) + peak, true)
    }
    @Synchronized internal fun reset() { lastPublished = 0; maximumPeak = 0f; mutable.value = MicLevelSnapshot() }
}
