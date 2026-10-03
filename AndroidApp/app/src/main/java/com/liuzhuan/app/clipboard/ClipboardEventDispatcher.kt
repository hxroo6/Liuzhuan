package com.liuzhuan.app.clipboard

import android.util.Log

/**
 * 剪贴板事件分发器 —— 所有 ClipboardEvent 必须经过这里。
 *
 * 流程：capture → normalize → validate → filter → deduplicate → ActionPipeline
 *
 * 禁止无障碍服务/捕获层直接调用 WebSocket，统一收敛到 Dispatcher。
 */
object ClipboardEventDispatcher {

    fun dispatch(event: ClipboardEvent) {
        val id = event.diagnosticId
        // normalize
        val text = event.text?.trim() ?: return

        // validate
        if (text.length < 2) return

        // 在去重/发送/排队前应用自动同步隐私规则；显式手动动作保持可用。
        val normalized = ClipboardPrivacy.stampIfAllowed(event.copy(text = text))
        if (normalized == null) {
            val reason = ClipboardPrivacy.blockReason(event) ?: return
            ClipboardMonitorCoordinator.setDiagnostic("已忽略（${ClipboardPrivacy.describe(reason)}）")
            return
        }

        // deduplicate
        if (!ClipboardMonitorCoordinator.deduplicator.shouldProcess(normalized)) {
            Log.d(TAG, "[DISPATCH][$id] rejected reason=duplicate fp=${normalized.fingerprint.take(8)}")
            ClipboardMonitorCoordinator.setDiagnostic("去重跳过（短时间重复）")
            return
        }

        // 敏感信息不打日志：只打 source/type/length/fingerprint/pkg
        Log.d(
            TAG,
            "[DISPATCH][$id] accepted source=${normalized.source} type=${normalized.type} " +
                "len=${text.length} fp=${normalized.fingerprint.take(8)} pkg=${normalized.sourcePackage}"
        )
        ClipboardMonitorCoordinator.setDiagnostic("[$id] dispatcher 已接受，进入发送管线")

        // action pipeline
        ClipboardMonitorCoordinator.pipeline.process(normalized)
    }

    private const val TAG = "ClipDispatch"
}
