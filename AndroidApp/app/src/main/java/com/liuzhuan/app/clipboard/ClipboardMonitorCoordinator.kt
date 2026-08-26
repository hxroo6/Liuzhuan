package com.liuzhuan.app.clipboard

import android.content.Context
import android.util.Log
import com.liuzhuan.app.LanHub
import com.liuzhuan.app.clipboard.action.ClipboardActionPipeline
import com.liuzhuan.app.clipboard.action.PushToPcAction
import com.liuzhuan.app.net.LanClient
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

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

    /** App 前台/后台状态（进程级追踪，供链路诊断标注 appState） */
    @Volatile
    var appInForeground: Boolean = true
        private set

    /** 实时诊断信息（供 UI 显示，避免依赖 logcat） */
    private val _diagnostic = MutableStateFlow("等待复制事件…")
    val diagnostic: StateFlow<String> = _diagnostic.asStateFlow()

    /** 诊断历史（最近几条，切前台后可回看后台发生了什么） */
    private val _diagnosticHistory = MutableStateFlow<List<String>>(emptyList())
    val diagnosticHistory: StateFlow<List<String>> = _diagnosticHistory.asStateFlow()

    lateinit var captureManager: ClipboardCaptureManager
        private set

    @Volatile
    private var initialized = false

    fun setDiagnostic(msg: String) {
        _diagnostic.value = msg
        _diagnosticHistory.value = (_diagnosticHistory.value + msg).takeLast(6)
        Log.d(TAG, msg)
    }

    /** appState 标签（FOREGROUND/BACKGROUND） */
    fun appStateTag(): String = if (appInForeground) "FOREGROUND" else "BACKGROUND"

    /** 幂等装配（无障碍服务 onCreate 调用；可能被系统多次创建） */
    fun init(context: Context) {
        if (initialized) return
        synchronized(this) {
            if (initialized) return
            initialized = true
            captureManager = ClipboardCaptureManagerImpl(context.applicationContext)
            pipeline.add(PushToPcAction(queue))
            trackAppVisibility(context.applicationContext)
            Log.d(TAG, "已装配：capture + dedup + queue + pipeline + PushToPc")
        }
    }

    /** 用 ActivityLifecycleCallbacks 追踪进程前台/后台（不依赖单个 Activity 存活） */
    private fun trackAppVisibility(app: Context) {
        val application = app as? android.app.Application ?: return
        application.registerActivityLifecycleCallbacks(
            object : android.app.Application.ActivityLifecycleCallbacks {
                private var startedCount = 0
                override fun onActivityStarted(activity: android.app.Activity) {
                    startedCount++
                    val now = startedCount > 0
                    if (now != appInForeground) {
                        appInForeground = now
                        Log.d(TAG, "[VIS] appState=FOREGROUND (started=$startedCount)")
                    }
                }
                override fun onActivityStopped(activity: android.app.Activity) {
                    startedCount = (startedCount - 1).coerceAtLeast(0)
                    val now = startedCount > 0
                    if (now != appInForeground) {
                        appInForeground = now
                        Log.d(TAG, "[VIS] appState=BACKGROUND (started=$startedCount)")
                    }
                }
                override fun onActivityCreated(a: android.app.Activity, b: android.os.Bundle?) {}
                override fun onActivityResumed(a: android.app.Activity) {}
                override fun onActivityPaused(a: android.app.Activity) {}
                override fun onActivitySaveInstanceState(a: android.app.Activity, b: android.os.Bundle) {}
                override fun onActivityDestroyed(a: android.app.Activity) {}
            }
        )
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
        Log.d(TAG, "[OUT_QUEUE] flush start size=${pending.size} appState=${appStateTag()}")
        pending.forEach { ev ->
            val text = ev.text ?: return@forEach
            Log.d(TAG, "[OUT_QUEUE] flush send id=${ev.diagnosticId}")
            client.pushClipboard(text, ev.sourcePackage ?: "android")
        }
        Log.d(TAG, "[OUT_QUEUE] flush complete")
    }

    private const val TAG = "ClipCoord"
}
