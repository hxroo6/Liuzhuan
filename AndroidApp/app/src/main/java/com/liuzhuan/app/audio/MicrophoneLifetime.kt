package com.liuzhuan.app.audio

/** Monotonic session limits: remote consent has no idle/age deadline; local tests remain bounded. */
class MicrophoneLifetime(private val localOnly: Boolean, private val startedMs: Long) {
    companion object {
        const val REMOTE_WAKE_MS = 30 * 60_000L
        const val LOCAL_WAKE_MS = 65_000L
        private const val RENEW_MS = 20 * 60_000L
    }
    private var renewAt = startedMs + RENEW_MS
    fun expired(nowMs: Long) = localOnly && nowMs - startedMs >= 60_000L
    fun secondsLeft(nowMs: Long) = if (localOnly) ((60_000L - (nowMs - startedMs)).coerceAtLeast(0) / 1000).toInt() else 0
    fun shouldRenew(nowMs: Long) = !localOnly && nowMs >= renewAt
    fun renewed(nowMs: Long) { renewAt = nowMs + RENEW_MS }
}
