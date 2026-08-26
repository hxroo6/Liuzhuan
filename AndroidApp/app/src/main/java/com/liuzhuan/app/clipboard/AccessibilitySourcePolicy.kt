package com.liuzhuan.app.clipboard

import android.content.Context
import android.view.accessibility.AccessibilityEvent

/**
 * 无障碍事件来源策略 —— 复制检测的「第一优先级」前置分类与过滤。
 *
 * 统一管理「来源 App」判定，避免散落在 Service / Detector / CaptureManager 三处。
 *
 * 分类优先级：SELF → SYSTEM_UI → LAUNCHER → THIRD_PARTY / UNKNOWN
 * - SELF（自身 App）：永远忽略，阻断「PC→Android→自身 UI 事件→误判复制→回推 PC」回环
 * - SYSTEM_UI / LAUNCHER：其弱信号（如 WINDOW_CONTENT_CHANGED）是高发假阳性来源，
 *   但不做「固定包名黑名单」——用「系统组件 + 弱事件」组合规则来降权，强信号仍可参与。
 * - THIRD_PARTY：照常进入检测，不硬编码任何 App 白名单（保持对任意第三方 App 开放）。
 */
object AccessibilitySourcePolicy {

    enum class AppCategory { SELF, SYSTEM_UI, LAUNCHER, THIRD_PARTY, UNKNOWN }

    /** 分类来源 App（packageName 优先，回退 source.packageName） */
    fun classify(context: Context, event: AccessibilityEvent): AppCategory {
        val pkg = event.packageName?.toString()
            ?: event.source?.packageName?.toString()
        if (pkg.isNullOrBlank()) return AppCategory.UNKNOWN

        if (pkg == context.packageName) return AppCategory.SELF

        val lower = pkg.lowercase()
        return when {
            pkg == "com.android.systemui" || lower.contains("systemui") -> AppCategory.SYSTEM_UI
            lower.contains("launcher") -> AppCategory.LAUNCHER
            else -> AppCategory.THIRD_PARTY
        }
    }

    /** 是否值得进入复制检测主流程（自身 App / 来源未知 → 直接忽略） */
    fun shouldInspect(context: Context, event: AccessibilityEvent): Boolean {
        return when (classify(context, event)) {
            AppCategory.SELF, AppCategory.UNKNOWN -> false
            else -> true
        }
    }
}
