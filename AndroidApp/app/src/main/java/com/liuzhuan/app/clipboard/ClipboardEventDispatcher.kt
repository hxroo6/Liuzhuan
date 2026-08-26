package com.liuzhuan.app.clipboard

import android.util.Log
import com.liuzhuan.app.LanHub

/**
 * 剪贴板事件分发器 —— 所有 ClipboardEvent 必须经过这里。
 *
 * 流程：capture → normalize → validate → filter → deduplicate → ActionPipeline
 *
 * 禁止无障碍服务/捕获层直接调用 WebSocket，统一收敛到 Dispatcher。
 */
object ClipboardEventDispatcher {

    fun dispatch(event: ClipboardEvent) {
        // normalize
        val text = event.text?.trim() ?: return

        // validate
        if (text.length < 2) return

        // filter：后台自动发送开关（@Volatile，MainActivity 实时同步）
        if (!LanHub.autoSendClipboard) return

        val normalized = event.copy(text = text)

        // deduplicate
        if (!ClipboardMonitorCoordinator.deduplicator.shouldProcess(normalized)) {
            Log.d(TAG, "去重跳过 fp=${normalized.fingerprint.take(8)}")
            return
        }

        // 敏感信息不打日志：只打 source/type/length/fingerprint/pkg
        Log.d(
            TAG,
            "source=${normalized.source} type=${normalized.type} len=${text.length} " +
                "fp=${normalized.fingerprint.take(8)} pkg=${normalized.sourcePackage}"
        )

        // action pipeline
        ClipboardMonitorCoordinator.pipeline.process(normalized)
    }

    private const val TAG = "ClipDispatch"
}
