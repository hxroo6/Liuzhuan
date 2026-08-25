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
import kotlinx.coroutines.runBlocking

/**
 * 剪贴板监控（后台自动发送）
 *
 * 核心机制（沿用已验证可用的 GitHub 版逻辑）：
 * 1. 无障碍事件触发（窗口切换/内容变化/聚焦/点击/文本选择变化）→ 立即读剪贴板
 * 2. 轮询兜底（每 2s 读一次对比）→ 复制即使没触发事件也能捕捉
 * 3. 服务连接时读一次
 *
 * 去重：内容与上次相同 → 跳过（有差异才发送）。
 */
class ClipMonitorService : AccessibilityService() {

    private val prefs by lazy { getSharedPreferences("clip", Context.MODE_PRIVATE) }
    private val settingsStore by lazy { SettingsStore(applicationContext) }
    private var lastText: String = prefs.getString("last_text", "") ?: ""
    private var lastPushTime: Long = 0
    private val debounceMs = 2_000L // 同内容 2s 内不重复推送

    // 轮询兜底
    private val handler = Handler(Looper.getMainLooper())
    private val pollRunnable = object : Runnable {
        override fun run() {
            readClipboardAndPush("poll")
            handler.postDelayed(this, 2_000L)
        }
    }

    /** 后台自动发送开关（DataStore 有内存缓存，first() 立即返回，不阻塞） */
    private fun autoSendEnabled(): Boolean = try {
        runBlocking { settingsStore.settings.first().autoSendClipboard }
    } catch (e: Exception) {
        true // 读取失败默认开启
    }

    override fun onServiceConnected() {
        super.onServiceConnected()
        readClipboardAndPush("service_connected")
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
                readClipboardAndPush("event_" + event.eventType)
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
    private fun readClipboardAndPush(trigger: String) {
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
        // 防抖：2s 内不重复推送
        if (now - lastPushTime < debounceMs) return

        lastText = text
        lastPushTime = now
        prefs.edit().putString("last_text", text).apply()

        android.util.Log.d("ClipMonitor", "检测到新剪贴板 (trigger=$trigger, len=${text.length})")

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
