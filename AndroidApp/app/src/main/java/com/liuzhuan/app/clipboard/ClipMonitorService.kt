package com.liuzhuan.app.clipboard

import android.accessibilityservice.AccessibilityService
import android.content.ClipboardManager
import android.content.Context
import android.widget.Toast
import android.view.accessibility.AccessibilityEvent
import com.liuzhuan.app.LanHub
import com.liuzhuan.app.core.SettingsStore
import com.liuzhuan.app.net.LanClient
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking

/**
 * 剪贴板监控（决策 #3：无障碍方案）
 * Android 10+ 后台读剪贴板的唯一可靠方式：
 * 监听窗口/视图事件 → 读取剪贴板文字 → 推送到电脑端
 *
 * 关键点：响应任意事件（窗口切换、视图聚焦、点击、内容变化）都尝试读一次剪贴板，
 * 这样「快捷启用」图标点击、App 切换、复制操作都能触发推送。
 */
class ClipMonitorService : AccessibilityService() {

    private val prefs by lazy { getSharedPreferences("clip", Context.MODE_PRIVATE) }
    private val settingsStore by lazy { SettingsStore(applicationContext) }
    private var lastText: String = prefs.getString("last_text", "") ?: ""
    private var lastPushTime: Long = 0
    private val debounceMs = 2_000L // 额外防抖：同内容 2s 内不重复推送

    /** 后台自动发送开关（发送页可关闭） */
    private fun autoSendEnabled(): Boolean = try {
        runBlocking { settingsStore.settings.first().autoSendClipboard }
    } catch (e: Exception) {
        true // 读取失败默认开启，不阻塞复制秒达
    }

    override fun onServiceConnected() {
        super.onServiceConnected()
        // 服务首次连接成功时，主动读一次剪贴板（用户可能刚复制）
        readClipboardAndPush("service_connected")
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) {
        event ?: return
        // 响应所有感兴趣的事件类型
        when (event.eventType) {
            AccessibilityEvent.TYPE_WINDOW_STATE_CHANGED,
            AccessibilityEvent.TYPE_WINDOW_CONTENT_CHANGED,
            AccessibilityEvent.TYPE_VIEW_FOCUSED,
            AccessibilityEvent.TYPE_VIEW_CLICKED -> {
                readClipboardAndPush("event_" + event.eventType)
            }
        }
    }

    override fun onInterrupt() {
        // 服务被系统中断时调用
    }

    /** 读取剪贴板并推送（仅剪贴板内容变化时；持久化防重复） */
    private fun readClipboardAndPush(trigger: String) {
        // 调试日志（定位不推送问题）
        android.util.Log.d("ClipMonitor", "readClipboardAndPush triggered: $trigger")

        // 后台自动发送开关（发送页可关闭）
        if (!autoSendEnabled()) {
            android.util.Log.d("ClipMonitor", "SKIP: autoSend disabled")
            return
        }

        val cm = getSystemService(Context.CLIPBOARD_SERVICE) as? ClipboardManager ?: run {
            android.util.Log.d("ClipMonitor", "SKIP: no ClipboardManager")
            return
        }
        if (!cm.hasPrimaryClip()) {
            android.util.Log.d("ClipMonitor", "SKIP: no primary clip")
            return
        }
        val clip = cm.primaryClip ?: return
        if (clip.itemCount == 0) return

        val text = clip.getItemAt(0)?.coerceToText(this)?.toString()?.trim() ?: return
        if (text.isEmpty() || text.length < 2) {
            android.util.Log.d("ClipMonitor", "SKIP: text too short (${text.length})")
            return
        }

        val now = System.currentTimeMillis()
        // 核心去重：内容与上次推送完全相同 → 直接跳过
        if (text == lastText) {
            android.util.Log.d("ClipMonitor", "SKIP: same as last text")
            return
        }
        // 额外防抖：即使内容不同，2s 内不重复
        if (now - lastPushTime < debounceMs) {
            android.util.Log.d("ClipMonitor", "SKIP: debounce (${now - lastPushTime}ms < ${debounceMs}ms)")
            return
        }

        lastText = text
        lastPushTime = now
        prefs.edit().putString("last_text", text).apply()

        // 未连接电脑则不推送、不提示
        val client = LanHub.client ?: run {
            android.util.Log.d("ClipMonitor", "SKIP: LanHub.client is null")
            return
        }
        if (client.state !is LanClient.State.Connected) {
            android.util.Log.d("ClipMonitor", "SKIP: client state = ${client.state}")
            return
        }

        val ok = client.pushClipboard(text, packageName)
        android.util.Log.d("ClipMonitor", "pushClipboard result: $ok, text len=${text.length}")
        if (ok) {
            Toast.makeText(this, "⚡ 流转已复制", Toast.LENGTH_SHORT).show()
        }
    }
}