package com.liuzhuan.app

import android.content.Intent
import android.os.Bundle
import android.widget.Toast
import androidx.activity.ComponentActivity
import com.liuzhuan.app.net.LanClient

/**
 * 文本选择菜单入口（ACTION_PROCESS_TEXT）
 *
 * 在任意应用里选中文本 → 系统文本选择菜单出现「流转」→ 点击即发送到电脑。
 * 文字通过 Intent.EXTRA_PROCESS_TEXT 直达，**不经过剪贴板**，
 * 完全绕过 Android 10+ 后台剪贴板焦点限制——这是「零切换发送」的合规路径。
 *
 * 顺便把文字写入剪贴板（写剪贴板不受焦点限制），保持两端剪贴板一致。
 * 无 UI：透明主题，onCreate 里完成发送即 finish()。
 */
class ProcessTextActivity : ComponentActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        val text = intent.getCharSequenceExtra(Intent.EXTRA_PROCESS_TEXT)?.toString()?.trim() ?: ""

        if (text.isEmpty()) {
            toast("⚠️ 未获取到选中文字")
            finish()
            return
        }

        // 同步写入剪贴板（OP_WRITE_CLIPBOARD 无焦点限制），保持手机剪贴板一致
        try {
            val cm = getSystemService(CLIPBOARD_SERVICE) as android.content.ClipboardManager
            cm.setPrimaryClip(android.content.ClipData.newPlainText("liuzhuan", text))
            // 循环回写防护：标记本 App 写入的内容，避免被无障碍捕获后回推 PC
            com.liuzhuan.app.clipboard.ClipboardMonitorCoordinator.markLocalText(text)
        } catch (_: Exception) {
            // 写剪贴板失败不影响发送
        }

        val client = com.liuzhuan.app.LanHub.client
        when {
            client == null || client.state !is LanClient.State.Connected -> {
                toast("❌ 未连接电脑：请先打开流转并连接")
            }
            client.pushClipboard(text, packageName) -> {
                toast("⚡ 已流转到电脑（${text.length} 字）")
            }
            else -> {
                toast("❌ 发送失败，请重试")
            }
        }
        finish()
    }

    private fun toast(msg: String) {
        Toast.makeText(this, msg, Toast.LENGTH_SHORT).show()
    }
}
