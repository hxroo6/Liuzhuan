package com.liuzhuan.app.clipboard

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.util.Log
import android.view.accessibility.AccessibilityEvent
import android.view.accessibility.AccessibilityNodeInfo
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
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

    /** 直接读剪贴板（best-effort，可能因焦点限制返回 null）。
     *  checkKnown=true 时做内容级防误发：读到与近期已知相同的内容返回 null（详见 Impl 说明） */
    suspend fun tryReadClipboard(
        reason: String,
        diagnosticId: String = "",
        checkKnown: Boolean = true
    ): ClipboardEvent?

    /** 标记「本 App 刚写入剪贴板」的内容（内容级已知状态，配合 markLocal 防回环/防误发） */
    fun updateLastKnownText(text: String)
}

class ClipboardCaptureManagerImpl(
    private val context: Context, // applicationContext
    private val scope: CoroutineScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
) : ClipboardCaptureManager {

    private val detector = CopyEventDetector

    private val idCounter = java.util.concurrent.atomic.AtomicLong(0)
    private fun nextId() = "COPY-${String.format("%06d", idCounter.incrementAndGet())}"

    /**
     * 上次已知的剪贴板文本 + 记录时刻（内容级防误发）。
     *
     * 背景：MEDIUM 剪贴板兜底在 WINDOW_CONTENT_CHANGED 高频事件下读取剪贴板时，
     * 读到的常常是「上一次复制留下的旧内容」——直接发出去就是误发。
     * 因此读取层维护已知状态：与近期已知内容相同 → 视为无新内容（返回 null）。
     * 时间窗 KNOWN_STALE_WINDOW_MS 内拦截（覆盖菜单操作/回环时间尺度），
     * 超窗后允许同内容再次触发（用户可能故意重复复制同一段文字）。
     */
    @Volatile
    private var lastKnownClipText: String? = null
    @Volatile
    private var lastKnownClipAt = 0L

    /** 待执行的兜底任务（debounce：连续 MEDIUM 事件只保留最后一轮） */
    private var fallbackJob: Job? = null

    override fun updateLastKnownText(text: String) {
        lastKnownClipText = text
        lastKnownClipAt = System.currentTimeMillis()
    }

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
                // 第三方窗口内容变化（可能是复制菜单弹出）→ 先试 selection 捕获（不读剪贴板）。
                // 流转前台时：selection 或剪贴板兜底均可能命中（焦点就绪）；
                // 流转后台时：两条路都被系统关死（App 不暴露 selection + 焦点限制拒绝剪贴板），
                // 只能等切回流转时由前台重读（onWindowFocusChanged）补发剪贴板里的最新内容。
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
                        Log.d(TAG, "[CAPTURE][$id] MEDIUM selection 未读到（该 App 未暴露 selection），安排剪贴板兜底")
                        // selection 读不到时尝试剪贴板（前台有效；后台被焦点限制拒绝，见方法注释）
                        scheduleMediumFallback(id)
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

    /**
     * MEDIUM 剪贴板兜底 —— debounce 模式的事件驱动读取（非轮询，无定时器）。
     *
     * 适用范围（2026-08-27 adb 实测定论）：前台/焦点就绪时有效——流转在前台期间，
     * 任意第三方 App 的界面事件（含其他 App 的浮动窗如截屏）触发本兜底即可读到剪贴板新内容；
     * 流转在后台时被系统焦点检查拒绝（hasPrimaryClip=false），「不切回流转的后台自动发送」
     * 在严格执行该限制的 ROM（如本机 ColorOS）上无解，可靠路径为文本选择菜单或切回流转发。
     *
     * debounce：每个 MEDIUM 事件重置计时，等事件流安静 FALLBACK_DEBOUNCE_MS 后读一次
     * ——天然合并「长按→菜单→复制」整串事件（读取频率被事件流间隔限制），
     * 且读取时机落在「复制已完成」之后（M14 固定节流会漏掉复制完成事件，已废弃）。
     */
    @Synchronized
    private fun scheduleMediumFallback(diagnosticId: String) {
        fallbackJob?.cancel()
        fallbackJob = scope.launch {
            delay(FALLBACK_DEBOUNCE_MS)
            Log.d(TAG, "[CAPTURE][$diagnosticId] medium_fallback start（debounce 后读取）")
            ClipboardMonitorCoordinator.setDiagnostic("[$diagnosticId] 兜底读取剪贴板中…")
            repeat(2) { attempt ->
                if (attempt > 0) delay(200)
                val ev = tryReadClipboard("medium_fallback_${attempt + 1}", diagnosticId)
                if (ev != null) {
                    Log.d(
                        TAG,
                        "[CAPTURE][$diagnosticId] medium_fallback 命中（剪贴板新内容 len=${ev.text?.length}）"
                    )
                    ClipboardMonitorCoordinator.setDiagnostic(
                        "[$diagnosticId] 剪贴板兜底命中 len=${ev.text?.length}"
                    )
                    ClipboardEventDispatcher.dispatch(ev)
                    return@launch
                }
            }
            Log.d(TAG, "[CAPTURE][$diagnosticId] medium_fallback 未命中（原因见上一条诊断）")
            ClipboardMonitorCoordinator.setDiagnostic("[$diagnosticId] 兜底未读到新内容（原因见上一条）")
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

    override suspend fun tryReadClipboard(
        reason: String,
        diagnosticId: String,
        checkKnown: Boolean
    ): ClipboardEvent? {
        // 失败原因上 UI 诊断（仅 diagnosticId 非空的调用：兜底/HIGH 路径；
        // 前台重读与手动测试按钮传空 id，各自已有结果展示，避免重复刷屏）
        fun diag(msg: String) {
            if (diagnosticId.isNotEmpty()) {
                ClipboardMonitorCoordinator.setDiagnostic("[$diagnosticId] $msg")
            }
        }
        return try {
            val cm = context.getSystemService(Context.CLIPBOARD_SERVICE) as? ClipboardManager
                ?: run { diag("剪贴板服务不可用"); return null }
            if (!cm.hasPrimaryClip()) {
                // 后台焦点限制的典型表现：系统对非焦点 App 直接不提供剪贴板
                diag("系统未提供剪贴板（后台读取被拒或为空）")
                return null
            }
            val clip = cm.primaryClip ?: run { diag("剪贴板内容为空"); return null }
            if (clip.itemCount == 0) return null
            val item = clip.getItemAt(0) ?: return null
            val text = item.coerceToText(context)?.toString()?.trim() ?: return null
            if (text.length < 2) return null
            // 内容级防误发：近期已知内容 → 视为「无新内容」（WCC 兜底/前台重读读到旧剪贴板时
            // 不重复产生事件；超窗后同内容放行，保留「故意重复复制同一段文字」的语义）
            if (checkKnown && text == lastKnownClipText) {
                val age = System.currentTimeMillis() - lastKnownClipAt
                if (age < KNOWN_STALE_WINDOW_MS) {
                    // 注：能走到这里说明剪贴板读取本身成功（未被后台限制拒绝）
                    diag("读到旧内容（${age / 1000}s 前已处理过相同内容，跳过）")
                    Log.d(TAG, "readClipboard[$reason] 已知旧内容，跳过（${age}ms 内重复）")
                    return null
                }
            }
            lastKnownClipText = text
            lastKnownClipAt = System.currentTimeMillis()
            val mimeTypes = extractMimeTypes(clip)
            buildEvent(text, ClipboardCaptureSource.ACCESSIBILITY_CLIPBOARD, null, mimeTypes, diagnosticId)
        } catch (e: Exception) {
            // 焦点限制 / OEM 差异导致的读取失败：优雅降级
            diag("读取异常 ${e.javaClass.simpleName}（可能是后台限制的表现）")
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

        /** MEDIUM 剪贴板兜底的 debounce 安静期：WCC 事件流停止该时长后才执行读取
         *  （合并「长按→菜单→复制」整串事件，读取时机落在复制完成之后） */
        const val FALLBACK_DEBOUNCE_MS = 500L

        /** 内容级已知比对的生效窗口：窗口内同内容不重复发，超窗后允许（重复复制语义） */
        const val KNOWN_STALE_WINDOW_MS = 30_000L
    }
}
