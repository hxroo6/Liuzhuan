package com.liuzhuan.app.clipboard

/**
 * 剪贴板事件发送队列（FIFO）
 *
 * 用途：WebSocket 断线时，捕获的事件先入队，等重连成功后按顺序补发。
 * 容量受限（默认 50），防止异常情况下无限积压。
 * 线程安全（@Synchronized），供无障碍服务/协调器多线程访问。
 */
class ClipboardEventQueue(private val maxSize: Int = 50) {

    private val queue = ArrayDeque<ClipboardEvent>()

    @Synchronized
    fun enqueue(event: ClipboardEvent) {
        if (queue.size >= maxSize) queue.removeFirst()
        queue.addLast(event)
    }

    /** 取出并清空全部积压事件（按入队顺序） */
    @Synchronized
    fun drain(): List<ClipboardEvent> {
        val out = queue.toList()
        queue.clear()
        return out
    }

    @Synchronized
    fun isNotEmpty(): Boolean = queue.isNotEmpty()

    @Synchronized
    fun size(): Int = queue.size
}
