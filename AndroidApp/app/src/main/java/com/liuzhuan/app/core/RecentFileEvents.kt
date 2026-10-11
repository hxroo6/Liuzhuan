package com.liuzhuan.app.core

/** 有限短窗口去重，避免文件广播重复；不永久阻止用户再次复制。 */
class RecentFileEvents(private val clock: () -> Long = { System.nanoTime() / 1_000_000 }) {
    private val recent = LinkedHashMap<String, Long>()
    @Synchronized fun accept(key: String): Boolean {
        val now = clock()
        recent.entries.removeAll { now - it.value >= 2_000 }
        if (recent.containsKey(key)) return false
        recent[key] = now
        while (recent.size > 64) recent.remove(recent.keys.first())
        return true
    }
}
