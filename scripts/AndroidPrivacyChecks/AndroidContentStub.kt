package android.content

open class Context {
    open val applicationContext: Context get() = this
    val packageName = "com.liuzhuan.app"
    val applicationInfo = ApplicationInfo()
    val preferenceFiles = mutableMapOf<String, MemoryPreferences>()
    var clipboard = ClipboardManager()
    var systemServiceReads = 0
    fun getSharedPreferences(name: String, mode: Int): MemoryPreferences = preferenceFiles.getOrPut(name) { MemoryPreferences() }
    fun getSystemService(name: String): Any? { systemServiceReads++; return if (name == CLIPBOARD_SERVICE) clipboard else null }
    companion object { const val MODE_PRIVATE = 0; const val CLIPBOARD_SERVICE = "clipboard" }
}

class ApplicationInfo {
    val dataDir = "F:/appdata/Temp/liuzhuan-privacy-fixtures/" + java.util.UUID.randomUUID().toString()
}

class MemoryPreferences {
    val values = mutableMapOf<String, Any>()
    val persistedValues = mutableMapOf<String, Any>()
    var failSaves = false
    var commitCount = 0
    val all: Map<String, Any> get() = values.toMap()
    fun contains(key: String) = values.containsKey(key)
    fun getString(key: String, default: String?) = values[key] as String? ?: default
    fun getBoolean(key: String, default: Boolean) = values[key] as Boolean? ?: default
    fun getLong(key: String, default: Long) = values[key] as Long? ?: default
    fun getInt(key: String, default: Int) = values[key] as Int? ?: default
    fun edit() = Editor(this)
    class Editor(private val target: MemoryPreferences) {
        private val changes = mutableMapOf<String, Any>()
        private var cleared = false
        fun clear() = apply { cleared = true; changes.clear() }
        fun putString(key: String, value: String) = apply { changes[key] = value }
        fun putBoolean(key: String, value: Boolean) = apply { changes[key] = value }
        fun putLong(key: String, value: Long) = apply { changes[key] = value }
        fun putInt(key: String, value: Int) = apply { changes[key] = value }
        fun putFloat(key: String, value: Float) = apply { changes[key] = value }
        fun putStringSet(key: String, value: Set<String>) = apply { changes[key] = value }
        fun apply() { if (cleared) target.values.clear(); target.values.putAll(changes) }
        fun commit(): Boolean {
            // Android commit 可以先变更缓存，之后报告磁盘写入失败。
            apply(); target.commitCount++
            if (target.failSaves) return false
            target.persistedValues.clear(); target.persistedValues.putAll(target.values)
            return true
        }
    }
}

abstract class BroadcastReceiver { abstract fun onReceive(context: Context, intent: Intent) }
class Intent(var action: String? = null) {
    private val extras = mutableMapOf<String, Any>()
    val reads = mutableMapOf<String, Int>()
    fun putExtra(key: String, value: Any) = apply { extras[key] = value }
    fun getStringExtra(key: String): String? { reads[key] = (reads[key] ?: 0) + 1; return extras[key] as String? }
    fun getLongExtra(key: String, default: Long) = extras[key] as Long? ?: default
    fun getIntExtra(key: String, default: Int) = extras[key] as Int? ?: default
}

class ClipDescription(var timestamp: Long)
class ClipData(val items: List<Item>, val description: ClipDescription) {
    val itemCount get() = items.size
    fun getItemAt(index: Int): Item? = items.getOrNull(index)
    class Item(val text: String, val uri: android.net.Uri? = null, val htmlText: String? = null) {
        var textReads = 0
        fun coerceToText(context: Context): CharSequence { textReads++; return text }
    }
}
class ClipboardManager {
    var clip: ClipData? = null
    var reads = 0
    fun hasPrimaryClip(): Boolean { reads++; return clip != null }
    val primaryClip: ClipData? get() { reads++; return clip }
}
