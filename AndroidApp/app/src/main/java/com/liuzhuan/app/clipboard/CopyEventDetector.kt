package com.liuzhuan.app.clipboard

import android.view.accessibility.AccessibilityEvent
import android.view.accessibility.AccessibilityNodeInfo

/**
 * 复制事件检测器 —— 强信号 / 弱信号分离的评分模型。
 *
 * 核心原则（避免 SystemUI/Launcher 假阳性，同时保留第三方 App 检测）：
 * - 强信号（HIGH）才触发完整 capture：
 *   1. 第三方 App 的 TYPE_VIEW_TEXT_SELECTION_CHANGED（文本选择变化，复制最可靠的信号）
 *   2. 明确「复制」按钮的 TYPE_VIEW_CLICKED
 * - 弱信号（LOW）不单独触发 capture：
 *   TYPE_WINDOW_CONTENT_CHANGED（SystemUI 状态栏 / Launcher 桌面 / 任意 UI 内容变化都会产生，
 *   不是复制证据）
 * - 系统 UI / Launcher 的弱信号直接 NONE（假阳性主来源）
 * - 系统 UI / Launcher 的强信号（如系统文本选择工具栏）降为 LOW 辅助，不主触发
 */
object CopyEventDetector {

    enum class Level { NONE, LOW, HIGH }

    data class Candidate(val level: Level, val reason: String, val preferSelection: Boolean)

    /** 复制按钮的常见文案（点击目标命中即视为 HIGH） */
    private val COPY_LABELS = listOf("复制", "拷贝", "copy")

    fun evaluate(
        event: AccessibilityEvent,
        category: AccessibilitySourcePolicy.AppCategory
    ): Candidate {
        return when (event.eventType) {
            AccessibilityEvent.TYPE_VIEW_TEXT_SELECTION_CHANGED -> {
                when (category) {
                    AccessibilitySourcePolicy.AppCategory.THIRD_PARTY ->
                        Candidate(Level.HIGH, "text_selection_changed", preferSelection = true)
                    else ->
                        // 系统 UI / Launcher 的 selection 变化（状态栏搜索框等）→ 弱辅助
                        Candidate(Level.LOW, "system_selection_changed", preferSelection = true)
                }
            }

            AccessibilityEvent.TYPE_VIEW_CLICKED -> {
                if (looksLikeCopyAction(event.source))
                    Candidate(Level.HIGH, "copy_action_clicked", preferSelection = true)
                else
                    Candidate(Level.NONE, "click_not_copy", preferSelection = false)
            }

            AccessibilityEvent.TYPE_WINDOW_CONTENT_CHANGED -> {
                when (category) {
                    AccessibilitySourcePolicy.AppCategory.THIRD_PARTY ->
                        Candidate(Level.LOW, "window_content_changed", preferSelection = true)
                    else ->
                        // SystemUI / Launcher 的高频弱事件 → 明确忽略，不再误判为复制
                        Candidate(Level.NONE, "weak_system_ui_event", preferSelection = false)
                }
            }

            else -> Candidate(Level.NONE, "uninteresting", preferSelection = false)
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
