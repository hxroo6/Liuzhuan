package com.liuzhuan.app.clipboard

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.util.Log
import android.view.accessibility.AccessibilityEvent
import android.view.accessibility.AccessibilityNodeInfo
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/**
 * 剪贴板捕获管理器 —— 统一决定「如何取得剪贴板/选中内容」，产出 ClipboardEvent。
 *
 * 捕获策略（按优先级，符合 Android 16 限制，不绕过系统权限）：
 * 策略 A：事件判断（CopyEventDetector 筛出疑似复制）
 * 策略 B：AccessibilityNodeInfo selection（选中文本，不依赖焦点，部分 App 可用）
 * 策略 C：ClipboardManager.getPrimaryClip()（仅前台/有焦点时成功，best-effort）
 * 策略 D：短延迟重试（给系统剪贴板极短的更新时间，50~300ms，非轮询）
 *
 * 失败降级：读不到就结束本次捕获，继续监听下一次事件，绝不 crash / 无限重试 / 高频轮询。
 */
interface ClipboardCaptureManager {
    /** 无障碍事件入口：来源分类 + 检测 + 短延迟捕获 + 分发 */
    fun onAccessibilityEvent(event: AccessibilityEvent)

    /** 前台主动捕获入口（App 获得焦点时调用，读取剪贴板合法且成功率高） */
    fun captureOnForeground()

    /** 疑似复制后：短延迟多尝试捕获（diagnosticId 用于端到端日志串联） */
    suspend fun tryCaptureAfterCopy(event: AccessibilityEvent, diagnosticId: String): ClipboardEvent?

    /** 直接读剪贴板（best-effort，可能因焦点限制返回 null） */
    suspend fun tryReadClipboard(reason: String, diagnosticId: String = ""): ClipboardEvent?
}

class ClipboardCaptureManagerImpl(
    private val context: Context, // applicationContext
    private val scope: CoroutineScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
) : ClipboardCaptureManager {

    private val detector = CopyEventDetector

    private val idCounter = java.util.concurrent.atomic.AtomicLong(0)
    private fun nextId() = "COPY-${String.format("%06d", idCounter.incrementAndGet())}"

    override fun onAccessibilityEvent(event: AccessibilityEvent) {
        val category = AccessibilitySourcePolicy.classify(context, event)

        // 第一优先级：来源过滤（自身 App / 来源未知 → 直接忽略）
        if (!AccessibilitySourcePolicy.shouldInspect(context, event)) {
            Log.d(TAG, "[DETECT] ignored reason=self_package/unknown pkg=${event.packageName}")
            return
        }

        val candidate = detector.evaluate(event, category)

        when (candidate.level) {
            CopyEventDetector.Level.NONE -> {
                // 弱信号 / 系统 UI 事件 → 静默忽略（不刷诊断，不触发 capture）
                return
            }

            CopyEventDetector.Level.LOW -> {
                // 弱信号（系统 UI selection 等）：不单独触发 capture，避免高频无意义读取。
                return
            }

            CopyEventDetector.Level.MEDIUM -> {
                // 第三方窗口内容变化（可能是复制菜单弹出）→ 轻量 selection 捕获（不读剪贴板）。
                // 这是 M6 后台自动发送生效的关键路径：微信等 App 复制时只产生
                // WINDOW_CONTENT_CHANGED（不暴露 TEXT_SELECTION_CHANGED），
                // 通过 selection 捕获能在后台拿到选中文本。
                val id = nextId()
                val pkg = event.packageName
                Log.d(TAG, "[DETECT][$id] candidate level=MEDIUM reason=${candidate.reason} pkg=$pkg")
                ClipboardMonitorCoordinator.setDiagnostic("[$id] 复制候选(${candidate.reason}) pkg=$pkg")
                scope.launch {
                    val ev = tryCaptureSelection(event, id)
                    if (ev != null) {
                        Log.d(TAG, "[CAPTURE][$id] success source=${ev.source} len=${ev.text?.length}")
                        ClipboardMonitorCoordinator.setDiagnostic("[$id] 捕获成功 len=${ev.text?.length} source=${ev.source}")
                        ClipboardEventDispatcher.dispatch(ev)
                    } else {
                        Log.d(TAG, "[CAPTURE][$id] MEDIUM selection 未读到（该 App 未暴露 selection）")
                    }
                }
            }

            CopyEventDetector.Level.HIGH -> {
                val id = nextId()
                val pkg = event.packageName
                val cls = event.className
                Log.d(
                    TAG,
                    "[ACCESS][$id] type=0x${Integer.toHexString(event.eventType)} pkg=$pkg class=$cls"
                )
                Log.d(TAG, "[DETECT][$id] candidate level=HIGH reason=${candidate.reason} pkg=$pkg")
                ClipboardMonitorCoordinator.setDiagnostic("[$id] 复制候选(${candidate.reason}) pkg=$pkg")
                scope.launch {
                    val ev = tryCaptureAfterCopy(event, id)
                    if (ev != null) {
                        ClipboardEventDispatcher.dispatch(ev)
                    } else {
                        Log.d(TAG, "[CAPTURE][$id] failed（selection/剪贴板均未读到）")
                    }
                }
            }
        }
    }

    override fun captureOnForeground() {
        scope.launch {
            val ev = tryReadClipboard("app_foreground")
            if (ev != null) {
                Log.d(TAG, "[CAPTURE][FG-fore] 前台重读剪贴板成功 len=${ev.text?.length}")
                ClipboardMonitorCoordinator.setDiagnostic("前台重读剪贴板成功 len=${ev.text?.length}")
                ClipboardEventDispatcher.dispatch(ev)
            } else {
                Log.d(TAG, "[CAPTURE][FG-fore] 前台重读剪贴板为空")
            }
        }
    }

    /** 短延迟多尝试：80ms → 160ms → 260ms，给系统剪贴板极短的更新时间 */
    override suspend fun tryCaptureAfterCopy(event: AccessibilityEvent, diagnosticId: String): ClipboardEvent? {
        Log.d(TAG, "[CAPTURE][$diagnosticId] start")
        var waited = 0L
        val steps = longArrayOf(80L, 80L, 100L)
        for (step in steps) {
            delay(step)
            waited += step
            // 策略 B：selection（优先，不依赖焦点）
            tryCaptureSelection(event, diagnosticId)?.let { ev ->
                Log.d(TAG, "[CAPTURE][$diagnosticId] success source=${ev.source} len=${ev.text?.length} fp=${ev.fingerprint.take(8)}")
                ClipboardMonitorCoordinator.setDiagnostic("[$diagnosticId] 捕获成功 len=${ev.text?.length} source=${ev.source}")
                return ev
            }
            // 策略 C：clipboard（前台/焦点时成功）
            tryReadClipboard("after_copy_${waited}ms", diagnosticId)?.let { ev ->
                Log.d(TAG, "[CAPTURE][$diagnosticId] success source=${ev.source} len=${ev.text?.length} fp=${ev.fingerprint.take(8)}")
                ClipboardMonitorCoordinator.setDiagnostic("[$diagnosticId] 捕获成功 len=${ev.text?.length} source=${ev.source}")
                return ev
            }
        }
        Log.d(TAG, "[CAPTURE][$diagnosticId] failed（selection/剪贴板均未读到）")
        ClipboardMonitorCoordinator.setDiagnostic("[$diagnosticId] 捕获失败（后台焦点限制或 selection 未暴露）")
        return null
    }

    override suspend fun tryReadClipboard(reason: String, diagnosticId: String): ClipboardEvent? {
        return try {
            val cm = context.getSystemService(Context.CLIPBOARD_SERVICE) as? ClipboardManager
                ?: return null
            if (!cm.hasPrimaryClip()) return null
            val clip = cm.primaryClip ?: return null
            if (clip.itemCount == 0) return null
            val item = clip.getItemAt(0) ?: return null
            val text = item.coerceToText(context)?.toString()?.trim() ?: return null
            if (text.length < 2) return null
            val mimeTypes = extractMimeTypes(clip)
            buildEvent(text, ClipboardCaptureSource.ACCESSIBILITY_CLIPBOARD, null, mimeTypes, diagnosticId)
        } catch (e: Exception) {
            // 焦点限制 / OEM 差异导致的读取失败：优雅降级
            Log.d(TAG, "readClipboard[$reason] 失败: ${e.javaClass.simpleName}")
            null
        }
    }

    /** 策略 B：从 AccessibilityNodeInfo 提取选中文本（不依赖剪贴板焦点） */
    private fun tryCaptureSelection(event: AccessibilityEvent, diagnosticId: String): ClipboardEvent? {
        val node = event.source ?: return null
        try {
            val text = extractSelectedText(node, depth = 0) ?: return null
            if (text.length < 2) return null
            val pkg = event.packageName?.toString()
            return buildEvent(
                text,
                ClipboardCaptureSource.ACCESSIBILITY_SELECTION,
                pkg,
                listOf("text/plain"),
                diagnosticId
            )
        } catch (e: Exception) {
            return null
        } finally {
            node.recycle()
        }
    }

    /** 有限递归查找选中文本（最多 2 层 / 64 节点，避免遍历整棵节点树） */
    private fun extractSelectedText(node: AccessibilityNodeInfo, depth: Int): String? {
        node.selectedText()?.let { return it }
        if (depth >= 2) return null
        val childCount = node.childCount.coerceAtMost(16)
        for (i in 0 until childCount) {
            val child = node.getChild(i) ?: continue
            val found = try {
                extractSelectedText(child, depth + 1)
            } catch (_: Exception) {
                null
            } finally {
                child.recycle()
            }
            if (found != null) return found
        }
        return null
    }

    private fun AccessibilityNodeInfo.selectedText(): String? {
        val text = this.text?.toString() ?: return null
        if (text.isEmpty()) return null
        val start = this.textSelectionStart
        val end = this.textSelectionEnd
        if (start >= 0 && end > start && end <= text.length) {
            return text.substring(start, end)
        }
        return null
    }

    private fun extractMimeTypes(clip: ClipData): List<String> {
        val types = mutableSetOf<String>()
        for (i in 0 until clip.itemCount.coerceAtMost(4)) {
            val item = clip.getItemAt(i) ?: continue
            val uri = item.uri
            if (uri != null) {
                types.add("uri:${uri.scheme}")
            } else {
                if (item.htmlText != null) types.add("text/html")
                types.add("text/plain")
            }
        }
        return types.toList()
    }

    private fun buildEvent(
        text: String,
        source: ClipboardCaptureSource,
        sourcePackage: String?,
        mimeTypes: List<String>,
        diagnosticId: String = ""
    ): ClipboardEvent {
        val ts = System.currentTimeMillis()
        return ClipboardEvent(
            text = text,
            timestamp = ts,
            sourcePackage = sourcePackage,
            source = source,
            mimeTypes = mimeTypes,
            fingerprint = fingerprintOf(text, mimeTypes),
            type = ClipboardContentType.TEXT,
            diagnosticId = diagnosticId
        )
    }

    private companion object {
        const val TAG = "ClipCapture"
    }
}
