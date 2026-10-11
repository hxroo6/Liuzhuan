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
import android.os.SystemClock
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.collectLatest

/**
 * 前台服务保活（M3）
 * 连接后启动：常驻通知 + START_STICKY，降低后台被杀概率，
 * 让剪贴板监控（无障碍）和 WS 连接在后台稳定运行。
 *
 * 保活锁：
 * 即时模式保留 CPU 保活基线；均衡模式只在实际工作时获取有期限的锁。
 * API 34+ HIGH_PERF 被映射为 LOW_LATENCY，Wi-Fi 锁不保证锁屏网络豁免。
 */
class ForegroundService : Service() {

    private var wakeLock: PowerManager.WakeLock? = null
    private var wifiLock: WifiManager.WifiLock? = null
    private val serviceScope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private var policyJob: Job? = null
    private var heldBalanced: Boolean? = null
    private var lastNotification = ""

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        LanController.init(applicationContext)
        android.util.Log.d("ForegroundService", "[FGS] created")
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (LanHub.client?.state == com.liuzhuan.app.net.LanClient.State.Idle ||
            LanHub.client?.state == com.liuzhuan.app.net.LanClient.State.Paused) {
            stopSelf()
            return START_NOT_STICKY
        }
        startForeground(NOTIF_ID, buildNotification("流转后台连接运行中"),
            android.content.pm.ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE)
        if (policyJob == null) policyJob = serviceScope.launch {
            combine(LanController.balanced, PowerSignals.activeUntil, TransferWork.active, LanController.state) {
                balanced, until, transfers, state -> Policy(balanced, until, transfers, state)
            }.collectLatest { policy ->
                val activeConnection = policy.state == com.liuzhuan.app.net.LanClient.State.Connected ||
                    policy.state == com.liuzhuan.app.net.LanClient.State.Connecting ||
                    policy.state == com.liuzhuan.app.net.LanClient.State.Disconnected
                val decision = com.liuzhuan.app.core.KeepAlivePolicy.decide(policy.balanced, activeConnection,
                    policy.transfers, policy.until, SystemClock.elapsedRealtime())
                if (decision.hold) {
                    if (heldBalanced != null && heldBalanced != policy.balanced) releaseLocks()
                    acquireKeepAliveLocks(policy.balanced)
                    heldBalanced = policy.balanced
                } else releaseLocks()
                val text = when {
                    !activeConnection -> "流转连接已暂停"
                    policy.balanced -> "流转 · 均衡省电，任务期间保持唤醒"
                    else -> "流转 · 即时同步，后台保持连接"
                }
                if (lastNotification != text) {
                    lastNotification = text
                    getSystemService(NotificationManager::class.java).notify(NOTIF_ID, buildNotification(text))
                }
                if (decision.releaseAfterMs != null) {
                    delay(decision.releaseAfterMs)
                    releaseLocks()
                }
            }
        }
        android.util.Log.d("ForegroundService", "[FGS] startCommand + startForeground success")
        return START_STICKY
    }

    /** 获取保活锁（Wi-Fi 锁 + CPU 锁），防止后台断连 */
    private fun acquireKeepAliveLocks(balanced: Boolean) {
        try {
            val pm = getSystemService(Context.POWER_SERVICE) as PowerManager
            if (wakeLock == null) wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "Liuzhuan::WakeLock").apply { setReferenceCounted(false) }
            if (balanced) wakeLock?.acquire(10 * 60_000L) else if (wakeLock?.isHeld != true) wakeLock?.acquire()
            android.util.Log.d("ForegroundService", "wakeLock acquired")
        } catch (e: Exception) {
            android.util.Log.d("ForegroundService", "wakeLock failed: ${e.message}")
        }
        try {
            val wm = applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
            if (wifiLock == null) wifiLock = wm.createWifiLock(WifiManager.WIFI_MODE_FULL_LOW_LATENCY, "Liuzhuan::WifiLock").apply { setReferenceCounted(false) }
            if (wifiLock?.isHeld != true) wifiLock?.acquire()
            android.util.Log.d("ForegroundService", "wifiLock acquired")
        } catch (e: Exception) {
            android.util.Log.d("ForegroundService", "wifiLock failed: ${e.message}")
        }
    }

    override fun onDestroy() {
        serviceScope.cancel()
        releaseLocks()
        wakeLock = null
        wifiLock = null
        android.util.Log.d("ForegroundService", "locks released")
        super.onDestroy()
    }

    private fun releaseLocks() {
        try { if (wakeLock?.isHeld == true) wakeLock?.release() } catch (_: Exception) {}
        try { if (wifiLock?.isHeld == true) wifiLock?.release() } catch (_: Exception) {}
        heldBalanced = null
    }

    override fun onTimeout(startId: Int, fgsType: Int) {
        serviceScope.cancel()
        LanHub.client?.pause()
        releaseLocks()
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    private data class Policy(val balanced: Boolean, val until: Long, val transfers: Int,
        val state: com.liuzhuan.app.net.LanClient.State)

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
