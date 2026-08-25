package com.liuzhuan.app.clipboard

import android.accessibilityservice.AccessibilityService
import android.content.ClipboardManager
import android.content.Context
import android.os.Handler
import android.os.Looper
import android.widget.Toast
import android.view.accessibility.AccessibilityEvent
import com.liuzhuan.app.LanHub
import com.liuzhuan.app.core.SettingsStore
import com.liuzhuan.app.net.LanClient
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch

/**
 * 剪贴板监控（后台自动发送）
 *
 * 核心思路：Android 无「复制」这一直接事件，只能监听「剪贴板内容变化」——
 * 内容变了 = 用户刚复制/剪切了东西，此时才读取并推送（有差异才发送）。
 *
 * 三种触发方式组合，保证后台也能可靠捕捉：
 * 1. 无障碍事件（文本选择变化/窗口切换/点击等）→ 复制后立即读一次（快）
 * 2. 轮询兜底（每 2s 读一次对比）→ 即使复制动作没触发任何事件也能捕捉（稳）
 * 3. 服务连接时读一次（用户可能刚复制才开服务）
 *
 * 去重：内容与上次相同 → 跳过（避免重复推送）。
 */
class ClipMonitorService : AccessibilityService() {

    private val prefs by lazy { getSharedPreferences("clip", Context.MODE_PRIVATE) }
    private val settingsStore by lazy { SettingsStore(applicationContext) }
    private var lastText: String = prefs.getString("last_text", "") ?: ""
    private var lastPushTime: Long = 0
    private val debounceMs = 2_000L // 同内容 2s 内不重复推送

    // 轮询兜底：每 2s 读一次剪贴板，开销极小（读字符串+对比）
    private val handler = Handler(Looper.getMainLooper())
    private val pollRunnable = object : Runnable {
        override fun run() {
            checkClipboard("poll")
            handler.postDelayed(this, 2_000L)
        }
    }

    /** 后台自动发送开关（非阻塞读 @Volatile，避免 runBlocking 卡死主线程） */
    private fun autoSendEnabled(): Boolean = LanHub.autoSendClipboard

    override fun onServiceConnected() {
        super.onServiceConnected()
        // 异步初始化开关值（不阻塞主线程；MainActivity 已启动时会覆盖，服务单独启动时兜底）
        kotlinx.coroutines.CoroutineScope(kotlinx.coroutines.Dispatchers.IO).launch {
            try {
                LanHub.autoSendClipboard = settingsStore.settings.first().autoSendClipboard
            } catch (e: Exception) {
                LanHub.autoSendClipboard = true
            }
        }
        checkClipboard("service_connected")
        handler.postDelayed(pollRunnable, 2_000L)
        android.util.Log.d("ClipMonitor", "服务已连接，轮询每2s启动")
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) {
        event ?: return
        when (event.eventType) {
            AccessibilityEvent.TYPE_WINDOW_STATE_CHANGED,
            AccessibilityEvent.TYPE_WINDOW_CONTENT_CHANGED,
            AccessibilityEvent.TYPE_VIEW_FOCUSED,
            AccessibilityEvent.TYPE_VIEW_CLICKED,
            AccessibilityEvent.TYPE_VIEW_TEXT_CHANGED,
            AccessibilityEvent.TYPE_VIEW_TEXT_SELECTION_CHANGED,
            AccessibilityEvent.TYPE_VIEW_SCROLLED -> {
                checkClipboard("event_" + event.eventType)
            }
        }
    }

    override fun onInterrupt() {
        // 服务被系统中断
    }

    override fun onDestroy() {
        handler.removeCallbacks(pollRunnable)
        super.onDestroy()
    }

    /** 读取剪贴板，内容有变化才推送 */
    private fun checkClipboard(trigger: String) {
        if (!autoSendEnabled()) return

        val cm = getSystemService(Context.CLIPBOARD_SERVICE) as? ClipboardManager ?: return
        if (!cm.hasPrimaryClip()) return
        val clip = cm.primaryClip ?: return
        if (clip.itemCount == 0) return

        val text = clip.getItemAt(0)?.coerceToText(this)?.toString()?.trim() ?: return
        if (text.isEmpty() || text.length < 2) return

        // 有差异才处理：内容与上次相同 → 跳过
        if (text == lastText) return

        val now = System.currentTimeMillis()
        // 防抖：2s 内不重复推送（防止快速连续事件）
        if (now - lastPushTime < debounceMs) return

        lastText = text
        lastPushTime = now
        prefs.edit().putString("last_text", text).apply()

        android.util.Log.d("ClipMonitor", "检测到新剪贴板内容 (trigger=$trigger, len=${text.length})")

        // 未连接电脑则不推送
        val client = LanHub.client ?: run {
            android.util.Log.d("ClipMonitor", "SKIP: LanHub.client is null")
            return
        }
        if (client.state !is LanClient.State.Connected) {
            android.util.Log.d("ClipMonitor", "SKIP: client state = ${client.state}")
            return
        }

        val ok = client.pushClipboard(text, packageName)
        android.util.Log.d("ClipMonitor", "pushClipboard result: $ok")
        if (ok) {
            Toast.makeText(this, "⚡ 流转已复制", Toast.LENGTH_SHORT).show()
        }
    }
}
