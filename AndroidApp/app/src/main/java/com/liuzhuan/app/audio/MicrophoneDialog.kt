package com.liuzhuan.app.audio

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import com.liuzhuan.app.SecondaryPanel
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import org.json.JSONObject
import kotlinx.coroutines.launch
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.catch

@Composable
fun MicrophoneDialog(initialPayload: String = "", onConnectCurrentComputer: () -> Unit = {}, connecting: Boolean = false, onDismiss: () -> Unit) {
    val context = LocalContext.current
    val state by MicrophoneState.status.collectAsStateWithLifecycle()
    val levels by MicrophoneLevels.state.collectAsStateWithLifecycle()
    var pending by remember { mutableStateOf("") }
    var target by remember { mutableStateOf("") }
    var error by remember { mutableStateOf("") }
    var confirmed by remember { mutableStateOf(false) }
    var starting by remember { mutableStateOf(false) }
    var localStart by remember { mutableStateOf(false) }
    val optionsStore = remember { MicrophoneOptionsStore(context) }
    val optionsFlow = remember(optionsStore) {
        optionsStore.options.map<MicrophoneOptions, MicrophoneOptions?> { it }.catch { emit(null) }
    }
    val options by optionsFlow.collectAsStateWithLifecycle(null)
    val optionsScope = rememberCoroutineScope()
    var savingOptions by remember { mutableStateOf(false) }
    var savedMessage by remember { mutableStateOf("") }
    fun saveOptions(value: MicrophoneOptions) {
        if (savingOptions) return
        savingOptions = true; savedMessage = ""; error = ""
        val write = optionsStore.save(value)
        optionsScope.launch {
            try { write.await(); savedMessage = "已保存，下次开启会话生效" }
            catch (ex: kotlinx.coroutines.CancellationException) { throw ex }
            catch (_: Exception) { error = "麦克风设置未保存，请重试" }
            finally { savingOptions = false }
        }
    }
    LaunchedEffect(initialPayload) {
        if (initialPayload.isNotBlank()) runCatching {
            pending = AudioPairing.microphone(initialPayload).orEmpty()
            val json = JSONObject(pending)
            target = "${json.getString("ip")}:${json.getInt("port")}"
            confirmed = false; error = ""
        }.onFailure { error = "麦克风邀请无效，请在电脑重新生成二维码" }
    }
    LaunchedEffect(state) { if (state.active || state.message != "麦克风已关闭") starting = false }
    fun startCapture() {
        try {
            starting = true; error = ""
            val action = if (localStart) MicrophoneService.START_LOCAL else MicrophoneService.START
            val payload = pending
            optionsScope.launch {
                try {
                    val saved = optionsStore.current()
                    ContextCompat.startForegroundService(context, Intent(context, MicrophoneService::class.java)
                        .setAction(action).putExtra("payload", payload)
                        .putExtra("suppressSilence", saved.suppressSilence).putExtra("thresholdDb", saved.thresholdDb))
                } catch (ex: kotlinx.coroutines.CancellationException) { throw ex }
                catch (_: Exception) { starting = false; error = "无法读取设置或启动服务，请在前台重试" }
            }
        } catch (_: Exception) { starting = false; error = "服务未能启动，请在前台授权麦克风后重试" }
    }
    val permission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted && (localStart || confirmed && pending.isNotBlank())) startCapture()
        else error = "需要麦克风权限；可在系统应用设置中允许后重试"
    }
    SecondaryPanel(onDismiss = onDismiss) {
            Column(Modifier.verticalScroll(rememberScrollState()).padding(22.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
                Text("手机麦克风", style = MaterialTheme.typography.headlineMedium)
                Text("让电脑使用手机收音，仍由 Codex 原听写识别。", color = MaterialTheme.colorScheme.onSurfaceVariant)
                Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.secondaryContainer)) {
                    Column(Modifier.fillMaxWidth().padding(18.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        Text(state.message, style = MaterialTheme.typography.titleMedium)
                        if (state.active) Text((if (state.localOnly) "试音剩余 ${state.secondsLeft}秒" else "持续待命 · 直到你结束会话") + " · 已采集 ${state.capturedFrames} 帧 · 已发送 ${state.frames} 帧")
                        if (state.active && !state.localOnly) Text(if (state.suppression) {
                            "安静省流量 · 已省略 ${state.suppressedFrames} 帧" + if (!state.forwarding) " · 等待电脑请求" else if (state.quiet) " · 当前仅心跳" else " · 检测到声音"
                        } else "连续传输 · 若已选择省流量但仍显示连续，请同时更新电脑端")
                        Text(if (state.active) "下拉或返回只关闭面板，采音继续；结束请用下方按钮或通知栏。" else "麦克风默认关闭；连接页“扫码连接全部功能”会在授权后开启采音，也可点下方连接当前电脑。")
                    }
                }
                MicrophoneWaveform(levels, state.active)
                Card {
                    Column(Modifier.fillMaxWidth().padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        Text("麦克风偏好", style = MaterialTheme.typography.titleMedium)
                        val saved = options
                        if (saved == null) Text("设置尚未就绪；若长时间未显示，请关闭面板后重试。") else {
                            Row {
                                Checkbox(checked = saved.suppressSilence, enabled = !savingOptions,
                                    onCheckedChange = { saveOptions(saved.copy(suppressSilence = it)) })
                                Text("安静省流量：有声音才发音频，安静时仅心跳", Modifier.padding(top = 10.dp))
                            }
                            if (saved.suppressSilence) {
                                Text("小声说话选灵敏；嘈杂环境选抗噪。只检测音量，不判断人声。", style = MaterialTheme.typography.bodySmall)
                                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                                    listOf(-48 to "灵敏", -42 to "标准", -36 to "抗噪").forEach { (value, label) ->
                                        FilterChip(selected = saved.thresholdDb == value, enabled = !savingOptions,
                                            onClick = { saveOptions(saved.copy(thresholdDb = value)) }, label = { Text(label) })
                                    }
                                }
                            }
                            Text(if (savingOptions) "正在保存…" else savedMessage.ifBlank { "自动保存；扫码、连接当前电脑与本机试音共用此设置。下次开启会话生效。" }, style = MaterialTheme.typography.bodySmall)
                            if (state.active) Text(if (state.localOnly) "当前为本机试音，不发送音频。" else if (state.suppression) "当前会话：安静省流量 · ${MicrophoneOptions(thresholdDb = state.thresholdDb).thresholdLabel}" else "当前会话：连续传输", style = MaterialTheme.typography.bodySmall)
                        }
                    }
                }
                if (!state.active && !starting) {
                    OutlinedButton(onClick = {
                        localStart = true
                        if (ContextCompat.checkSelfPermission(context, Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED) startCapture()
                        else permission.launch(Manifest.permission.RECORD_AUDIO)
                    }, enabled = options != null && !savingOptions, modifier = Modifier.fillMaxWidth()) { Text("本机试音 60 秒 · 不连接电脑") }
                    Button(onClick = onConnectCurrentComputer, enabled = !connecting && options != null && !savingOptions, modifier = Modifier.fillMaxWidth()) {
                        Text(if (connecting) "正在连接麦克风…" else "连接当前电脑麦克风 · 不用再扫码")
                    }
                    Text("连接页扫一次码即会完成素材与麦克风握手。这里可直接重新连接；返回连接页后，音波图缩到左上角，点击即可展开。", style = MaterialTheme.typography.bodySmall)
                    if (pending.isNotBlank()) {
                        Text("目标电脑：$target")
                        Row {
                            Checkbox(checked = confirmed, onCheckedChange = { confirmed = it })
                            Text("允许这次局域网明文传输。开启后手机会持续采音待命，电脑可请求转发。", Modifier.padding(top = 10.dp))
                        }
                        Button(enabled = confirmed && options != null && !savingOptions, onClick = {
                            localStart = false
                            if (ContextCompat.checkSelfPermission(context, Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED) startCapture()
                            else permission.launch(Manifest.permission.RECORD_AUDIO)
                        }, modifier = Modifier.fillMaxWidth()) { Text("开启麦克风 · 持续待命") }
                    }
                }
                if (starting) { LinearProgressIndicator(Modifier.fillMaxWidth()); Text("正在启动，请稍候…") }
                if (state.active) {
                    if (!state.localOnly) {
                        OutlinedButton(enabled = !state.paused, onClick = { context.startService(Intent(context, MicrophoneService::class.java).setAction(MicrophoneService.PAUSE)) }, modifier = Modifier.fillMaxWidth()) { Text(if (state.paused) "已暂停传输 · 仍会采音" else "暂停传输 · 仍会采音") }
                        if (state.paused) TextButton(onClick = { context.startService(Intent(context, MicrophoneService::class.java).setAction(MicrophoneService.RESUME)) }) { Text("允许电脑再次请求转发") }
                        if (!state.forwarding) Text(if (state.paused) "先允许电脑再次请求，再在电脑点击“开始转发”；不会自动恢复发送。" else "正在采音待命；在电脑点击“开始转发”即可恢复发送，无需扫码。", style = MaterialTheme.typography.bodySmall)
                    }
                    Button(onClick = { context.startService(Intent(context, MicrophoneService::class.java).setAction(MicrophoneService.END)) }, modifier = Modifier.fillMaxWidth()) { Text("结束会话 · 释放麦克风") }
                }
                if (error.isNotBlank()) Text(error, color = MaterialTheme.colorScheme.error)
                Text("需同一局域网或手机热点。开启后持续待命，没有闲置或15分钟限制；断线、撤权或系统阻断采音时结束。安静时仍需采音检测，不能完全消除功耗；本机试音仍限60秒。", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                TextButton(onClick = onDismiss, modifier = Modifier.fillMaxWidth()) { Text("返回流转") }
            }
    }
}
