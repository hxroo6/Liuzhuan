package com.liuzhuan.app.net

/** 失败计数独立于重连 Job，饱和计数不会移位溢出。 */
class RetryBackoff {
    private var failures = 0
    @Synchronized fun reset() { failures = 0 }
    @Synchronized fun nextDelayMs(): Long {
        val delay = (1_000L shl failures.coerceAtMost(5)).coerceAtMost(30_000L)
        failures = (failures + 1).coerceAtMost(6)
        return delay
    }
}
