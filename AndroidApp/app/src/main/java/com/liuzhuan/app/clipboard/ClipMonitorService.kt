package com.liuzhuan.app.clipboard

import android.accessibilityservice.AccessibilityService
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.os.Build
import android.view.accessibility.AccessibilityEvent
import com.liuzhuan.app.MainActivity
import com.liuzhuan.app.R

/**
 * 剪贴板监控无障碍服务（薄触发器）
 *
 * 职责收敛为：接收 AccessibilityEvent → 交给 ClipboardCaptureManager 检测/捕获/分发。
 * 不再：轮询剪贴板、去重、直接调用 WebSocket（这些职责分别由
 * CopyEventDetector / ClipboardCaptureManager / ClipboardEventDispatcher / ActionPipeline 承担）。
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

    override fun onCreate() {
        super.onCreate()
        // 幂等装配剪贴板监控各组件（可能被系统多次创建服务实例）
        ClipboardMonitorCoordinator.init(applicationContext)
        android.util.Log.d("ClipMonitor", "onCreate")
    }

    override fun onServiceConnected() {
        super.onServiceConnected()
        isRunning = true
        ClipboardMonitorCoordinator.onAccessibilityConnected()
        notifyStatus("流转剪贴板监控", "✅ 服务已启动（正在监听复制）")
        android.util.Log.d("ClipMonitor", "服务已连接")
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) {
        event ?: return
        // 统一入口：检测 → 短延迟捕获 → 分发（不在此做任何业务逻辑）
        ClipboardMonitorCoordinator.captureManager.onAccessibilityEvent(event)
    }

    override fun onInterrupt() {
        // 服务被系统中断
    }

    override fun onDestroy() {
        isRunning = false
        ClipboardMonitorCoordinator.onAccessibilityDisconnected()
        super.onDestroy()
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
