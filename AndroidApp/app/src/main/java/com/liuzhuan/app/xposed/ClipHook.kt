package com.liuzhuan.app.xposed

import android.content.ClipData
import android.content.Context
import android.content.Intent
import de.robv.android.xposed.IXposedHookLoadPackage
import de.robv.android.xposed.XC_MethodHook
import de.robv.android.xposed.XposedHelpers
import de.robv.android.xposed.callbacks.XC_LoadPackage

/**
 * LSPosed 剪贴板写入 hook（方案 C）。
 *
 * 思路：在「任意第三方 App 进程」hook `ClipboardManager.setPrimaryClip`，
 * 在 App 写剪贴板那一刻直接截获内容 + 来源包名，通过显式广播发给流转 App。
 *
 * 与无障碍方案的本质区别：
 * - 事件驱动（只截获「写入」这一动作，零轮询、零读取）；
 * - 不读剪贴板 → 完全绕开 ColorOS 的 checkPackage / 焦点 / READ_CLIPBOARD_IN_BACKGROUND
 *   那套读取豁免链（它们只管「读」，管不到「写」）；
 * - 天然拿到 sourcePackage（lpparam.packageName）。
 *
 * 安全铁律：hook 副作用绝不能干扰原 App 的复制——所有逻辑 try-catch 静默吞掉，
 * 只「旁路观察」，不改动返回值、不抛异常。
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
                            val text = clip.getItemAt(0)?.coerceToText(ctx)?.toString()?.trim() ?: return
                            if (text.length < 2) return
                            notifyHost(ctx, pkg, text)
                        } catch (_: Throwable) {
                            // 静默：观察失败不影响原 App 复制
                        }
                    }
                }
            )
        } catch (_: Throwable) {
            // 该 App 环境 hook 失败：静默
        }
    }

    /** 显式广播通知流转 App（root/standard 两个候选包名，谁装谁收） */
    private fun notifyHost(ctx: Context, sourcePkg: String, text: String) {
        for (host in HOST_PACKAGES) {
            try {
                val intent = Intent(ACTION_CLIP)
                    .putExtra(EXTRA_TEXT, text)
                    .putExtra(EXTRA_PKG, sourcePkg)
                    .putExtra(EXTRA_TS, System.currentTimeMillis())
                    .setPackage(host)
                ctx.sendBroadcast(intent)
            } catch (_: Throwable) {
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
        const val ACTION_CLIP = "com.liuzhuan.app.ACTION_CLIPHOOK"
        const val EXTRA_TEXT = "text"
        const val EXTRA_PKG = "pkg"
        const val EXTRA_TS = "ts"
        private val HOST_PACKAGES = listOf("com.liuzhuan.app.root", "com.liuzhuan.app")
    }
}
