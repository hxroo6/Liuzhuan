package com.liuzhuan.app.clipboard

import android.view.accessibility.AccessibilityEvent
import android.view.accessibility.AccessibilityNodeInfo

/**
 * 复制事件检测器
 *
 * 目标：从海量无障碍事件中筛出「高度疑似发生复制」的事件，降低无意义剪贴板读取。
 * 不要求 100% 准确 —— 宁可漏捕，也不要每个事件都去读剪贴板（高耗电 + 隐私读取）。
 *
 * 判定策略：
 * - HIGH   = 文本选择变化（用户选中文本，复制的前兆）/ 点击了「复制」按钮 → 短延迟后双策略捕获
 * - MEDIUM = 窗口内容变化（可能出现复制菜单）→ 仅做 selection 捕获（不读剪贴板）
 * - 其余事件 → 不捕获（返回 null）
 */
object CopyEventDetector {

    enum class Confidence { HIGH, MEDIUM, NONE }

    data class Hint(val confidence: Confidence, val preferSelection: Boolean)

    /** 复制按钮的常见文案（点击目标命中即视为 HIGH） */
    private val COPY_LABELS = listOf("复制", "拷贝", "copy")

    fun shouldCapture(event: AccessibilityEvent): Hint? {
        return when (event.eventType) {
            AccessibilityEvent.TYPE_VIEW_TEXT_SELECTION_CHANGED ->
                Hint(Confidence.HIGH, preferSelection = true)

            AccessibilityEvent.TYPE_VIEW_CLICKED ->
                if (looksLikeCopyAction(event.source))
                    Hint(Confidence.HIGH, preferSelection = true)
                else null

            AccessibilityEvent.TYPE_WINDOW_CONTENT_CHANGED ->
                Hint(Confidence.MEDIUM, preferSelection = true)

            else -> null
        }
    }

    /** 点击目标是否疑似「复制」按钮（text / contentDescription / viewId 含复制关键词） */
    private fun looksLikeCopyAction(node: AccessibilityNodeInfo?): Boolean {
        node ?: return false
        val candidates = listOfNotNull(
            node.text?.toString(),
            node.contentDescription?.toString(),
            node.viewIdResourceName
        )
        return candidates.any { text ->
            COPY_LABELS.any { label -> text.contains(label, ignoreCase = true) }
        }
    }
}
