package com.liuzhuan.app.root

import android.content.Context
import com.liuzhuan.app.LanHub
import com.liuzhuan.app.clipboard.ClipboardCaptureSource
import com.liuzhuan.app.clipboard.ClipboardContentType
import com.liuzhuan.app.clipboard.ClipboardEvent
import com.liuzhuan.app.clipboard.ClipboardEventDispatcher
import com.liuzhuan.app.clipboard.ClipboardMonitorCoordinator
import com.liuzhuan.app.clipboard.fingerprintOf
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import java.io.BufferedReader
import java.io.InputStreamReader
import java.util.concurrent.atomic.AtomicLong

/**
 * root 后台剪贴板监控（仅存在于 root flavor，经 RootBridge 反射加载）。
 *
 * 方案 B：事件驱动 + 一次性 root 读（不轮询）。
 * - 无障碍检测到疑似复制（MEDIUM/HIGH）→ ClipboardCaptureManager 调 RootBridge.requestRead
 *   → 本类往守护 stdin 写 READ:<id> → 守护读一次剪贴板 → 输出 LZCLIP:<id>:<b64>
 *   → 本类消费 → 走标准 Dispatcher（autoSend 过滤 + 指纹去重 + 队列补发全复用）。
 * - 守护进程零轮询：平时阻塞在 stdin.read，App 死 → stdin EOF → 守护自清理，无僵尸进程。
 * - 命中后同步 updateLastKnownText：防止切前台时焦点重读把同内容再发一遍。
 * - 独立开关：发送页「Root 后台读取」开关（LanHub.rootClipboardSync），与无障碍开关解耦。
 *
 * 日志：所有关键节点打 [ROOT] 前缀，同时进 Coordinator 诊断历史（UI 可见）+ logcat。
 * 前提：设备已 root（Magisk 等）且 ROM 保留 ClipboardService 对 uid 0 的测试豁免
 * （AOSP 默认保留，ColorOS 未验证——被拒时表现为 LZNONE，见 ClipDaemon 注释）。
 */
class RootMonitorImpl(private val context: Context) {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val idCounter = AtomicLong(0)
    private var rootOk: Boolean? = null
    private var proc: Process? = null

    /** 成功探测到的 su 绝对路径（spawnDaemon 复用，避免重复探测） */
    @Volatile
    private var suPath: String? = null

    @Volatile
    private var daemonReady = false

    /** 串行化 stdin 写入（requestRead 可能从多个协程并发调用） */
    private val writeLock = Any()

    fun start() {
        scope.launch { supervise() }
        log("root 监控已启动（等待开关）")
    }

    /** 主动请求一次剪贴板读取（由无障碍候选触发；标准版 RootBridge 反射不到则 no-op） */
    fun requestRead(diagnosticId: String) {
        val p = proc ?: return
        if (!daemonReady) return
        synchronized(writeLock) {
            try {
                p.outputStream.write("READ:$diagnosticId\n".toByteArray(Charsets.UTF_8))
                p.outputStream.flush()
                log("[$diagnosticId] 已请求 root 读取")
            } catch (e: Exception) {
                // 管道断裂 → 守护已死，readLoop 会负责重启
                log("[$diagnosticId] 写守护失败（${e.javaClass.simpleName}），守护可能已退出")
            }
        }
    }

    /** 主动触发 su 授权（发送页「获取 Root 权限」按钮；force 强制重探，首次弹授权框） */
    fun requestPermission() {
        scope.launch {
            log("手动触发 root 权限请求…")
            val ok = ensureRoot(force = true)
            log(
                when (ok) {
                    true -> "root 已授权（uid 0 可用），守护即将自动拉起"
                    false -> "root 未授权 / 被拒，详见上一条「探测失败」日志"
                    null -> "root 检测中（等待用户授权）"
                }
            )
        }
    }

    private suspend fun supervise() {
        var attempt = 0
        while (true) {
            if (!LanHub.rootClipboardSync) {
                if (daemonReady) log("root 开关已关闭，暂停守护")
                daemonReady = false
                delay(3_000)
                continue
            }
            if (proc == null) {
                if (ensureRoot() != true) {
                    log("未检测到可用 root（su 被拒/未授权），后台读取不可用")
                    delay(60_000)
                    continue
                }
                val np = spawnDaemon()
                if (np == null) {
                    log("守护启动失败，30s 后重试")
                    delay(30_000)
                    continue
                }
                proc = np
                attempt = 0
                daemonReady = false
                log("守护已拉起，等待 LZREADY")
                readLoop(np) // 阻塞直到守护退出
                proc = null
                daemonReady = false
                log("守护进程退出，准备重启")
            }
            attempt++
            delay(minOf(5_000L * attempt, 30_000L))
        }
    }

    /**
     * su 探针：能以 uid 0 执行 id 即视为可用（首次弹授权框，10s 超时视为拒绝）。
     *
     * 关键修正（KernelSU）：**不依赖 File.exists() 探测**。KernelSU 的 sucompat 是内核
     * 对 /system/bin/su 的 execve 拦截（stat 探测可能不被拦截 → 误判「找不到 su」），
     * 所以必须直接尝试执行每个候选路径，能返回 uid=0 的就是可用 su。
     * 只有「成功(true)」才缓存；失败不缓存 → supervise 与手动按钮都能重试。
     */
    private fun ensureRoot(force: Boolean = false): Boolean? {
        if (!force) rootOk?.let { return it }
        log("探测 root 权限…")
        for (candidate in SU_CANDIDATES) {
            if (tryExecSu(candidate)) {
                rootOk = true
                suPath = candidate
                log("root 探测成功（uid=0，su=$candidate）")
                return true
            }
        }
        log("所有 su 路径均不可用（未授权或 su 不存在）")
        return false
    }

    /** 直接尝试以 candidate 执行 `id -u`，返回是否拿到 uid=0（不先 File.exists 探测） */
    private fun tryExecSu(candidate: String): Boolean {
        return try {
            val p = Runtime.getRuntime().exec(arrayOf(candidate, "-c", "id -u"))
            // 后台线程读 stdout/stderr（避免输出缓冲满导致 waitFor 死锁）；主线程 waitFor 带超时
            val outFuture = java.util.concurrent.CompletableFuture.supplyAsync {
                try { p.inputStream.bufferedReader().use { it.readText().trim() } } catch (_: Exception) { "" }
            }
            val errFuture = java.util.concurrent.CompletableFuture.supplyAsync {
                try { p.errorStream.bufferedReader().use { it.readText().trim() } } catch (_: Exception) { "" }
            }
            val finished = p.waitFor(10, java.util.concurrent.TimeUnit.SECONDS)
            if (!finished) {
                p.destroyForcibly()
                log("$candidate 超时（可能授权框未确认）")
                false
            } else {
                val exit = p.exitValue()
                val out = outFuture.get(2, java.util.concurrent.TimeUnit.SECONDS)
                val err = errFuture.get(2, java.util.concurrent.TimeUnit.SECONDS)
                val ok = exit == 0 && out == "0"
                if (!ok) {
                    log("$candidate 执行失败 exit=$exit stderr=\"${err.take(100)}\"")
                }
                ok
            }
        } catch (_: Exception) {
            // IOException（No such file / Permission denied）= 该路径无 su，静默试下一个
            false
        }
    }

    private fun spawnDaemon(): Process? {
        return try {
            val su = suPath ?: run {
                log("尚未确定 su 路径，无法启动守护")
                return null
            }
            val script = "pkill -f lzclipd 2>/dev/null; " +
                "CLASSPATH=${context.applicationInfo.sourceDir} " +
                "app_process /system/bin --nice-name=lzclipd com.liuzhuan.app.root.ClipDaemon"
            log("启动守护：$su -c app_process --nice-name=lzclipd")
            Runtime.getRuntime().exec(arrayOf(su, "-c", script))
        } catch (e: Exception) {
            log("守护启动失败 ${e.javaClass.simpleName}")
            null
        }
    }

    private suspend fun readLoop(p: Process) {
        var fatal: String? = null
        try {
            val reader = BufferedReader(InputStreamReader(p.inputStream))
            while (true) {
                val line = reader.readLine() ?: break // EOF：守护退出/管道断裂
                when {
                    line.startsWith("LZREADY") -> {
                        daemonReady = true
                        log("守护就绪（LZREADY），事件驱动读取链路已通")
                        // 自检：就绪后发一次 PING 验证双向通信
                        p.outputStream.write("PING\n".toByteArray(Charsets.UTF_8))
                        p.outputStream.flush()
                    }
                    line.startsWith("LZPONG") -> {
                        log("双向通信自检通过（PING→LZPONG）")
                    }
                    line.startsWith("LZCLIP:") -> {
                        // 格式 LZCLIP:<id>:<base64>
                        val body = line.removePrefix("LZCLIP:")
                        val idx = body.indexOf(':')
                        if (idx <= 0) continue
                        val id = body.substring(0, idx)
                        val b64 = body.substring(idx + 1)
                        handleContent(id, b64)
                    }
                    line.startsWith("LZNONE:") -> {
                        val id = line.removePrefix("LZNONE:")
                        log("[$id] root 读无新内容（剪贴板为空 / 无变化 / 或 ROM 拒绝 root 读）")
                    }
                    line.startsWith("LZERR:FATAL") -> {
                        fatal = line
                        break
                    }
                    line.startsWith("LZERR") -> {
                        // 格式 LZERR:<id>:read=<异常>
                        log("root 读被拒（${line}）——ROM 可能未豁免 root 读取")
                    }
                }
            }
        } catch (e: Exception) {
            log("readLoop 结束: ${e.javaClass.simpleName}")
        } finally {
            try { p.destroy() } catch (_: Exception) {}
        }
        if (fatal != null) {
            log("守护致命错误（$fatal），5 分钟后重试")
            delay(300_000)
        }
    }

    private fun handleContent(id: String, b64: String) {
        val text = try {
            String(android.util.Base64.decode(b64, android.util.Base64.NO_WRAP), Charsets.UTF_8)
        } catch (_: IllegalArgumentException) {
            log("[$id] base64 解码失败")
            return
        }
        if (text.length < 2) {
            log("[$id] 内容过短（len=${text.length}），忽略")
            return
        }
        val ev = ClipboardEvent(
            text = text,
            timestamp = System.currentTimeMillis(),
            sourcePackage = null,
            source = ClipboardCaptureSource.ROOT_DAEMON,
            mimeTypes = listOf("text/plain"),
            fingerprint = fingerprintOf(text, listOf("text/plain")),
            type = ClipboardContentType.TEXT,
            diagnosticId = id
        )
        log("[$id] root 后台读取命中 len=${text.length}")
        ClipboardEventDispatcher.dispatch(ev)
        // 同步内容级已知状态：防止切前台时 ON_RESUME/焦点重读把同内容再发一遍
        try {
            if (ClipboardMonitorCoordinator.isCaptureReady) {
                ClipboardMonitorCoordinator.captureManager.updateLastKnownText(text)
            }
        } catch (_: Exception) {
        }
    }

    /** root 日志：同时进 Coordinator 诊断历史（UI 可见）+ logcat（ClipRoot tag） */
    private fun log(msg: String) {
        ClipboardMonitorCoordinator.setDiagnostic("[ROOT] $msg")
        android.util.Log.d(TAG, msg)
    }

    private companion object {
        const val TAG = "ClipRoot"

        /** su 候选绝对路径（App 默认 PATH 不含 KernelSU 路径，必须逐个直接尝试执行） */
        private val SU_CANDIDATES = arrayOf(
            "/system/bin/su",        // KernelSU sucompat 内核拦截点 + Magisk
            "/system/xbin/su",       // 老 root
            "/sbin/su",              // 老 Magisk
            "/data/adb/ksu/bin/su",  // KernelSU 官方路径（目录 700，App 常无法访问，仅兜底）
            "/su/bin/su"             // 老 SuperSU
        )
    }
}
