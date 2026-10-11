package com.liuzhuan.app.core

/** 服务是否存在由连接状态决定；这个策略只管理保活锁。 */
object KeepAlivePolicy {
    data class Decision(val hold: Boolean, val releaseAfterMs: Long?)
    fun decide(balanced: Boolean, activeConnection: Boolean, transfers: Int, until: Long, now: Long): Decision {
        if (!activeConnection) return Decision(false, null)
        if (!balanced || transfers > 0) return Decision(true, null)
        val remaining = (until - now).coerceAtLeast(0)
        return Decision(remaining > 0, remaining.takeIf { it > 0 })
    }
}
