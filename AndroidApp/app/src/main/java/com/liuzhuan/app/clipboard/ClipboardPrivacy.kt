package com.liuzhuan.app.clipboard

import android.content.Context
import com.liuzhuan.app.LanHub
import java.io.File
import java.io.IOException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/** 小型同步偏好适配器：服务/广播冷启动先读规则，再允许自动事件。 */
internal class ClipboardPrivacyPreferences(context: Context) {
    // SharedPreferences 会把损坏 XML 读成空记录；必须在读取前区分首次安装与已有损坏配置。
    private val hadPersistedFile = File(context.applicationInfo.dataDir, "shared_prefs/clipboard_privacy.xml").let {
        it.exists() || File(it.path + ".bak").exists()
    }
    private val preferences = context.getSharedPreferences("clipboard_privacy", Context.MODE_PRIVATE)
    private val legacyEligible = !hadPersistedFile && preferences.all.isEmpty()
    val needsLegacyMigration: Boolean get() = legacyEligible && !preferences.contains("schema")
    fun exclusionText(): String = preferences.getString("excluded_packages", "") ?: ""
    fun load(): ClipboardPrivacyPolicy.State {
        if (needsLegacyMigration) return ClipboardPrivacyPolicy.State()
        require(preferences.getInt("schema", 0) == 1 &&
            listOf("automatic_enabled", "paused_until", "discard_before", "excluded_packages").all(preferences::contains)) {
            "隐私配置缺少完整标记或字段"
        }
        val paused = preferences.getLong("paused_until", 0)
        val cutoff = preferences.getLong("discard_before", 0)
        require(paused >= -1 && cutoff >= 0) { "隐私配置中的时间无效" }
        return ClipboardPrivacyPolicy.State(automaticEnabled = preferences.getBoolean("automatic_enabled", false),
            pausedUntilMs = paused, discardBeforeMs = cutoff,revision=preferences.getLong("revision",0),
            excludedPackages = ClipboardPrivacyPolicy.parsePackages(exclusionText()))
    }
    @Synchronized
    fun save(state: ClipboardPrivacyPolicy.State) {
        val previous = preferences.all.toMap()
        val editor = preferences.edit().putInt("schema", 1).putBoolean("automatic_enabled", state.automaticEnabled)
            .putLong("revision",state.revision).putLong("paused_until", state.pausedUntilMs).putLong("discard_before", state.discardBeforeMs)
            .putString("excluded_packages", state.excludedPackages.joinToString("\n"))
        try {
            if (editor.commit()) return
        } catch (_: Exception) { /* 同样恢复 SharedPreferences 提前更新的内存缓存。 */ }
        val restore = preferences.edit().clear()
        previous.forEach { (key, value) ->
            when (value) {
                is Boolean -> restore.putBoolean(key, value)
                is Long -> restore.putLong(key, value)
                is Int -> restore.putInt(key, value)
                is Float -> restore.putFloat(key, value)
                is String -> restore.putString(key, value)
                is Set<*> -> restore.putStringSet(key, value.filterIsInstance<String>().toSet())
            }
        }
        // commit 失败也可能已经更新缓存；恢复尝试的返回值不影响保持旧策略的结论。
        runCatching { restore.commit() }
        throw IOException("隐私设置未能保存，原有保护仍然生效，请重试")
    }
}

object ClipboardPrivacy {
    private lateinit var preferences: ClipboardPrivacyPreferences
    private val policy = ClipboardPrivacyPolicy(beforeChange = { candidate -> preferences.save(candidate) })
    val state = policy.state
    private val _loadError = MutableStateFlow("")
    val loadError: StateFlow<String> = _loadError.asStateFlow()
    @Volatile private var initialized = false
    val revision: Long get() = state.value.revision

    /** Context 访问仅在 onCreate/init/onReceive 后，保留 Coordinator 原有幂等装配。 */
    fun init(context: Context) {
        if (initialized) return
        synchronized(this) {
            if (initialized) return
            preferences = ClipboardPrivacyPreferences(context.applicationContext)
            try { policy.initialize(preferences.load()) }
            catch (_: Exception) {
                policy.initialize(ClipboardPrivacyPolicy.State(pausedUntilMs = -1))
                _loadError.value = "隐私配置未能读取，自动同步已暂停。请检查并保存排除规则后恢复。"
            }
            LanHub.autoSendClipboard = state.value.automaticEnabled
            initialized = true
        }
    }

    @Synchronized
    fun setAutomaticEnabled(enabled: Boolean, invalidatePending: Boolean = true) {
        requireReadable()
        if (policy.setAutomaticEnabled(enabled, invalidatePending)) discardAutomaticPending()
        LanHub.autoSendClipboard = state.value.automaticEnabled
    }

    /** 旧连接配置只允许首次迁移；schema marker 写入后，隐私偏好是唯一权威。 */
    @Synchronized
    fun migrateLegacyAutomaticEnabled(enabled: Boolean) {
        if (!initialized || _loadError.value.isNotEmpty() || !preferences.needsLegacyMigration) return
        try {
            policy.migrateAutomaticEnabled(enabled)
            LanHub.autoSendClipboard = state.value.automaticEnabled
            discardAutomaticPending()
        } catch (_: Exception) {
            _loadError.value = "旧隐私设置迁移未能保存，自动同步保持关闭。请检查并保存排除规则后恢复。"
        }
    }

    @Synchronized
    fun pause(durationMs: Long? = null) { requireReadable(); policy.pause(durationMs); discardAutomaticPending() }

    @Synchronized
    fun resume() {
        requireReadable()
        policy.resume()
        discardAutomaticPending()
    }

    @Synchronized
    fun saveExcludedPackages(raw: String) {
        val recovering = _loadError.value.isNotEmpty()
        // 即使修正为空规则，也要确认完整记录落盘，之后才解除读取错误。
        if (policy.replaceExcludedPackages(raw, forcePersist = recovering || preferences.needsLegacyMigration)) {
            _loadError.value = ""
            discardAutomaticPending()
        }
    }

    @Synchronized
    fun refreshExpiredPause() {
        requireReadable()
        policy.refreshExpiredPause()
    }

    fun exclusionText(): String = try { preferences.exclusionText() } catch (_: Exception) { "" }
    fun captureRevision(sourcePackage: String?): Long? = policy.captureRevision(sourcePackage)
    fun blockReason(event: ClipboardEvent) = policy.blockReason(event)
    fun stampIfAllowed(event: ClipboardEvent) = policy.stampIfAllowed(event)
    fun runIfAllowed(event: ClipboardEvent, action: () -> Unit) = policy.runIfAllowed(event, action)

    private fun requireReadable() {
        check(initialized && _loadError.value.isEmpty()) { "请先检查并保存排除规则，再恢复自动同步" }
    }

    private fun discardAutomaticPending() {
        ClipboardMonitorCoordinator.queue.discardAutomatic()
        com.liuzhuan.app.DurableTransfers.invalidateAutomatic()
    }

    fun describe(reason: ClipboardPrivacyPolicy.BlockReason): String = when (reason) {
        ClipboardPrivacyPolicy.BlockReason.NOT_READY -> "隐私规则未就绪"
        ClipboardPrivacyPolicy.BlockReason.DISABLED -> "自动同步已关闭"
        ClipboardPrivacyPolicy.BlockReason.PAUSED -> "自动同步已暂停"
        ClipboardPrivacyPolicy.BlockReason.EXCLUDED_SOURCE -> "来源应用已排除"
        ClipboardPrivacyPolicy.BlockReason.UNKNOWN_SOURCE -> "来源无法确认，已按排除规则跳过"
        ClipboardPrivacyPolicy.BlockReason.INVALIDATED -> "隐私规则已变化，旧自动事件已丢弃"
        ClipboardPrivacyPolicy.BlockReason.OLD_COPY -> "只同步恢复后新复制的内容"
    }
}
