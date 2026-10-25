package com.liuzhuan.app.audio

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import com.liuzhuan.app.net.LanClient
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.launch

/** Explicit, one-shot control over the authenticated material socket; never starts work on composition. */
@Composable
internal fun ComputerAudioConfigurationCard(
    connected: Boolean,
    supported: Boolean,
    microphoneActive: Boolean,
    connecting: Boolean,
    onRequest: suspend (repair: Boolean) -> LanClient.AudioConfigurationResult,
    onConnectAfterRepair: () -> Unit
) {
    val scope = rememberCoroutineScope()
    val owner = LocalLifecycleOwner.current
    var working by remember { mutableStateOf(false) }
    var result by remember { mutableStateOf<LanClient.AudioConfigurationResult?>(null) }
    var error by remember { mutableStateOf("") }
    LaunchedEffect(connected) { if (!connected) { result = null; error = "" } }

    fun request(repair: Boolean) {
        if (working || !connected || !supported || repair && microphoneActive) return
        working = true; result = null; error = ""
        scope.launch {
            try {
                val reply = onRequest(repair)
                result = reply
                // A result may remain visible after leaving the foreground. It must not become
                // delayed permission to start recording or reconnect on its own after returning.
                if (repair && reply.ready && reply.isCurrent() && !microphoneActive &&
                    owner.lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED)) onConnectAfterRepair()
            } catch (_: TimeoutCancellationException) {
                error = "电脑音频检查超时。请确认电脑流转仍在运行，再点一次重试；无需重新扫码。"
            } catch (ex: CancellationException) { throw ex
            } catch (ex: Exception) {
                error = ex.message ?: "无法检查电脑音频，请确认两端连接后重试"
            } finally { working = false }
        }
    }

    Card {
        Column(Modifier.fillMaxWidth().padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text("电脑音频", style = MaterialTheme.typography.titleMedium)
            Text("电脑重启后听写无声，可在这里检查或修复流转使用的虚拟音频设备。不会修改 Windows 默认扬声器、音量或静音。",
                style = MaterialTheme.typography.bodySmall)
            when {
                !connected -> Text("先在连接页连接电脑；素材连接正常后即可检查，无需再扫麦克风码。")
                !supported -> Text("当前电脑端尚不支持手机检查音频，请先更新电脑端流转。")
            }
            val reply = result
            if (reply != null) {
                Text(if (reply.isCurrent()) reply.message else "电脑连接已变化，请重新检查音频。",
                    color = if (reply.ready) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant)
                if (reply.isCurrent() && reply.ready && !microphoneActive && !connecting)
                    Text("电脑配置已就绪；若麦克风尚未开启，可点下方“开启当前电脑麦克风”。", style = MaterialTheme.typography.bodySmall)
            }
            if (working) {
                LinearProgressIndicator(Modifier.fillMaxWidth())
                Text("正在检查电脑音频…", style = MaterialTheme.typography.bodySmall)
            }
            Button(onClick = { request(true) }, enabled = connected && supported && !working && !connecting && !microphoneActive,
                modifier = Modifier.fillMaxWidth()) { Text("检查并修复电脑音频") }
            TextButton(onClick = { request(false) }, enabled = connected && supported && !working && !connecting,
                modifier = Modifier.fillMaxWidth()) { Text("仅检查 · 不改配置") }
            if (microphoneActive) Text("当前麦克风会话仍在运行；可仅检查。需要修复路由时，请先结束当前会话。", style = MaterialTheme.typography.bodySmall)
            Text("修复成功后会沿现有授权流程连接麦克风。设备不唯一或驱动缺失时会说明下一步，不会替你安装驱动或强选设备。",
                style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            if (error.isNotBlank()) Text(error, color = MaterialTheme.colorScheme.error)
        }
    }
}
