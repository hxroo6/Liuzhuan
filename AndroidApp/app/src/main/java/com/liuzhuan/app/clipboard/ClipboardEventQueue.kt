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
        val target: String? = null, val receiptCapable: Boolean = false, val mayHaveRegistered: Boolean = false,
        val restored: Boolean = false, val held:Boolean=false)
    private val queue = linkedMapOf<String, Pending>()
    var onChanged: () -> Unit = {}
    var onDropped: (ClipboardEvent, String) -> Unit = { _, _ -> }
    @Volatile var persistenceReady = true
    private var persist: (List<Pending>)->Unit = {}
    @Synchronized fun attachPersistence(loaded:List<Pending>,writer:(List<Pending>)->Unit) {
        val merged=(loaded.filter {it.expiresAt>now() && it.target!=null}.map {it.copy(restored=true)}+queue.values).distinctBy {it.event.requestId}.takeLast(maxSize)
        writer(merged);queue.clear();merged.forEach {queue[it.event.requestId]=it};persist=writer;persistenceReady=true;onChanged()
    }
    @Synchronized fun snapshot():List<Pending> = queue.values.toList()
    @Synchronized fun rebindVerifiedTarget(old:String,new:String) {
        if(old==new || queue.values.none {it.target==old})return
        val replacement=queue.values.map {if(it.target==old)it.copy(target=new) else it}
        persist(replacement);queue.clear();replacement.forEach {queue[it.event.requestId]=it}
    }

    @Synchronized
    fun enqueue(event: ClipboardEvent, target: String? = null) {
        if (queue.containsKey(event.requestId)) return
        val oldest=if(queue.size>=maxSize)queue.values.first() else null
        val at = now()
        val pending=Pending(event, at, at + if(event.source==ClipboardCaptureSource.MANUAL) MANUAL_MAX_AGE_MS else MAX_AGE_MS, target = target)
        persist(queue.values.filterNot {it.event.requestId==oldest?.event?.requestId}+pending)
        if(oldest!=null){queue.remove(oldest.event.requestId);onDropped(oldest.event,"待确认队列已满，旧请求已取消；请检查电脑后重新复制")}
        queue[event.requestId] = pending
        onChanged()
    }

    /** 取出并清空全部积压事件（按入队顺序） */
    @Synchronized
    fun drain(): List<ClipboardEvent> {
        val out = queue.values.map { it.event }
        persist(emptyList());queue.clear()
        onChanged()
        return out
    }

    @Synchronized
    fun isNotEmpty(): Boolean = queue.isNotEmpty()

    @Synchronized
    fun size(): Int = queue.size

    @Synchronized fun head(): Pending? = queue.values.firstOrNull { !it.held }
    /** Held requests stop retries, not their retention clock. Persist once before changing memory. */
    @Synchronized fun expire(at:Long):List<Pending> {
        val expired=queue.values.filter {it.expiresAt<=at}
        if(expired.isEmpty())return emptyList()
        persist(queue.values.filter {it.expiresAt>at})
        expired.forEach {queue.remove(it.event.requestId)}
        onChanged()
        return expired
    }
    @Synchronized fun nextExpiry():Long? = queue.values.minOfOrNull {it.expiresAt}
    @Synchronized fun find(id: String): Pending? = queue[id]
    @Synchronized fun update(id: String, transform: (Pending) -> Pending): Pending? =
        queue[id]?.let { transform(it).also { replacement -> persist(queue.values.map {if(it.event.requestId==id)replacement else it});queue[id] = replacement } }
    @Synchronized fun remove(id: String): Pending? = queue[id]?.also {persist(queue.values.filterNot {it.event.requestId==id});queue.remove(id);onChanged()}

    /** 隐私设置变化后，自动缓存不在恢复连接时补发；显式动作不受影响。 */
    @Synchronized
    fun discardAutomatic() {
        val discarded = queue.values.filter { it.event.source != ClipboardCaptureSource.MANUAL }
        persist(queue.values.filter {it.event.source==ClipboardCaptureSource.MANUAL})
        discarded.forEach { queue.remove(it.event.requestId); onDropped(it.event, "隐私规则已变化，这次自动发送已取消") }
        onChanged()
    }

    @Synchronized
    fun changeTarget(target: String) {
        val discarded = queue.values.filter { it.target != null && it.target != target }
        persist(queue.values.filter {it.target==null || it.target==target}.map {it.copy(target=target)})
        discarded.forEach { queue.remove(it.event.requestId); onDropped(it.event, "连接电脑已变更，旧请求已取消") }
        queue.entries.forEach { it.setValue(it.value.copy(target = target)) }
        onChanged()
    }

    companion object { const val MAX_AGE_MS = 5 * 60_000L; const val MANUAL_MAX_AGE_MS = 7 * 24 * 60 * 60_000L }
}
