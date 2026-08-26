package com.liuzhuan.app.clipboard

import android.content.Context
import android.view.accessibility.AccessibilityEvent

/**
 * 无障碍事件来源策略 —— 复制检测的「第一优先级」前置过滤。
 *
 * 统一管理「来源 App 校验」，避免散落在 Service / Detector / CaptureManager 三处。
 *
 * 关键原则：
 * - 自身 App（com.liuzhuan.app）的事件（接收页列表刷新 / 输入框 / 设置 / 测试按钮等）
 *   永不进入复制检测，从源头阻断「PC→Android→自身 UI 事件→误判复制→回推 PC」的回环。
 * - 第三方 App 的事件照常放行，不硬编码任何 App 白名单（不限制监听范围）。
 * - packageName 为空时回退到 event.source.packageName；都为 null 则拒绝（不把 null 当复制）。
 */
object AccessibilitySourcePolicy {

    /**
     * 判断该事件是否值得进入复制检测主流程。
     * @return true=来自第三方 App，可继续检测；false=自身 App / 来源为空，直接忽略。
     */
    fun shouldInspect(context: Context, event: AccessibilityEvent): Boolean {
        val pkg = event.packageName?.toString()
            ?: event.source?.packageName?.toString()

        if (pkg.isNullOrBlank()) {
            return false // 来源未知 → 不当作复制候选
        }
        if (pkg == context.packageName) {
            return false // 自身 App → 忽略
        }
        return true
    }
}
