package com.liuzhuan.app

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.net.wifi.WifiManager
import android.os.Build
import android.os.IBinder
import android.os.PowerManager

/**
 * 前台服务保活（M3）
 * 连接后启动：常驻通知 + START_STICKY，降低后台被杀概率，
 * 让剪贴板监控（无障碍）和 WS 连接在后台稳定运行。
 *
 * 保活锁：
 * - Wi-Fi 锁：防止锁屏后 Wi-Fi 省电休眠 → 局域网 WS 连接被断开（后台断连主因）
 * - CPU 锁（PARTIAL_WAKE_LOCK）：防止深度睡眠，保证心跳/重连协程正常调度
 */
class ForegroundService : Service() {

    private var wakeLock: PowerManager.WakeLock? = null
    private var wifiLock: WifiManager.WifiLock? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        android.util.Log.d("ForegroundService", "[FGS] created")
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        startForeground(NOTIF_ID, buildNotification("流转已连接电脑，后台运行中"))
        acquireKeepAliveLocks()
        android.util.Log.d("ForegroundService", "[FGS] startCommand + startForeground success")
        return START_STICKY
    }

    /** 获取保活锁（Wi-Fi 锁 + CPU 锁），防止后台断连 */
    private fun acquireKeepAliveLocks() {
        try {
            val pm = getSystemService(Context.POWER_SERVICE) as PowerManager
            wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "Liuzhuan::WakeLock").apply {
                setReferenceCounted(false)
                acquire()
            }
            android.util.Log.d("ForegroundService", "wakeLock acquired")
        } catch (e: Exception) {
            android.util.Log.d("ForegroundService", "wakeLock failed: ${e.message}")
        }
        try {
            val wm = applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
            // FULL_HIGH_PERF：保持 Wi-Fi 高性能不省电休眠（防锁屏断连）即可；
            // LOW_LATENCY 会强制 radio 持续最低延迟（Wi-Fi 锁里最耗电的模式），
            // 对 30s 心跳/重连场景毫无必要——2026-08-28 功耗审查降级。
            wifiLock = wm.createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "Liuzhuan::WifiLock").apply {
                acquire()
            }
            android.util.Log.d("ForegroundService", "wifiLock acquired")
        } catch (e: Exception) {
            android.util.Log.d("ForegroundService", "wifiLock failed: ${e.message}")
        }
    }

    override fun onDestroy() {
        try { wakeLock?.release() } catch (_: Exception) {}
        try { wifiLock?.release() } catch (_: Exception) {}
        android.util.Log.d("ForegroundService", "locks released")
        super.onDestroy()
    }

    private fun buildNotification(content: String): Notification {
        val channelId = "liuzhuan_channel"
        if (Build.VERSION.SDK_INT >= 26) {
            val manager = getSystemService(NotificationManager::class.java)
            if (manager.getNotificationChannel(channelId) == null) {
                manager.createNotificationChannel(
                    NotificationChannel(channelId, "流转后台", NotificationManager.IMPORTANCE_LOW).apply {
                        description = "流转与电脑端的连接状态"
                    }
                )
            }
        }
        val pi = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )
        return Notification.Builder(this, channelId)
            .setContentTitle("⚡ 流转")
            .setContentText(content)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentIntent(pi)
            .setOngoing(true)
            .build()
    }

    companion object {
        const val NOTIF_ID = 1001
    }
}
