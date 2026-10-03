package com.liuzhuan.app.clipboard

/**
 * 剪贴板事件发送队列（FIFO）
 *
 * 自动文字等待登记回执时也保留；条目最多 50、期限 5 分钟。
 * 发送尝试由 AutomaticClipboardDelivery 管理，旧 drain 接口仅保留兼容检查。
 * 线程安全（@Synchronized），供无障碍服务/协调器多线程访问。
 */
class ClipboardEventQueue(private val maxSize: Int = 50, private val now: () -> Long = System::currentTimeMillis) {

    data class Pending(val event: ClipboardEvent, val queuedAt: Long, val expiresAt: Long,
        val attempts: Int = 0, val nextAt: Long = queuedAt, val awaitingReceipt: Boolean = false,
        val target: String? = null, val receiptCapable: Boolean = false, val mayHaveRegistered: Boolean = false)
    private val queue = linkedMapOf<String, Pending>()
    var onChanged: () -> Unit = {}
    var onDropped: (ClipboardEvent, String) -> Unit = { _, _ -> }

    @Synchronized
    fun enqueue(event: ClipboardEvent, target: String? = null) {
        if (queue.containsKey(event.requestId)) return
        if (queue.size >= maxSize) {
            val oldest = queue.values.first()
            queue.remove(oldest.event.requestId)
            onDropped(oldest.event, "待确认队列已满，旧请求已取消；请检查电脑后重新复制")
        }
        val at = now()
        queue[event.requestId] = Pending(event, at, at + MAX_AGE_MS, target = target)
        onChanged()
    }

    /** 取出并清空全部积压事件（按入队顺序） */
    @Synchronized
    fun drain(): List<ClipboardEvent> {
        val out = queue.values.map { it.event }
        queue.clear()
        onChanged()
        return out
    }

    @Synchronized
    fun isNotEmpty(): Boolean = queue.isNotEmpty()

    @Synchronized
    fun size(): Int = queue.size

    @Synchronized fun head(): Pending? = queue.values.firstOrNull()
    @Synchronized fun find(id: String): Pending? = queue[id]
    @Synchronized fun update(id: String, transform: (Pending) -> Pending): Pending? =
        queue[id]?.let { transform(it).also { replacement -> queue[id] = replacement } }
    @Synchronized fun remove(id: String): Pending? = queue.remove(id)?.also { onChanged() }

    /** 隐私设置变化后，自动缓存不在恢复连接时补发；显式动作不受影响。 */
    @Synchronized
    fun discardAutomatic() {
        val discarded = queue.values.filter { it.event.source != ClipboardCaptureSource.MANUAL }
        discarded.forEach { queue.remove(it.event.requestId); onDropped(it.event, "隐私规则已变化，这次自动发送已取消") }
        onChanged()
    }

    @Synchronized
    fun changeTarget(target: String) {
        val discarded = queue.values.filter { it.target != null && it.target != target }
        discarded.forEach { queue.remove(it.event.requestId); onDropped(it.event, "连接电脑已变更，旧请求已取消") }
        queue.entries.forEach { it.setValue(it.value.copy(target = target)) }
        onChanged()
    }

    companion object { const val MAX_AGE_MS = 5 * 60_000L }
}
