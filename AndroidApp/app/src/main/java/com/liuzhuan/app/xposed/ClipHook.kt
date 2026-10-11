package com.liuzhuan.app.xposed

import android.content.ClipData
import android.content.Context
import android.content.Intent
import android.util.Log
import com.liuzhuan.app.core.readWithinLimit
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.ThreadPoolExecutor
import java.util.concurrent.TimeUnit
import de.robv.android.xposed.IXposedHookLoadPackage
import de.robv.android.xposed.XC_MethodHook
import de.robv.android.xposed.XposedHelpers
import de.robv.android.xposed.callbacks.XC_LoadPackage

/**
 * LSPosed 剪贴板写入 hook（方案 C）—— 文本 + 图片/文件（不限格式）。
 *
 * 思路：在「任意第三方 App 进程」hook `ClipboardManager.setPrimaryClip`，
 * 在 App 写剪贴板那一刻直接截获内容 + 来源包名，通过显式广播发给流转 App。
 *
 * 截获两类 item：
 * - 文本 item：coerceToText → 文本广播（kind=text）
 * - URI item（图片/文件）：有界后台读字节流 → 字节文件广播（kind=file）
 *
 * 与无障碍方案的本质区别：事件驱动、不读剪贴板、绕开 ColorOS 读取豁免链。
 *
 * 安全铁律：hook 副作用绝不能干扰原 App 的复制——所有逻辑 try-catch 吞掉，
 * 只「旁路观察」，不改动返回值、不抛异常。
 *
 * 日志：只打长度/包名/阶段，不打完整内容（敏感）。
 */
class ClipHook : IXposedHookLoadPackage {

    override fun handleLoadPackage(lpparam: XC_LoadPackage.LoadPackageParam) {
        val pkg = lpparam.packageName
        // 跳过系统进程；跳过自身（回环防护：流转自己的写入走前台读剪贴板链路，不重复发）
        if (pkg == "android" || pkg.startsWith("com.liuzhuan")) return

        try {
            XposedHelpers.findAndHookMethod(
                "android.content.ClipboardManager",
                lpparam.classLoader,
                "setPrimaryClip",
                ClipData::class.java,
                object : XC_MethodHook() {
                    override fun afterHookedMethod(param: MethodHookParam) {
                        try {
                            val clip = param.args.getOrNull(0) as? ClipData ?: return
                            val ctx = currentSystemContext() ?: return
                            for (i in 0 until clip.itemCount.coerceAtMost(8)) {
                                val item = clip.getItemAt(i) ?: continue
                                val uri = item.uri
                                if (uri != null) {
                                    // 图片 I/O 不阻塞原 App 的复制线程；队列满时旁路跳过。
                                    val capturedAt = System.currentTimeMillis()
                                    fileWorker.execute { handleFileItem(ctx, pkg, uri, capturedAt) }
                                } else {
                                    val text = item.coerceToText(ctx)?.toString()?.trim() ?: continue
                                    if (text.length >= 2) {
                                        Log.d(TAG, "setPrimaryClip pkg=$pkg len=${text.length}")
                                        notifyText(ctx, pkg, text)
                                    }
                                }
                            }
                        } catch (t: Throwable) {
                            Log.d(TAG, "afterHooked error pkg=$pkg err=${t.javaClass.simpleName}")
                        }
                    }
                }
            )
            Log.d(TAG, "hook installed pkg=$pkg")
        } catch (t: Throwable) {
            Log.d(TAG, "hook install failed pkg=$pkg err=${t.javaClass.simpleName}")
        }
    }

    /** 最多读取 512 KiB+1，超限跳过；原复制时间不因异步队列延后 */
    private fun handleFileItem(ctx: Context, pkg: String, uri: android.net.Uri, capturedAt: Long) {
        try {
            val mime = ctx.contentResolver.getType(uri) ?: "application/octet-stream"
            val bytes = ctx.contentResolver.openInputStream(uri)?.use { it.readWithinLimit(MAX_FILE_BYTES) } ?: return
            if (bytes.isEmpty()) return
            if (bytes.size > MAX_FILE_BYTES) {
                Log.d(TAG, "file too large skip pkg=$pkg size=${bytes.size}")
                return
            }
            Log.d(TAG, "file captured pkg=$pkg mime=$mime size=${bytes.size}")
            notifyFile(ctx, pkg, bytes, mime, capturedAt)
        } catch (t: Throwable) {
            Log.d(TAG, "file read error pkg=$pkg err=${t.javaClass.simpleName}")
        }
    }

    /** 文本广播 */
    private fun notifyText(ctx: Context, sourcePkg: String, text: String) {
        for (host in HOST_PACKAGES) {
            try {
                val intent = Intent(ACTION_CLIP)
                    .putExtra(EXTRA_KIND, KIND_TEXT)
                    .putExtra(EXTRA_TEXT, text)
                    .putExtra(EXTRA_PKG, sourcePkg)
                    .putExtra(EXTRA_TS, System.currentTimeMillis())
                    .putExtra(EXTRA_TOKEN, TOKEN)
                    .setPackage(host)
                ctx.sendBroadcast(intent)
                Log.d(TAG, "text broadcast to $host len=${text.length}")
            } catch (t: Throwable) {
                Log.d(TAG, "broadcast failed to $host err=${t.javaClass.simpleName}")
            }
        }
    }

    /** 文件广播 */
    private fun notifyFile(ctx: Context, sourcePkg: String, bytes: ByteArray, mime: String, capturedAt: Long) {
        for (host in HOST_PACKAGES) {
            try {
                val intent = Intent(ACTION_CLIP)
                    .putExtra(EXTRA_KIND, KIND_FILE)
                    .putExtra(EXTRA_FILE_BYTES, bytes)
                    .putExtra(EXTRA_FILE_MIME, mime)
                    .putExtra(EXTRA_FILE_SIZE, bytes.size)
                    .putExtra(EXTRA_PKG, sourcePkg)
                    .putExtra(EXTRA_TS, capturedAt)
                    .putExtra(EXTRA_TOKEN, TOKEN)
                    .setPackage(host)
                ctx.sendBroadcast(intent)
                Log.d(TAG, "file broadcast to $host size=${bytes.size}")
            } catch (t: Throwable) {
                Log.d(TAG, "broadcast failed to $host err=${t.javaClass.simpleName}")
            }
        }
    }

    /** 反射拿当前进程的 system context（微信进程内返回微信进程的 system context） */
    private fun currentSystemContext(): Context? {
        return try {
            val at = Class.forName("android.app.ActivityThread")
                .getMethod("currentActivityThread").invoke(null)
            at.javaClass.getMethod("getSystemContext").invoke(at) as? Context
        } catch (_: Throwable) {
            null
        }
    }

    companion object {
        const val TAG = "ClipHook"
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
        /** 握手 token：与 ClipReceiver 校验一致，防止其他 App 伪造复制广播 */
        const val TOKEN = "liuzhuan-cliphook-2026-v1-9f3a7c2e"
        /** 文件大小上限：字节广播避免 Base64 字符串膨胀；Binder 仍有总事务预算 */
        private const val MAX_FILE_BYTES = 512 * 1024
        private val HOST_PACKAGES = listOf("com.liuzhuan.app")
        private val fileWorker = ThreadPoolExecutor(1, 1, 30, TimeUnit.SECONDS,
            ArrayBlockingQueue(8), { task -> Thread(task, "liuzhuan-hook-file").apply { isDaemon = true } },
            ThreadPoolExecutor.DiscardPolicy()).apply { allowCoreThreadTimeOut(true) }
    }
}
