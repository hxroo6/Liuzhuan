package com.liuzhuan.app.clipboard

import android.content.ClipboardManager
import android.content.Context
import android.content.SharedPreferences
import com.liuzhuan.app.LanHub
import com.liuzhuan.app.core.SettingsStore
import com.liuzhuan.app.net.LanClient
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking

/**
 * 剪贴板统一推送器（无障碍服务 / App 前台 / 文本选择菜单 共用）
 *
 * ⚠️ Android 10+ 平台限制（AOSP ClipboardService 确认，无无障碍豁免）：
 * 后台读取剪贴板会被系统拒绝（primaryClip 返回 null），只有
 * 「默认输入法」或「有窗口焦点的应用」可读。因此：
 * - 无障碍事件/轮询触发时：仅当流转恰在前台（有焦点）才能读到内容
 * - MainActivity ON_RESUME 触发时：流转必在前台，读取必然成功
 * - ProcessTextActivity（文本选择菜单）：文字经 Intent 直达，不经过剪贴板
 *
 * 去重以 SharedPreferences("clip") 为唯一真源（多调用方共享，防止双发）。
 */
object ClipPusher {

    private const val DEBOUNCE_MS = 2_000L

    /** 后台自动发送开关（DataStore 有内存缓存，first() 立即返回） */
    private fun autoSendEnabled(context: Context): Boolean = try {
        val store = SettingsStore(context.applicationContext)
        runBlocking { store.settings.first().autoSendClipboard }
    } catch (e: Exception) {
        true
    }

    /**
     * 读取剪贴板，内容有变化才推送到电脑。
     *
     * @param trigger 诊断标签（event_xxx / poll / app_resume / service_connected）
     * @param force   true=用户显式动作（不受 autoSend 开关限制）
     * @return true=已成功推送；false=无新内容/未连接/读取被系统拒绝等
     */
    fun checkAndPush(context: Context, trigger: String, force: Boolean = false): Boolean {
        if (!force && !autoSendEnabled(context)) return false

        // 读取剪贴板（Android 10+：调用方需有焦点，否则 primaryClip 为 null）
        val cm = context.getSystemService(Context.CLIPBOARD_SERVICE) as? ClipboardManager ?: return false
        if (!cm.hasPrimaryClip()) return false
        val clip = cm.primaryClip ?: return false
        if (clip.itemCount == 0) return false

        val text = clip.getItemAt(0)?.coerceToText(context)?.toString()?.trim() ?: return false
        if (text.isEmpty() || text.length < 2) return false

        val prefs: SharedPreferences =
            context.getSharedPreferences("clip", Context.MODE_PRIVATE)

        // 去重：内容与上次成功推送相同 → 跳过（prefs 为唯一真源）
        val lastText = prefs.getString("last_text", "") ?: ""
        if (text == lastText) return false

        // 防抖：2s 内不重复推送
        val now = System.currentTimeMillis()
        val lastPush = prefs.getLong("last_push_time", 0L)
        if (now - lastPush < DEBOUNCE_MS) return false

        // 连接检查
        val client = LanHub.client ?: run {
            android.util.Log.d("ClipPusher", "[$trigger] SKIP: LanHub.client is null")
            return false
        }
        if (client.state !is LanClient.State.Connected) {
            android.util.Log.d("ClipPusher", "[$trigger] SKIP: client state = ${client.state}")
            return false
        }

        val ok = client.pushClipboard(text, context.packageName)
        android.util.Log.d("ClipPusher", "[$trigger] pushClipboard(${text.length} chars) = $ok")
        if (ok) {
            // commit() 同步写：缩小与无障碍服务读取的竞态窗口
            prefs.edit()
                .putString("last_text", text)
                .putLong("last_push_time", now)
                .commit()
        }
        return ok
    }
}
