package com.liuzhuan.app.clipboard.action

import android.util.Log
import com.liuzhuan.app.LanHub
import com.liuzhuan.app.clipboard.ClipboardEvent
import com.liuzhuan.app.clipboard.ClipboardEventQueue
import com.liuzhuan.app.net.LanClient

/**
 * 推送到 PC 动作 —— 把 ClipboardEvent 通过现有 WebSocket 协议（clipboard_push）发送。
 *
 * 关键点：
 * 1. 捕获与网络解耦：断线时事件入队，不阻塞捕获链路；重连成功后由协调器补发。
 * 2. 敏感信息不打日志：只打 source/type/length/fingerprint 前 8 位，绝不打 content。
 * 3. 不修改 PC↔Android 现有协议（仍走 client.pushClipboard）。
 */
class PushToPcAction(
    private val queue: ClipboardEventQueue
) : ClipboardAction {

    override suspend fun execute(event: ClipboardEvent) {
        val client = LanHub.client
        if (client?.state is LanClient.State.Connected) {
            // 已连接：先补发积压，再发当前（FIFO 保序）
            if (queue.isNotEmpty()) {
                queue.drain().forEach { send(client, it) }
            }
            send(client, event)
        } else {
            queue.enqueue(event)
            Log.d(TAG, "未连接，事件入队（队列长度=${queue.size()}）")
        }
    }

    private fun send(client: LanClient, event: ClipboardEvent) {
        val text = event.text ?: return
        val ok = client.pushClipboard(text, event.sourcePackage ?: "android")
        Log.d(
            TAG,
            "source=${event.source} type=${event.type} len=${text.length} fp=${event.fingerprint.take(8)} ok=$ok"
        )
    }

    private companion object {
        const val TAG = "PushToPc"
    }
}
