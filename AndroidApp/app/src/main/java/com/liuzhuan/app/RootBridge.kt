package com.liuzhuan.app

import android.content.Context

/**
 * root 变体桥接器 —— 标准版空实现，零开销。
 *
 * root flavor（applicationIdSuffix ".root"）的源集里存在 com.liuzhuan.app.root.RootMonitorImpl，
 * 本桥通过反射注册并启动它；标准版 Class.forName 抛 ClassNotFoundException → 静默跳过。
 * 这样主源集不依赖 root 模块的任何符号，两套 APK 共用同一份主代码。
 *
 * 三个入口（均为反射，标准版 no-op）：
 * - init(context)：注册并启动 root 监控（MainActivity.onCreate 与 ClipMonitorService.onCreate）
 * - requestRead(diagnosticId)：请求一次 root 剪贴板读取（由无障碍候选触发，方案 B 事件驱动）
 * - requestPermission()：主动触发 su 授权（发送页「获取 Root 权限」按钮）
 *
 * 开关：发送页「Root 后台读取」开关（LanHub.rootClipboardSync），RootMonitorImpl 每轮检查。
 */
object RootBridge {
    @Volatile
    private var impl: Any? = null
    @Volatile
    private var started = false

    fun init(context: Context) {
        if (started) return
        synchronized(this) {
            if (started) return
            started = true
            try {
                val i = Class.forName("com.liuzhuan.app.root.RootMonitorImpl")
                    .getDeclaredConstructor(Context::class.java)
                    .newInstance(context)
                impl = i
                i.javaClass.getMethod("start").invoke(i)
                android.util.Log.d("RootBridge", "root 剪贴板监控已注册")
            } catch (_: ClassNotFoundException) {
                // 标准版：无 root 模块，正常情况
            } catch (e: Throwable) {
                android.util.Log.d("RootBridge", "root 监控注册失败: ${e.javaClass.simpleName}: ${e.message}")
                started = false // 注册失败允许下次再试
            }
        }
    }

    /** 请求一次 root 剪贴板读取（标准版 no-op；root 版守护未就绪时同样 no-op） */
    fun requestRead(diagnosticId: String) {
        val i = impl ?: return
        try {
            i.javaClass.getMethod("requestRead", String::class.java).invoke(i, diagnosticId)
        } catch (_: Throwable) {
            // 反射失败静默：root 读是旁路兜底，不影响主链路
        }
    }

    /** 主动触发 su 授权（发送页「获取 Root 权限」按钮；标准版 no-op） */
    fun requestPermission() {
        val i = impl ?: return
        try {
            i.javaClass.getMethod("requestPermission").invoke(i)
        } catch (_: Throwable) {
        }
    }

    /** 当前是否为 root flavor（供 UI 判断是否显示 root 设置项） */
    fun isRootFlavor(context: Context): Boolean =
        context.packageName.endsWith(".root")
}
