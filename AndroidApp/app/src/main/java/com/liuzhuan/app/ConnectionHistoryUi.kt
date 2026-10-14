package com.liuzhuan.app

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.liuzhuan.app.core.ConnectionHistoryStore
import com.liuzhuan.app.core.ConnectionProfile
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

@Composable fun ConnectionHistoryDialog(
    state: ConnectionHistoryStore.State,
    busy: Boolean,
    onDismiss: () -> Unit,
    onConnect: (ConnectionProfile) -> Unit,
    onDelete: (ConnectionProfile) -> Unit,
    onClear: () -> Unit
) {
    var confirmClear by remember { mutableStateOf(false) }
    val dateFormat = remember { SimpleDateFormat("MM-dd HH:mm", Locale.CHINA) }
    AlertDialog(onDismissRequest = { if (!busy) onDismiss() }, title = { Text("历史连接") },
        confirmButton = { TextButton(enabled = !busy, onClick = onDismiss) { Text("关闭") } },
        dismissButton = {
            if (state.entries.isNotEmpty() || state.unavailable) TextButton(enabled = !busy, onClick = { confirmClear = true }) { Text("清空历史") }
        }, text = {
            Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                Text("只记录成功连接的地址，最多保留 20 个。口令加密保存在本机；更换网络后地址可能变化，请重新扫码。",
                    style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                if (state.unavailable) Text("历史记录暂不可读取，可清空后重新连接。", color = MaterialTheme.colorScheme.error)
                else if (state.entries.isEmpty()) Text("还没有历史连接。成功连接电脑后会自动记录。")
                LazyColumn(Modifier.heightIn(max = 420.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                    items(state.entries, key = { it.id }) { profile ->
                        Surface(shape = MaterialTheme.shapes.medium, color = MaterialTheme.colorScheme.surfaceContainerHigh) {
                            Column(Modifier.fillMaxWidth().padding(12.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                                Text("${profile.host}:${profile.port}", style = MaterialTheme.typography.titleSmall)
                                Text("上次连接 ${dateFormat.format(Date(profile.lastConnected))}", style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant)
                                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                                    Button(enabled = !busy, onClick = { onConnect(profile) }) { Text(if (busy) "处理中…" else "连接") }
                                    TextButton(enabled = !busy, onClick = { onDelete(profile) }) { Text("删除") }
                                }
                            }
                        }
                    }
                }
            }
        })
    if (confirmClear) AlertDialog(onDismissRequest = { confirmClear = false }, title = { Text("清空历史连接？") },
        text = { Text("删除保存的历史地址和口令，不会断开当前连接或删除素材。") },
        confirmButton = { TextButton(onClick = { onClear(); confirmClear = false }) { Text("清空") } },
        dismissButton = { TextButton(onClick = { confirmClear = false }) { Text("取消") } })
}
