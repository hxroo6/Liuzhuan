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
 * LSPosed hook 广播接收器：接收第三方 App 复制（ClipHook 截获）的内容。
 *
 * 两类：
 * - 文本（kind=text）：构造 ClipboardEvent 复用 Dispatcher→PushToPcAction（去重/队列/回环防护全继承）
 * - 文件/图片（kind=file）：解码 base64 → 直接走 LanClient.pushFile（WS file_push）
 *
 * 硬编码 action/token 与 ClipHook 保持一致，避免引用 ClipHook 类导致
 * 无 LSPosed 环境加载 Xposed API 类报 ClassNotFound。
 */
class ClipReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != ACTION_CLIP) return
        // 握手 token 校验：非 ClipHook 发来的广播（伪造）直接丢弃
        if (intent.getStringExtra(EXTRA_TOKEN) != TOKEN) return
        val pkg = intent.getStringExtra(EXTRA_PKG)
        val kind = intent.getStringExtra(EXTRA_KIND) ?: KIND_TEXT

        if (kind == KIND_FILE) {
            handleFile(intent, pkg)
        } else {
            handleText(intent, pkg)
        }
    }

    private fun handleText(intent: Intent, pkg: String?) {
        val text = intent.getStringExtra(EXTRA_TEXT) ?: return
        val ts = intent.getLongExtra(EXTRA_TS, System.currentTimeMillis())
        val id = "HOOK-${String.format("%06d", idCounter.incrementAndGet())}"
        android.util.Log.d(TAG, "[$id] text received pkg=$pkg len=${text.length}")

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
        android.util.Log.d(TAG, "[$id] text dispatched")
    }

    private fun handleFile(intent: Intent, pkg: String?) {
        val b64 = intent.getStringExtra(EXTRA_FILE_B64) ?: return
        val mime = intent.getStringExtra(EXTRA_FILE_MIME) ?: "application/octet-stream"
        val size = intent.getIntExtra(EXTRA_FILE_SIZE, 0)
        val id = "HOOK-${String.format("%06d", idCounter.incrementAndGet())}"
        android.util.Log.d(TAG, "[$id] file received pkg=$pkg mime=$mime size=$size")
        try {
            val bytes = android.util.Base64.decode(b64, android.util.Base64.DEFAULT)
            val name = fileBaseName(mime, System.currentTimeMillis())
            LanHub.client?.pushFile(bytes, name, mime)
            android.util.Log.d(TAG, "[$id] file upload queued via HTTP")
        } catch (t: Throwable) {
            android.util.Log.d(TAG, "[$id] file decode/upload error ${t.javaClass.simpleName}")
        }
    }

    /** 从 mime 推断文件扩展名（供上传命名） */
    private fun fileBaseName(mime: String, ts: Long): String {
        val ext = when (mime.substringBefore(';').trim()) {
            "image/png" -> ".png"
            "image/jpeg" -> ".jpg"
            "image/gif" -> ".gif"
            "image/webp" -> ".webp"
            "image/bmp" -> ".bmp"
            "image/svg+xml" -> ".svg"
            "video/mp4" -> ".mp4"
            "audio/mpeg" -> ".mp3"
            "audio/mp4" -> ".m4a"
            "text/plain" -> ".txt"
            "application/pdf" -> ".pdf"
            else -> ".bin"
        }
        return "clip_${ts}$ext"
    }

    companion object {
        const val TAG = "ClipReceiver"
        const val ACTION_CLIP = "com.liuzhuan.app.ACTION_CLIPHOOK"
        const val EXTRA_KIND = "kind"
        const val KIND_TEXT = "text"
        const val KIND_FILE = "file"
        const val EXTRA_TEXT = "text"
        const val EXTRA_FILE_B64 = "file_b64"
        const val EXTRA_FILE_MIME = "file_mime"
        const val EXTRA_FILE_SIZE = "file_size"
        const val EXTRA_PKG = "pkg"
        const val EXTRA_TS = "ts"
        const val EXTRA_TOKEN = "token"
        /** 握手 token：与 ClipHook 一致（硬编码，不引用 ClipHook 类避免标准版 ClassNotFound） */
        const val TOKEN = "liuzhuan-cliphook-2026-v1-9f3a7c2e"
        private val idCounter = AtomicLong(0)
    }
}
