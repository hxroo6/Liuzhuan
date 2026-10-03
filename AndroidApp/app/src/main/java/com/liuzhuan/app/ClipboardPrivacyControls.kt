package com.liuzhuan.app

import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.liuzhuan.app.clipboard.ClipboardPrivacy
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/** 自动同步的设置就地展开，不创建额外页面或持续动效。 */
@Composable
fun ClipboardPrivacyControls(settingsReady: Boolean, onEnabledChange: (Boolean) -> Unit) {
    val privacy by ClipboardPrivacy.state.collectAsStateWithLifecycle()
    val loadError by ClipboardPrivacy.loadError.collectAsStateWithLifecycle()
    var showExcluded by rememberSaveable { mutableStateOf(false) }
    var excludedText by rememberSaveable { mutableStateOf(ClipboardPrivacy.exclusionText()) }
    var feedback by remember { mutableStateOf("") }
    var feedbackError by remember { mutableStateOf(false) }
    var saving by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()
    val paused = privacy.paused(System.currentTimeMillis())
    val canChange = settingsReady && privacy.ready && !saving && loadError.isEmpty()

    fun applyRule(success: String, afterSaved: () -> Unit = {}, operation: () -> Unit) {
        if (saving) return
        // 点击回调中先互斥，避免两个快速操作同时进入落盘过程。
        saving = true
        scope.launch {
            try {
                withContext(Dispatchers.IO) { operation() }
                afterSaved()
                feedbackError = false; feedback = success
            } catch (ex: CancellationException) { throw ex }
            catch (ex: Exception) { feedbackError = true; feedback = ex.message ?: "设置未保存，请重试" }
            finally { saving = false }
        }
    }

    // 一次到期刷新；后台规则本身按时间判断，既不读剪贴板也不轮询。
    LaunchedEffect(privacy.pausedUntilMs) {
        if (privacy.pausedUntilMs > 0) {
            delay((privacy.pausedUntilMs - System.currentTimeMillis()).coerceAtLeast(0))
            // 到期本身按策略时间自动生效；UI 只在没有其他保存时清理过期元数据。
            if (!saving && loadError.isEmpty()) {
                saving = true
                try { withContext(Dispatchers.IO) { ClipboardPrivacy.refreshExpiredPause() } }
                catch (ex: CancellationException) { throw ex }
                catch (ex: Exception) { feedbackError = true; feedback = ex.message ?: "到期状态未能保存，请重试" }
                finally { saving = false }
            }
        }
    }
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text("复制内容自动同步", style = MaterialTheme.typography.bodyMedium)
                Text("LSPosed 支持后台复制；无障碍用于前台与选中文字", style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            Switch(checked = privacy.automaticEnabled, onCheckedChange = { checked ->
                applyRule(if (checked) "自动同步已开启" else "自动同步已关闭", afterSaved = { onEnabledChange(checked) }) {
                    ClipboardPrivacy.setAutomaticEnabled(checked)
                }
            }, enabled = canChange)
        }
        Text(when {
            !privacy.automaticEnabled -> "已关闭 · 手动发送仍可使用"
            paused && privacy.pausedUntilMs == -1L -> "已暂停 · 等待手动恢复"
            paused -> "暂停至 ${SimpleDateFormat("HH:mm", Locale.getDefault()).format(Date(privacy.pausedUntilMs))} · 到期自动恢复"
            else -> "自动同步已开启 · 只发送允许的来源"
        }, style = MaterialTheme.typography.bodySmall,
            color = if (paused) MaterialTheme.colorScheme.tertiary else MaterialTheme.colorScheme.primary)
        if (paused) {
            FilledTonalButton(onClick = { applyRule("已结束暂停，只同步新复制的内容") { ClipboardPrivacy.resume() } },
                enabled = canChange, modifier = Modifier.fillMaxWidth()) {
                Text(if (privacy.automaticEnabled) "恢复自动同步" else "结束暂停")
            }
        } else {
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.fillMaxWidth()) {
                listOf("暂停" to null, "15 分钟" to 15 * 60_000L, "1 小时" to 60 * 60_000L).forEach { (label, duration) ->
                    OutlinedButton(onClick = { applyRule("已暂停，待发送的自动缓存已丢弃") { ClipboardPrivacy.pause(duration) } },
                        enabled = canChange && privacy.automaticEnabled, modifier = Modifier.weight(1f), contentPadding = PaddingValues(horizontal = 8.dp, vertical = 10.dp)) { Text(label) }
                }
            }
        }
        Text("只控制自动复制。手动分享、发送文件和文本选择菜单仍可用。调整规则会丢弃待发送自动缓存；已提交的传输继续。",
            style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        FlowDisclosure(showExcluded, "收起来源排除", "来源排除 · ${privacy.excludedPackages.size} 个应用") { showExcluded = !showExcluded }
        FlowReveal(showExcluded) {
            OutlinedTextField(value = excludedText, onValueChange = { excludedText = it; feedback = "" },
                label = { Text("不自动同步的应用包名") },
                placeholder = { Text("com.tencent.mm\ncom.android.chrome") },
                enabled = !saving, minLines = 3, maxLines = 6, modifier = Modifier.fillMaxWidth())
            Text("每行一个完整包名，也可用逗号分隔；空白表示不排除。LSPosed 能提供写入应用，选中文字使用无障碍事件来源。启用排除后，无法确认来源的剪贴板重读会跳过。",
                style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            OutlinedButton(onClick = {
                val rulesToSave = excludedText
                applyRule("排除规则已保存") { ClipboardPrivacy.saveExcludedPackages(rulesToSave) }
            }, enabled = settingsReady && privacy.ready && !saving, modifier = Modifier.fillMaxWidth()) { Text("保存排除规则") }
        }
        if (saving) Text("正在保存隐私设置…", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        if (loadError.isNotEmpty()) Text(loadError, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error)
        if (feedback.isNotEmpty()) Text(feedback, style = MaterialTheme.typography.bodySmall,
            color = if (feedbackError) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.primary)
    }
}
