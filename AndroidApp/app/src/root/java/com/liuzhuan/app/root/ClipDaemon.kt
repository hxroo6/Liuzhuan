package com.liuzhuan.app.root

import android.util.Base64

/**
 * root 剪贴板守护进程 —— 由 su 以 uid 0 运行（app_process 加载本 APK 的 dex）。
 *
 * 原理：AOSP ClipboardService 对 SHELL_UID/ROOT_UID 有测试豁免（跳过窗口焦点检查），
 * 因此 root 进程在任意前后台状态都能读到剪贴板——这是无障碍/焦点路径做不到的。
 * 【待确认】ColorOS 是否保留该豁免：若被拒，hasPrimaryClip 通常返回 false（表现为 LZNONE），
 * 与「剪贴板为空」无法区分；只有抛异常时才输出 LZERR。App 端据此只能如实报告，不误判。
 *
 * 事件驱动协议（方案 B）：不主动轮询，等 App 发 READ 命令才读一次剪贴板。
 *
 * stdin（App → daemon，行式命令）：
 *   READ:<id>   读一次剪贴板（<id> 为诊断链路 ID，回传用于端到端日志串联）
 *   PING        存活探针
 *   QUIT        退出
 *   （EOF：App 进程死亡 → 本进程自动退出，自清理，不残留僵尸进程）
 *
 * stdout（daemon → App，行式）：
 *   LZREADY                初始化完成 + 基线已建立
 *   LZCLIP:<id>:<base64>   读到新文本（与上次不同，UTF-8 + NO_WRAP）
 *   LZNONE:<id>            无新内容（剪贴板为空 / 无变化 / 或被 ROM 拒绝后 hasPrimaryClip=false）
 *   LZERR:<id>:read=<异常> 单次读取抛异常（明确的错误，非静默）
 *   LZERR:FATAL:<简述>     致命错误（系统上下文创建失败等），输出后进程退出
 *   LZPONG                 心跳回执
 */
class ClipDaemon {

    companion object {
        @JvmStatic
        fun main(args: Array<String>) {
            android.util.Log.d("ClipDaemon", "main entered uid=" + android.os.Process.myUid())
            // 关键：必须先 prepareMainLooper，否则 systemMain() 内部 thread.attach 抛
            // InvocationTargetException（Android 14+ 实测）
            android.os.Looper.prepareMainLooper()
            android.util.Log.d("ClipDaemon", "looper prepared")
            val ctx = try {
                val atClass = Class.forName("android.app.ActivityThread")
                val at = atClass.getMethod("systemMain").invoke(null)
                atClass.getMethod("getSystemContext").invoke(at) as android.content.Context
            } catch (t: Throwable) {
                val cause = (t as? java.lang.reflect.InvocationTargetException)?.cause
                android.util.Log.e("ClipDaemon", "systemMain FAILED " + t.javaClass.simpleName + ":" + cause?.javaClass?.simpleName + ":" + cause?.message)
                println("LZERR:FATAL:ctx=${t.javaClass.simpleName}:${cause?.javaClass?.simpleName}:${cause?.message}")
                System.out.flush()
                return
            }
            android.util.Log.d("ClipDaemon", "systemContext ok")
            val cm = ctx.getSystemService(android.content.ClipboardManager::class.java)
            if (cm == null) {
                println("LZERR:FATAL:noclipboard")
                System.out.flush()
                return
            }

            // 建立基线：启动时读一次（不输出），避免把历史旧内容当「新复制」误发。
            // 加 try-catch：ColorOS 可能对 uid0 也做 package 检查，读失败要显式上报而非崩溃
            var last: String? = try {
                readClipText(cm, ctx)
            } catch (t: Throwable) {
                android.util.Log.e("ClipDaemon", "init read FAILED uid=" + android.os.Process.myUid() + " err=" + t.javaClass.simpleName + ":" + t.message)
                println("LZERR:init:read=${t.javaClass.simpleName}:${t.message}")
                System.out.flush()
                null
            }
            android.util.Log.d("ClipDaemon", "init read OK uid=" + android.os.Process.myUid() + " len=" + (last?.length ?: 0))
            println("LZREADY")
            System.out.flush()

            // 事件驱动主循环：阻塞读 stdin，收到 READ 才读一次剪贴板（零轮询、零 CPU 空转）
            val reader = System.`in`.bufferedReader()
            while (true) {
                val line = try {
                    reader.readLine()
                } catch (_: Throwable) {
                    null
                } ?: break // EOF：App 死亡 → 退出自清理

                when {
                    line.startsWith("READ:") -> {
                        val id = line.removePrefix("READ:").trim()
                        try {
                            if (cm.hasPrimaryClip()) {
                                val text = readClipText(cm, ctx)
                                if (!text.isNullOrEmpty() && text.length >= 2 && text != last) {
                                    last = text
                                    println("LZCLIP:$id:" + Base64.encodeToString(text.toByteArray(Charsets.UTF_8), Base64.NO_WRAP))
                                } else {
                                    println("LZNONE:$id")
                                }
                            } else {
                                // 空或被 ROM 拒绝（uid0 下 hasPrimaryClip 被拒时也返回 false，无法区分）
                                last = null
                                println("LZNONE:$id")
                            }
                            System.out.flush()
                        } catch (t: Throwable) {
                            println("LZERR:$id:read=${t.javaClass.simpleName}")
                            System.out.flush()
                        }
                    }

                    line.startsWith("PING") -> {
                        println("LZPONG")
                        System.out.flush()
                    }

                    line.startsWith("QUIT") -> break

                    // 未知命令静默忽略
                }
            }
        }

        /** 读剪贴板第一项的纯文本（trim 后）；异常向上抛由调用方区分 LZERR */
        private fun readClipText(
            cm: android.content.ClipboardManager,
            ctx: android.content.Context
        ): String? {
            return cm.primaryClip?.getItemAt(0)?.coerceToText(ctx)?.toString()?.trim()
        }
    }
}
