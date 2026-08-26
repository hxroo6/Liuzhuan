package com.liuzhuan.app.clipboard

import android.content.Context
import android.util.Log
import com.liuzhuan.app.LanHub
import com.liuzhuan.app.clipboard.action.ClipboardActionPipeline
import com.liuzhuan.app.clipboard.action.PushToPcAction
import com.liuzhuan.app.net.LanClient

/**
 * 剪贴板监控协调器 —— 进程级单例，装配所有组件，统一管理生命周期与状态。
 *
 * 状态机：STOPPED → STARTING → RUNNING / DEGRADED / ERROR
 *
 * 避免多个 Service 重复初始化：init() 幂等，只装配一次。
 * 使用 applicationContext，不持有 Activity/Service 引用，避免 Context 泄漏。
 */
object ClipboardMonitorCoordinator {

    enum class State { STOPPED, STARTING, RUNNING, DEGRADED, ERROR }

    @Volatile
    var state: State = State.STOPPED
        private set

    val deduplicator = ClipboardDeduplicator()
    val queue = ClipboardEventQueue()
    val pipeline = ClipboardActionPipeline()

    lateinit var captureManager: ClipboardCaptureManager
        private set

    @Volatile
    private var initialized = false

    /** 幂等装配（无障碍服务 onCreate 调用；可能被系统多次创建） */
    fun init(context: Context) {
        if (initialized) return
        synchronized(this) {
            if (initialized) return
            initialized = true
            captureManager = ClipboardCaptureManagerImpl(context.applicationContext)
            pipeline.add(PushToPcAction(queue))
            Log.d(TAG, "已装配：capture + dedup + queue + pipeline + PushToPc")
        }
    }

    fun onAccessibilityConnected() {
        state = State.RUNNING
        Log.d(TAG, "无障碍服务已连接")
    }

    fun onAccessibilityDisconnected() {
        state = State.STOPPED
        Log.d(TAG, "无障碍服务已断开")
    }

    /** 标记本 App 本地写入剪贴板的内容（循环回写防护：PC→手机复制后不再回推 PC） */
    fun markLocalText(text: String) {
        deduplicator.markLocal(text)
    }

    /** WS 连接成功 → 补发断线期间积压的事件（FIFO） */
    fun onWsConnected() {
        val client = LanHub.client ?: return
        if (client.state !is LanClient.State.Connected) return
        val pending = queue.drain()
        if (pending.isEmpty()) return
        Log.d(TAG, "WS 恢复，补发 ${pending.size} 条积压")
        pending.forEach { ev ->
            val text = ev.text ?: return@forEach
            client.pushClipboard(text, ev.sourcePackage ?: "android")
        }
    }

    private const val TAG = "ClipCoord"
}
