package com.liuzhuan.app.clipboard.action

import android.util.Log
import com.liuzhuan.app.clipboard.ClipboardEvent
import com.liuzhuan.app.clipboard.ClipboardEventQueue
import com.liuzhuan.app.clipboard.ClipboardPrivacy

/**
 * 推送到 PC 动作 —— 把 ClipboardEvent 通过现有 WebSocket 协议（clipboard_push）发送。
 *
 * 关键点：
 * 1. 捕获与网络解耦：断线时事件入队，不阻塞捕获链路；重连成功后由协调器补发。
 * 2. 敏感信息不打日志：只打 source/type/length/fingerprint 前 8 位，绝不打 content。
 * 3. 自动文字经进程 outbox 等待 PC 登记回执；网络接受发送不表示成功。
 */
class PushToPcAction(
    private val queue: ClipboardEventQueue
) : ClipboardAction {

    override suspend fun execute(event: ClipboardEvent) {
        if (ClipboardPrivacy.blockReason(event) != null) return
        if (event.text.isNullOrBlank()) return
        com.liuzhuan.app.clipboard.ClipboardMonitorCoordinator.delivery.submit(event)
        Log.d(TAG, "[OUT_QUEUE][${event.diagnosticId}] pending=${queue.size()} source=${event.source} len=${event.text.length}")
    }

    private companion object {
        const val TAG = "PushToPc"
    }
}
