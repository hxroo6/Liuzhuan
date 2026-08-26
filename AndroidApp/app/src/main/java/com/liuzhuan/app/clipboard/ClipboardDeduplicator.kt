package com.liuzhuan.app.clipboard

/**
 * 剪贴板事件去重器（fingerprint 维度 + 时间窗口）
 *
 * 策略：IGNORE_IMMEDIATE_DUPLICATE —— 短时间内相同 fingerprint 只处理一次，
 * 超过时间窗口后允许「相同内容重复复制」再次触发（用户可能故意复制同一段文字两次）。
 *
 * 使用 LinkedHashMap(accessOrder=true) 做轻量 LRU，容量受限，不无限增长。
 * 同时承担「循环回写防护」：本 App 写入剪贴板（如接收 PC 内容）时调用 [markLocal]，
 * 把该内容的 fingerprint 记为已处理，后续捕获到相同内容会被跳过，避免 Android↔PC 无限回写。
 */
class ClipboardDeduplicator(
    private val capacity: Int = 64,
    private val duplicateWindowMs: Long = 2_000L
) {
    private val recent = object : LinkedHashMap<String, Long>(capacity, 0.75f, true) {
        override fun removeEldestEntry(eldest: MutableMap.MutableEntry<String, Long>?): Boolean {
            return size > capacity
        }
    }

    /** 判断事件是否应处理；返回 true 表示通过（非重复） */
    @Synchronized
    fun shouldProcess(event: ClipboardEvent): Boolean {
        val last = recent[event.fingerprint]
        val now = event.timestamp
        if (last != null && now - last < duplicateWindowMs) return false
        recent[event.fingerprint] = now
        return true
    }

    /** 标记本 App 本地写入的内容为「已处理」，用于抑制循环回写 */
    @Synchronized
    fun markLocal(text: String, mimeTypes: List<String> = emptyList()) {
        recent[fingerprintOf(text, mimeTypes)] = System.currentTimeMillis()
    }
}
