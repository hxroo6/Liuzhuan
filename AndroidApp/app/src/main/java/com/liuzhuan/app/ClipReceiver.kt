package com.liuzhuan.app

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import com.liuzhuan.app.clipboard.ClipboardCaptureSource
import com.liuzhuan.app.clipboard.ClipboardContentType
import com.liuzhuan.app.clipboard.ClipboardEvent
import com.liuzhuan.app.clipboard.ClipboardEventDispatcher
import com.liuzhuan.app.clipboard.ClipboardPrivacy
import com.liuzhuan.app.clipboard.fingerprintOf
import java.util.concurrent.atomic.AtomicLong
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import java.util.concurrent.atomic.AtomicInteger
import java.security.MessageDigest
import com.liuzhuan.app.core.hexString

/**
 * LSPosed hook 广播接收器：接收第三方 App 复制（ClipHook 截获）的内容。
 *
 * 两类：
 * - 文本（kind=text）：构造 ClipboardEvent 复用 Dispatcher→PushToPcAction（去重/队列/回环防护全继承）
 * - 文件/图片（kind=file）：字节（兼容旧 Base64）→ 有界异步 / 去重 / 缓存 → HTTP 上传
 *
 * 硬编码 action/token 与 ClipHook 保持一致，避免引用 ClipHook 类导致
 * 无 LSPosed 环境加载 Xposed API 类报 ClassNotFound。
 */
class ClipReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != ACTION_CLIP) return
        // 握手 token 校验：非 ClipHook 发来的广播（伪造）直接丢弃
        if (intent.getStringExtra(EXTRA_TOKEN) != TOKEN) return
        ClipboardPrivacy.init(context.applicationContext)
        val pkg = intent.getStringExtra(EXTRA_PKG)
        val metadata = ClipboardEvent(null,
            intent.getLongExtra(EXTRA_TS, System.currentTimeMillis()), pkg,
            ClipboardCaptureSource.XPOSED_HOOK, emptyList(), "",
            privacyRevision = ClipboardPrivacy.revision)
        val blocked = ClipboardPrivacy.blockReason(metadata)
        if (blocked != null) {
            android.util.Log.d(TAG, "hook ignored pkg=$pkg reason=$blocked")
            com.liuzhuan.app.clipboard.ClipboardMonitorCoordinator.setDiagnostic("已忽略（${ClipboardPrivacy.describe(blocked)}）")
            return
        }
        val kind = intent.getStringExtra(EXTRA_KIND) ?: KIND_TEXT

        if (kind == KIND_FILE) {
            handleFile(context, intent, pkg, metadata)
        } else {
            handleText(intent, pkg, metadata)
        }
    }

    private fun handleText(intent: Intent, pkg: String?, metadata: ClipboardEvent) {
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
            diagnosticId = id,
            privacyRevision = metadata.privacyRevision
        )
        ClipboardEventDispatcher.dispatch(event)
        // 同步内容级已知状态：LSPosed 不经 tryReadClipboard，读取层的 30s 已知比对
        // 感知不到此内容——不同步的话，30s 后 WCC 兜底重读剪贴板会把同内容再发一遍
        // （PC 端同文本移顶、接收页顺序抖动）。
        val coord = com.liuzhuan.app.clipboard.ClipboardMonitorCoordinator
        if (coord.isCaptureReady) coord.captureManager.updateLastKnownText(text)
        android.util.Log.d(TAG, "[$id] text dispatched")
    }

    private fun handleFile(context: Context, intent: Intent, pkg: String?, metadata: ClipboardEvent) {
        val raw = intent.getByteArrayExtra(EXTRA_FILE_BYTES)
        val b64 = if (raw == null) intent.getStringExtra(EXTRA_FILE_B64) else null
        if (raw == null && b64 == null) return
        if ((raw?.size ?: 0) > MAX_FILE_BYTES || (b64?.length ?: 0) > MAX_FILE_BYTES * 4 / 3 + 8) return
        if (pendingFiles.incrementAndGet() > 8) { pendingFiles.decrementAndGet(); return }
        val pending = try { goAsync() } catch (_: Exception) { pendingFiles.decrementAndGet(); return }
        val mime = intent.getStringExtra(EXTRA_FILE_MIME) ?: "application/octet-stream"
        val size = intent.getIntExtra(EXTRA_FILE_SIZE, 0)
        val id = "HOOK-${String.format("%06d", idCounter.incrementAndGet())}"
        android.util.Log.d(TAG, "[$id] file received pkg=$pkg mime=$mime size=$size")
        val app = context.applicationContext
        fileScope.launch {
        var cache: java.io.File? = null
        try {
            if (ClipboardPrivacy.blockReason(metadata) != null) return@launch
            val bytes = raw ?: android.util.Base64.decode(b64 ?: return@launch, android.util.Base64.DEFAULT)
            if (bytes.isEmpty() || bytes.size > MAX_FILE_BYTES) return@launch
            val fingerprint = MessageDigest.getInstance("SHA-256").digest(bytes).hexString()
            val key = "${pkg.orEmpty()}|$mime|${metadata.privacyRevision}|$fingerprint"
            if (!fileDedup.accept(key)) return@launch
            val name = fileBaseName(mime, System.currentTimeMillis())
            val captured = metadata.copy(fingerprint = fingerprint, type = ClipboardContentType.OTHER)
            val client = LanHub.client ?: return@launch
            val file = java.io.File.createTempFile("lz_clip_", ".upload", app.cacheDir)
            cache = file
            file.writeBytes(bytes)
            if (!ClipboardPrivacy.runIfAllowed(metadata) {
                client.pushFileCached(file, name, mime, automaticEvent = captured)
            }) file.delete()
            android.util.Log.d(TAG, "[$id] file upload queued via HTTP")
        } catch (t: Throwable) {
            cache?.delete()
            android.util.Log.d(TAG, "[$id] file decode/upload error ${t.javaClass.simpleName}")
        } finally { pendingFiles.decrementAndGet(); pending.finish() }
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
        const val EXTRA_FILE_BYTES = "file_bytes"
        const val EXTRA_FILE_MIME = "file_mime"
        const val EXTRA_FILE_SIZE = "file_size"
        const val EXTRA_PKG = "pkg"
        const val EXTRA_TS = "ts"
        const val EXTRA_TOKEN = "token"
        /** 握手 token：与 ClipHook 一致（硬编码，不引用 ClipHook 类避免标准版 ClassNotFound） */
        const val TOKEN = "liuzhuan-cliphook-2026-v1-9f3a7c2e"
        private val idCounter = AtomicLong(0)
        private const val MAX_FILE_BYTES = 512 * 1024
        private val fileScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        private val pendingFiles = AtomicInteger(0)
        private val fileDedup = com.liuzhuan.app.core.RecentFileEvents()
    }
}
