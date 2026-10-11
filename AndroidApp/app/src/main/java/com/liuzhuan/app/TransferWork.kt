package com.liuzhuan.app

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import okhttp3.Call

/** 进程级传输，离开页面不停止任务；并发上限含 Hook 文件。 */
object TransferWork {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val slots = Semaphore(2)
    private val jobs = mutableMapOf<String, Job>()
    private val calls = mutableMapOf<String, Call>()
    private val resources = mutableMapOf<String, MutableList<java.io.Closeable>>()
    private val mutableActive = MutableStateFlow(0)
    val active = mutableActive.asStateFlow()

    @Synchronized fun launch(id: String, block: suspend () -> Unit): Boolean {
        val predecessor = jobs[id]
        if (predecessor == null && jobs.size >= 16) {
            TransferTasks.finish(id, false, "等待任务较多，请稍后重新传输")
            return false
        }
        val job = scope.launch(start = CoroutineStart.LAZY) {
            try {
                // 失败记录可先于 I/O 清理完成可见；显式重试等旧任务退出，不能丢掉重试。
                predecessor?.join()
                slots.withPermit {
                    synchronized(this@TransferWork) { mutableActive.value++ }
                    PowerSignals.touch()
                    try { block() }
                    finally { synchronized(this@TransferWork) { mutableActive.value-- } }
                }
            } catch (_: CancellationException) {
                TransferTasks.finish(id, false, "已取消；重新传输前请检查对端是否已收到")
            } catch (ex: Exception) {
                TransferTasks.finish(id, false, ex.message ?: "传输失败")
            }
        }
        jobs[id] = job
        job.invokeOnCompletion {
            synchronized(this) {
                if (jobs[id] === job) {
                    jobs.remove(id)
                    calls.remove(id)
                    resources.remove(id)
                }
            }
        }
        job.start()
        return true
    }

    fun cancel(id: String) {
        val current = synchronized(this) { Triple(calls[id], jobs[id], resources[id]?.toList().orEmpty()) }
        current.first?.cancel()
        current.second?.cancel()
        scope.launch { current.third.forEach { runCatching { it.close() } } }
    }

    /** URI / 缓存流也归任务所有，取消不仅停止网络，还关闭已打开的来源流。 */
    fun <R : java.io.Closeable, T> useResource(id: String, resource: R, block: (R) -> T): T {
        synchronized(this) {
            if (jobs[id]?.isActive != true) { runCatching { resource.close() }; throw CancellationException("已取消") }
            resources.getOrPut(id) { mutableListOf() }.add(resource)
        }
        try { return resource.use(block) }
        finally { synchronized(this) { resources[id]?.remove(resource); if (resources[id]?.isEmpty()==true) resources.remove(id) } }
    }

    suspend fun <T> withCall(id: String, call: Call, block: (Call) -> T): T {
        synchronized(this) { calls[id] = call }
        // 子任务清理结束前不释放传输槽，取消会立即打断底层网络读取。
        return kotlinx.coroutines.coroutineScope {
            kotlinx.coroutines.suspendCancellableCoroutine { continuation ->
                continuation.invokeOnCancellation { call.cancel() }
                launch(Dispatchers.IO) {
                    try {
                        val result = block(call)
                        if (continuation.isActive) continuation.resumeWith(Result.success(result))
                    } catch (ex: Throwable) {
                        if (continuation.isActive) continuation.resumeWith(Result.failure(ex))
                    } finally {
                        synchronized(this@TransferWork) { if (calls[id] === call) calls.remove(id) }
                    }
                }
            }
        }
    }
}
