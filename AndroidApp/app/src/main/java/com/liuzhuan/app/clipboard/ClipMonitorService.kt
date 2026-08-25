package com.liuzhuan.app.clipboard

import android.accessibilityservice.AccessibilityService
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.widget.Toast
import android.view.accessibility.AccessibilityEvent
import com.liuzhuan.app.MainActivity
import com.liuzhuan.app.R

/**
 * 剪贴板监控（后台自动发送）
 *
 * 触发路径：无障碍事件（窗口切换/内容变化/聚焦/点击）+ 2s 轮询兜底。
 * 实际读取/去重/推送统一走 [ClipPusher]。
 *
 * ⚠️ Android 10+ 焦点限制（平台行为，非 bug）：流转在后台时
 * primaryClip 读取被系统拒绝 → 只有流转切到前台（获得焦点）瞬间才能读到。
 * 「零切换发送」请用文本选择菜单（ProcessTextActivity）。
 *
 * ⚠️ 严禁在字段初始化（构造期）访问 Context：mBase 尚未 attach 会 NPE，
 * 导致服务创建即崩溃、系统绑定失败（详见 2026-08-26 排查记录）。
 */
class ClipMonitorService : AccessibilityService() {

    companion object {
        /** 服务真实运行标志（onServiceConnected=true / onDestroy=false），供 UI 检测 */
        @Volatile
        var isRunning: Boolean = false
    }

    // 轮询兜底
    private val handler = Handler(Looper.getMainLooper())
    private val pollRunnable = object : Runnable {
        override fun run() {
            if (ClipPusher.checkAndPush(this@ClipMonitorService, "poll")) {
                onPushed()
            }
            handler.postDelayed(this, 2_000L)
        }
    }

    override fun onCreate() {
        super.onCreate()
        android.util.Log.d("ClipMonitor", "onCreate")
    }

    override fun onServiceConnected() {
        super.onServiceConnected()
        isRunning = true
        Toast.makeText(this, "🔌 剪贴板监控已启动", Toast.LENGTH_SHORT).show()
        notifyStatus("流转剪贴板监控", "✅ 服务已启动（正在监听复制）")
        if (ClipPusher.checkAndPush(this, "service_connected")) {
            onPushed()
        }
        handler.postDelayed(pollRunnable, 2_000L)
        android.util.Log.d("ClipMonitor", "服务已连接，轮询每2s启动")
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) {
        event ?: return
        when (event.eventType) {
            AccessibilityEvent.TYPE_WINDOW_STATE_CHANGED,
            AccessibilityEvent.TYPE_WINDOW_CONTENT_CHANGED,
            AccessibilityEvent.TYPE_VIEW_FOCUSED,
            AccessibilityEvent.TYPE_VIEW_CLICKED -> {
                if (ClipPusher.checkAndPush(this, "event_${event.eventType}")) {
                    onPushed()
                }
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

    /** 推送成功的用户反馈 */
    private fun onPushed() {
        try {
            Toast.makeText(this, "⚡ 流转已复制", Toast.LENGTH_SHORT).show()
            notifyStatus("流转", "⚡ 已复制并发送到电脑")
        } catch (_: Exception) {
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
