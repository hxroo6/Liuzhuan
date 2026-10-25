package com.liuzhuan.app.clipboard

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/** 自动同步的纯逻辑策略；不读取剪贴板、Context 或网络。 */
class ClipboardPrivacyPolicy(
    private val beforeChange: (State) -> Unit = {},
    private val now: () -> Long = System::currentTimeMillis
) {
    data class State(
        val ready: Boolean = false,
        val automaticEnabled: Boolean = false,
        /** 0=未暂停，-1=暂停至手动恢复，正值=到期时间。 */
        val pausedUntilMs: Long = 0,
        val excludedPackages: Set<String> = emptySet(),
        val discardBeforeMs: Long = 0,
        /** 运行期版本，使已捕获但尚未发送的旧事件失效。 */
        val revision: Long = 0
    ) {
        fun paused(at: Long): Boolean = pausedUntilMs == -1L || pausedUntilMs > at
    }

    enum class BlockReason { NOT_READY, DISABLED, PAUSED, EXCLUDED_SOURCE, UNKNOWN_SOURCE, INVALIDATED, OLD_COPY }
    private val _state = MutableStateFlow(State())
    val state: StateFlow<State> = _state.asStateFlow()

    @Synchronized
    fun initialize(loaded: State) {
        _state.value = loaded.copy(ready = true, excludedPackages = loaded.excludedPackages.toSet())
    }

    @Synchronized
    fun setAutomaticEnabled(enabled: Boolean, invalidatePending: Boolean = true): Boolean {
        val old = _state.value
        if (old.automaticEnabled == enabled) return false
        publish(old.copy(automaticEnabled = enabled,
            discardBeforeMs = if (invalidatePending) maxOf(old.discardBeforeMs, now()) else old.discardBeforeMs,
            revision = old.revision + 1))
        return true
    }

    @Synchronized
    fun pause(durationMs: Long? = null) {
        require(durationMs == null || durationMs > 0) { "暂停时长必须大于零" }
        val at = now()
        val deadline = if (durationMs == null) -1L else Math.addExact(at, durationMs)
        val old = _state.value
        publish(old.copy(pausedUntilMs = deadline, discardBeforeMs = maxOf(old.discardBeforeMs, at), revision = old.revision + 1))
    }

    @Synchronized
    fun resume() {
        val old = _state.value
        publish(old.copy(pausedUntilMs = 0, discardBeforeMs = maxOf(old.discardBeforeMs, now()), revision = old.revision + 1))
    }

    @Synchronized
    fun replaceExcludedPackages(raw: String, forcePersist: Boolean = false): Boolean {
        val parsed = parsePackages(raw)
        val old = _state.value
        if (old.excludedPackages == parsed && !forcePersist) return false
        publish(old.copy(excludedPackages = parsed, discardBeforeMs = maxOf(old.discardBeforeMs, now()), revision = old.revision + 1))
        return true
    }

    /** 只处理到期元数据，不加轮询；到期之后已捕获的新事件仍有效。 */
    @Synchronized
    fun refreshExpiredPause(): Boolean {
        val old = _state.value
        if (old.pausedUntilMs <= 0 || now() < old.pausedUntilMs) return false
        publish(old.copy(pausedUntilMs = 0, discardBeforeMs = maxOf(old.discardBeforeMs, old.pausedUntilMs)))
        return true
    }

    /** 首次迁移也要写 schema marker，即使旧开关刚好与默认值相同。 */
    @Synchronized
    fun migrateAutomaticEnabled(enabled: Boolean) {
        val old = _state.value
        publish(old.copy(automaticEnabled = enabled, revision = old.revision + 1))
    }

    private fun publish(candidate: State) {
        // 与 runIfAllowed 同一策略锁：落盘或回滚期间不能观察尚未确认的候选规则。
        beforeChange(candidate)
        _state.value = candidate
    }

    @Synchronized
    fun blockReason(event: ClipboardEvent): BlockReason? {
        if (event.source == ClipboardCaptureSource.MANUAL) return null
        val s = _state.value
        if (!s.ready) return BlockReason.NOT_READY
        if (!s.automaticEnabled) return BlockReason.DISABLED
        if (s.paused(now())) return BlockReason.PAUSED
        if (event.privacyRevision != null && event.privacyRevision != s.revision) return BlockReason.INVALIDATED
        val source = event.sourcePackage?.takeIf { it.isNotBlank() }
        if (source != null && source in s.excludedPackages) return BlockReason.EXCLUDED_SOURCE
        // 剪贴板读取不能可靠获得写入 App；排除规则启用后，未知来源不自动转发。
        if (source == null && s.excludedPackages.isNotEmpty()) return BlockReason.UNKNOWN_SOURCE
        val cutoff = maxOf(s.discardBeforeMs, s.pausedUntilMs.takeIf { it > 0 } ?: 0)
        if (cutoff > 0 && event.timestamp <= cutoff) return BlockReason.OLD_COPY
        return null
    }

    @Synchronized
    fun stampIfAllowed(event: ClipboardEvent): ClipboardEvent? {
        if (blockReason(event) != null) return null
        return event.copy(privacyRevision = _state.value.revision)
    }

    /** 判断和开始发送/入队在同一锁内，暂停生效后不会再提交旧自动事件。 */
    @Synchronized
    fun runIfAllowed(event: ClipboardEvent, action: () -> Unit): Boolean {
        if (blockReason(event) != null) return false
        action()
        return true
    }

    @Synchronized
    fun captureRevision(sourcePackage: String?): Long? {
        val s = _state.value
        if (!s.ready || !s.automaticEnabled || s.paused(now())) return null
        if (sourcePackage.isNullOrBlank() && s.excludedPackages.isNotEmpty()) return null
        if (sourcePackage in s.excludedPackages) return null
        return s.revision
    }

    companion object {
        const val MAX_EXCLUDED_PACKAGES = 100
        private val packagePattern = Regex("[A-Za-z][A-Za-z0-9_]*(?:\\.[A-Za-z][A-Za-z0-9_]*)+")
        fun parsePackages(raw: String): Set<String> {
            require(raw.length <= 32_000) { "排除规则过长" }
            val packages = raw.split(Regex("[\\s,，;；]+")).filter { it.isNotEmpty() }.toSet()
            require(packages.size <= MAX_EXCLUDED_PACKAGES) { "最多排除 $MAX_EXCLUDED_PACKAGES 个应用" }
            val invalid = packages.firstOrNull { it.length > 255 || !packagePattern.matches(it) }
            require(invalid == null) { "包名格式无效：${invalid?.take(80)}。请使用完整包名，如 com.tencent.mm" }
            return packages
        }
    }
}
