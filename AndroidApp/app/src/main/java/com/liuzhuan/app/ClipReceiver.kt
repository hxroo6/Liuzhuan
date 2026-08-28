package com.liuzhuan.app

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import com.liuzhuan.app.clipboard.ClipboardCaptureSource
import com.liuzhuan.app.clipboard.ClipboardContentType
import com.liuzhuan.app.clipboard.ClipboardEvent
import com.liuzhuan.app.clipboard.ClipboardEventDispatcher
import com.liuzhuan.app.clipboard.fingerprintOf
import java.util.concurrent.atomic.AtomicLong

/**
 * LSPosed hook 广播接收器：接收第三方 App 复制（ClipHook 截获）的内容，
 * 构造 ClipboardEvent 复用现有 Dispatcher → ActionPipeline → PushToPcAction 链路
 * （normalize/validate/去重/队列/回环防护全继承）。
 *
 * 硬编码 action 字符串与 ClipHook 保持一致，避免引用 ClipHook 类导致
 * 标准版（无 LSPosed）加载 Xposed API 类报 ClassNotFound。
 */
class ClipReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != ACTION_CLIP) return
        // 握手 token 校验：非 ClipHook 发来的广播（伪造）直接丢弃
        if (intent.getStringExtra(EXTRA_TOKEN) != TOKEN) return
        val text = intent.getStringExtra(EXTRA_TEXT) ?: return
        val pkg = intent.getStringExtra(EXTRA_PKG)
        val ts = intent.getLongExtra(EXTRA_TS, System.currentTimeMillis())
        val id = "HOOK-${String.format("%06d", idCounter.incrementAndGet())}"
        android.util.Log.d(TAG, "[$id] received pkg=$pkg len=${text.length}")

        val event = ClipboardEvent(
            text = text,
            timestamp = ts,
            sourcePackage = pkg,
            source = ClipboardCaptureSource.XPOSED_HOOK,
            mimeTypes = listOf("text/plain"),
            fingerprint = fingerprintOf(text, listOf("text/plain")),
            type = ClipboardContentType.TEXT,
            diagnosticId = id
        )
        ClipboardEventDispatcher.dispatch(event)
        android.util.Log.d(TAG, "[$id] dispatched")
    }

    companion object {
        const val TAG = "ClipReceiver"
        const val ACTION_CLIP = "com.liuzhuan.app.ACTION_CLIPHOOK"
        const val EXTRA_TEXT = "text"
        const val EXTRA_PKG = "pkg"
        const val EXTRA_TS = "ts"
        const val EXTRA_TOKEN = "token"
        /** 握手 token：与 ClipHook 一致（硬编码，不引用 ClipHook 类避免标准版 ClassNotFound） */
        const val TOKEN = "liuzhuan-cliphook-2026-v1-9f3a7c2e"
        private val idCounter = AtomicLong(0)
    }
}
