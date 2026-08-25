package com.liuzhuan.app.clipboard

import android.accessibilityservice.AccessibilityService
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.widget.Toast
import android.view.accessibility.AccessibilityEvent
import com.liuzhuan.app.LanHub
import com.liuzhuan.app.MainActivity
import com.liuzhuan.app.R
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

    companion object {
        /** 服务真实运行标志（onServiceConnected=true / onDestroy=false），供 UI 检测 */
        @Volatile
        var isRunning: Boolean = false
    }

    private val prefs by lazy { getSharedPreferences("clip", Context.MODE_PRIVATE) }
    private val settingsStore by lazy { SettingsStore(applicationContext) }

    // ⚠️ 严禁在此（字段初始化=构造期）访问 Context：Service 构造函数执行时
    // mBase 尚未 attach（handleCreateService: instantiateService → attach → onCreate），
    // 构造期调 getSharedPreferences 会 NPE → 服务创建崩溃 → 系统绑定失败 →
    // onServiceConnected 永不触发（表现为「开关已开但服务未运行」）。
    // 所有需要 Context 的初始化一律延迟到 onCreate()/首次使用（lazy）。
    private var lastText: String = ""
    private var lastPushTime: Long = 0
    private val debounceMs = 2_000L // 同内容 2s 内不重复推送

    override fun onCreate() {
        super.onCreate()
        // onCreate 时 mBase 已注入，这里访问 Context 安全
        lastText = try {
            prefs.getString("last_text", "") ?: ""
        } catch (_: Exception) {
            ""
        }
        android.util.Log.d("ClipMonitor", "onCreate: lastText 已加载 (len=${lastText.length})")
    }

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
        isRunning = true
        Toast.makeText(this, "🔌 剪贴板监控已启动", Toast.LENGTH_SHORT).show()
        notifyStatus("流转剪贴板监控", "✅ 服务已启动（正在监听复制）")
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
        isRunning = false
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

        // 未连接电脑则不推送（并弹诊断 Toast，方便定位）
        val client = LanHub.client
        if (client == null) {
            android.util.Log.d("ClipMonitor", "SKIP: LanHub.client is null")
            Toast.makeText(this, "⚠️ 检测到剪贴板，但连接未初始化", Toast.LENGTH_LONG).show()
            notifyStatus("流转", "⚠️ 检测到剪贴板，但连接未初始化")
            return
        }
        if (client.state !is LanClient.State.Connected) {
            android.util.Log.d("ClipMonitor", "SKIP: client state = ${client.state}")
            Toast.makeText(this, "⚠️ 检测到剪贴板，但未连接(${client.state})", Toast.LENGTH_LONG).show()
            notifyStatus("流转", "⚠️ 检测到剪贴板，但未连接(${client.state})")
            return
        }

        val ok = client.pushClipboard(text, packageName)
        android.util.Log.d("ClipMonitor", "pushClipboard result: $ok")
        if (ok) {
            Toast.makeText(this, "⚡ 流转已复制", Toast.LENGTH_SHORT).show()
            notifyStatus("流转", "⚡ 已复制并发送到电脑")
        } else {
            Toast.makeText(this, "❌ 推送失败", Toast.LENGTH_LONG).show()
            notifyStatus("流转", "❌ 推送失败")
        }
    }

    /** 发诊断通知（Toast 在部分 ROM 可能被吞，通知栏更可靠） */
    private fun notifyStatus(title: String, content: String) {
        try {
            val channelId = "clip_diag"
            val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
            if (Build.VERSION.SDK_INT >= 26) {
                nm.createNotificationChannel(
                    NotificationChannel(channelId, "剪贴板监控诊断", NotificationManager.IMPORTANCE_DEFAULT)
                )
            }
            val pi = PendingIntent.getActivity(
                this, 0, Intent(this, MainActivity::class.java),
                PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
            )
            val n = Notification.Builder(this, channelId)
                .setContentTitle(title)
                .setContentText(content)
                .setSmallIcon(R.drawable.ic_notification)
                .setContentIntent(pi)
                .setAutoCancel(true)
                .build()
            nm.notify(2001, n)
        } catch (e: Exception) {
            android.util.Log.d("ClipMonitor", "notify failed: ${e.message}")
        }
    }
}
