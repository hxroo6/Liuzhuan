package com.liuzhuan.app

import android.os.SystemClock
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow

/** 业务活动通知服务续期，不轮询剪贴板。仅在实际发送/接收时触发。 */
object PowerSignals {
    private val mutable = MutableStateFlow(0L)
    val activeUntil = mutable.asStateFlow()
    @Synchronized fun touch() {
        val now = SystemClock.elapsedRealtime()
        if (mutable.value - now > 15_000L) return
        mutable.value = now + 30_000L
    }
}
