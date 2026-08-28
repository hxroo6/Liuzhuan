package com.liuzhuan.app.xposed

import android.content.ClipData
import android.content.Context
import android.content.Intent
import android.util.Base64
import android.util.Log
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
 * - URI item（图片/文件）：读字节流 → base64 → 文件广播（kind=file）
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
                                    handleFileItem(ctx, pkg, uri)
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

    /** 读 URI（图片/文件）字节流，base64 后走文件广播（大文件跳过，走 HTTP 的后续优化项） */
    private fun handleFileItem(ctx: Context, pkg: String, uri: android.net.Uri) {
        try {
            val mime = ctx.contentResolver.getType(uri) ?: "application/octet-stream"
            val bytes = ctx.contentResolver.openInputStream(uri)?.use { it.readBytes() } ?: return
            if (bytes.isEmpty()) return
            if (bytes.size > MAX_FILE_BYTES) {
                Log.d(TAG, "file too large skip pkg=$pkg size=${bytes.size}")
                return
            }
            val b64 = Base64.encodeToString(bytes, Base64.NO_WRAP)
            Log.d(TAG, "file captured pkg=$pkg mime=$mime size=${bytes.size}")
            notifyFile(ctx, pkg, b64, mime, bytes.size)
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
    private fun notifyFile(ctx: Context, sourcePkg: String, b64: String, mime: String, size: Int) {
        for (host in HOST_PACKAGES) {
            try {
                val intent = Intent(ACTION_CLIP)
                    .putExtra(EXTRA_KIND, KIND_FILE)
                    .putExtra(EXTRA_FILE_B64, b64)
                    .putExtra(EXTRA_FILE_MIME, mime)
                    .putExtra(EXTRA_FILE_SIZE, size)
                    .putExtra(EXTRA_PKG, sourcePkg)
                    .putExtra(EXTRA_TS, System.currentTimeMillis())
                    .putExtra(EXTRA_TOKEN, TOKEN)
                    .setPackage(host)
                ctx.sendBroadcast(intent)
                Log.d(TAG, "file broadcast to $host size=$size")
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
        const val EXTRA_FILE_MIME = "file_mime"
        const val EXTRA_FILE_SIZE = "file_size"
        const val EXTRA_PKG = "pkg"
        const val EXTRA_TS = "ts"
        const val EXTRA_TOKEN = "token"
        /** 握手 token：与 ClipReceiver 校验一致，防止其他 App 伪造复制广播 */
        const val TOKEN = "liuzhuan-cliphook-2026-v1-9f3a7c2e"
        /** 文件大小上限：base64 走广播 extra，受 Binder ~1MB 限制，512KB 原文件留足余量 */
        private const val MAX_FILE_BYTES = 512 * 1024
        private val HOST_PACKAGES = listOf("com.liuzhuan.app")
    }
}
