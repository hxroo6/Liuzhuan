package android.view.accessibility

import android.os.SystemClock

class AccessibilityEvent(val eventType: Int, val packageName: CharSequence?, private val node: AccessibilityNodeInfo? = null,
                         val eventTime: Long = SystemClock.uptimeMillis(), val className: String = "test") {
    var sourceReads = 0
    val source: AccessibilityNodeInfo? get() { sourceReads++; return node }
    companion object {
        const val TYPE_VIEW_TEXT_SELECTION_CHANGED = 8192
        const val TYPE_VIEW_CLICKED = 1
        const val TYPE_WINDOW_CONTENT_CHANGED = 2048
    }
}
class AccessibilityNodeInfo(private val content: CharSequence?, val packageName: CharSequence? = "com.allowed.app") {
    var textReads = 0
    val text: CharSequence? get() { textReads++; return content }
    val contentDescription: CharSequence? = null
    val viewIdResourceName: String? = null
    val textSelectionStart = 0
    val textSelectionEnd = content?.length ?: 0
    val childCount = 0
    fun getChild(index: Int): AccessibilityNodeInfo? = null
    fun recycle() { }
}
