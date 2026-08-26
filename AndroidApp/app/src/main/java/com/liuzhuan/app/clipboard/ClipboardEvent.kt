package com.liuzhuan.app.clipboard

import java.security.MessageDigest

/**
 * 剪贴板捕获来源
 *
 * 本次实现的三种真实来源 + 手动来源；IME/ROOT/SHIZUKU 仅预留，不实现。
 */
enum class ClipboardCaptureSource {
    /** 通过 ClipboardManager.getPrimaryClip() 读到（仅当流转 App 有焦点/前台时成功） */
    ACCESSIBILITY_CLIPBOARD,

    /** 通过 AccessibilityNodeInfo 的 textSelection 读到（不依赖焦点，部分 App 可用） */
    ACCESSIBILITY_SELECTION,

    /** 疑似「复制」动作后的捕获（点击复制按钮 / 文本选择变化触发） */
    ACCESSIBILITY_COPY_ACTION,

    /** 手动动作：文本选择菜单 / 分享 / 立即测试按钮 */
    MANUAL
    // 预留：IME, ROOT, SHIZUKU（本次不实现）
}

/**
 * 剪贴板内容类型（第一阶段 PC 同步仅处理 TEXT，但模型需保留类型认知）
 */
enum class ClipboardContentType {
    TEXT, URI, IMAGE, OTHER
}

/**
 * 统一剪贴板事件模型。
 *
 * 所有捕获路径最终都必须产出 [ClipboardEvent]，经 Dispatcher 统一分发，
 * 不允许无障碍服务/捕获层直接调用 WebSocket。
 */
data class ClipboardEvent(
    val text: String?,
    val timestamp: Long,
    val sourcePackage: String?,
    val source: ClipboardCaptureSource,
    val mimeTypes: List<String>,
    val fingerprint: String,
    val type: ClipboardContentType = ClipboardContentType.TEXT,
    val diagnosticId: String = "" // 端到端链路 ID（COPY-xxxxxx），贯穿 capture→dispatch→action→ws
)

/**
 * 轻量稳定指纹：SHA-256(normalizedText + mimeTypes)，用于去重与循环回写防护。
 * 不参与日志输出（日志只打印前 8 位）。
 */
fun fingerprintOf(text: String, mimeTypes: List<String>): String {
    val raw = text + "|" + mimeTypes.sorted().joinToString(",")
    val digest = MessageDigest.getInstance("SHA-256").digest(raw.toByteArray())
    return digest.joinToString("") { "%02x".format(it) }
}
