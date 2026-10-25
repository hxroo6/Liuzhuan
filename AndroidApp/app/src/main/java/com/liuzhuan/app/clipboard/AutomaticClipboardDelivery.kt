package com.liuzhuan.app.clipboard

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull

/** FIFO、登记确认后出队、有界重试。系统/网络副作用由接口注入，检查执行生产状态机。 */
class AutomaticClipboardDelivery(
    private val queue: ClipboardEventQueue,
    private val transport: () -> Transport?,
    private val guarded: (ClipboardEvent, () -> Unit) -> Boolean,
    private val diagnostic: (ClipboardEvent, String) -> Unit,
    private val scope: CoroutineScope = CoroutineScope(SupervisorJob() + Dispatchers.IO),
    private val now: () -> Long = System::currentTimeMillis,
    private val receiptTimeoutMs: Long = 20_000,
    private val retryDelayMs: Long = 1_000
) {
    interface Transport {
        val identity: String
        val supportsReceipts: Boolean
        val durableReceipts: Boolean get() = false
        fun send(event: ClipboardEvent): Boolean
    }
    private val signals = Channel<Unit>(Channel.CONFLATED)
    @Volatile private var target: String? = null

    init {
        queue.onChanged = { wake() }
        queue.onDropped = { event, message -> diagnostic(event, message) }
        scope.launch {
            while (true) {
                val wait = try {
                    val next=step()
                    val expiry=queue.nextExpiry()?.let {(it-now()).coerceAtLeast(1)}
                    when {next==null->expiry;expiry==null->next;else->minOf(next,expiry)}
                }catch(ex:Exception){queue.snapshot().firstOrNull()?.let {diagnostic(it.event,"待发记录未能保存，已停止发送；请检查存储后重连")};null}
                if (wait == null) signals.receive()
                else withTimeoutOrNull(wait.coerceAtLeast(1)) { signals.receive() }
            }
        }
    }

    fun wake() { signals.trySend(Unit) }
    fun changeTarget(identity: String) {
        target = identity
        queue.changeTarget(identity)
        wake()
    }
    fun submit(event: ClipboardEvent, destination:String?=target, stillCurrent:()->Boolean={true}):Boolean {
        var accepted=false
        guarded(event) {
            if(!stillCurrent() || (destination!=null && target!=null && destination!=target)) {
                diagnostic(event,"连接电脑已变更，旧请求已取消；请重新提交文字")
                return@guarded
            }
            // A concurrent switch can cancel this request, but can never rebind it
            // to the newly selected target between validation and durable insertion.
            queue.enqueue(event, destination)
            accepted=true
            diagnostic(event, if (transport() == null) "等待连接电脑，文字已进入有期限的待确认队列"
                else "文字已进入待确认队列（${queue.size()}）")
        }
        return accepted
    }

    /** 晚到回执只确认仍在队列且属于该电脑的请求，不覆盖取消诊断。 */
    fun acknowledge(identity: String, requestId: String, status: String, persistReceipt:()->Unit={}):Boolean {
        if (status != "stored" && status != "rejected") return false
        val candidate = queue.find(requestId) ?: return false
        var removed:ClipboardEventQueue.Pending?=null
        var expired=false
        guarded(candidate.event) {
            // Serialize the final check, receipt journal and removal with cancellation,
            // target changes and another enqueue reusing a manually supplied request ID.
            synchronized(queue) {
                val pending=queue.find(requestId) ?: return@synchronized
                if(pending.event!=candidate.event || pending.target!=identity || pending.attempts==0)return@synchronized
                expired=now()>=pending.expiresAt
                if(!expired)persistReceipt()
                removed=queue.remove(requestId)
            }
        }
        val confirmed=removed ?: return false
        diagnostic(confirmed.event, if(expired)"请求已超期，晚到回执已忽略；请检查电脑"
            else if (status == "stored") "电脑已登记文字" else "电脑拒绝登记；请检查电脑后重新复制")
        return !expired
    }

    /** 时间驱动的一步状态机，无剪贴板/网络轮询。 */
    internal fun step(): Long? {
        if(!queue.persistenceReady)return null
        queue.expire(now()).forEach {diagnostic(it.event,"请求已超期，未确认电脑是否登记；请检查电脑后重新复制")}
        val pending = queue.head() ?: return null
        val event = pending.event
        val at = now()
        if (at >= pending.expiresAt) {
            drop(event, "请求已超期，未确认电脑是否登记；请检查电脑后重新复制")
            return 1
        }
        if (!guarded(event) {}) {
            drop(event, "隐私规则已拦截，这次自动发送已取消")
            return 1
        }
        if (pending.awaitingReceipt) {
            if (at < pending.nextAt) return minOf(pending.nextAt, pending.expiresAt) - at
            if (!pending.receiptCapable || pending.attempts >= MAX_ATTEMPTS) {
                stopOrDrop(pending, if (!pending.receiptCapable) "未收到登记确认；旧版电脑不支持安全重试，请检查电脑并升级" else "三次提交仍未收到登记确认；请检查电脑后重新复制")
            } else {
                queue.update(event.requestId) { it.copy(awaitingReceipt = false, nextAt = at + retryDelayMs * it.attempts) }
                diagnostic(event, "未收到登记确认，将使用同一请求重试（${pending.attempts}/$MAX_ATTEMPTS）")
            }
            return 1
        }
        val current = transport() ?: return pending.expiresAt - at
        if((pending.restored || at-pending.queuedAt>=9*60_000L) && pending.mayHaveRegistered && !current.durableReceipts) {
            drop(event,"旧版电脑没有持久回执，已停止跨重启重发；请先检查电脑并升级")
            return 1
        }
        if (pending.target != null && pending.target != current.identity) {
            drop(event, "连接电脑已变更，旧请求已取消")
            return 1
        }
        if (at < pending.nextAt) return minOf(pending.nextAt, pending.expiresAt) - at
        if (pending.mayHaveRegistered && !current.supportsReceipts) {
            drop(event, "电脑未声明安全重试能力，已停止重发；请检查电脑并升级")
            return 1
        }
        if (pending.attempts >= MAX_ATTEMPTS) {
            stopOrDrop(pending, "发送未成功，已停止自动重试；请检查电脑后重新复制")
            return 1
        }
        guarded(event) {
            val active = queue.find(event.requestId) ?: return@guarded
            if (active.target != null && active.target != current.identity) return@guarded
            val attempt = active.attempts + 1
            // send 可能立即触发另一个线程的 ack；提交前安装关联，确认后不能再写回等待状态。
            queue.update(event.requestId) {
                it.copy(attempts = attempt, target = current.identity, receiptCapable = current.supportsReceipts,
                    awaitingReceipt = true, mayHaveRegistered=true, nextAt = now() + receiptTimeoutMs)
            }
            val accepted = try { current.send(event) } catch (_: Exception) { false }
            val stillPending = queue.update(event.requestId) {
                it.copy(attempts = attempt, target = current.identity, receiptCapable = current.supportsReceipts,
                    awaitingReceipt = accepted, mayHaveRegistered = it.mayHaveRegistered || accepted,
                    nextAt = now() + if (accepted) receiptTimeoutMs else retryDelayMs * attempt)
            }
            if (stillPending != null) diagnostic(event, if (accepted) "文字已提交，等待电脑登记确认（$attempt/$MAX_ATTEMPTS）"
                else "发送未成功，将有限重试（$attempt/$MAX_ATTEMPTS）")
        }
        return 1
    }

    private fun drop(event: ClipboardEvent, reason: String) {
        if (queue.remove(event.requestId) != null) diagnostic(event, reason)
    }
    private fun stopOrDrop(pending:ClipboardEventQueue.Pending,reason:String) {
        if(pending.event.source!=ClipboardCaptureSource.MANUAL){drop(pending.event,reason);return}
        queue.update(pending.event.requestId){it.copy(held=true,awaitingReceipt=false)}
        diagnostic(pending.event,"自动重试已停止，文字仍在待发中；请检查电脑后点击重试或取消")
    }
    companion object { const val MAX_ATTEMPTS = 3 }
}
