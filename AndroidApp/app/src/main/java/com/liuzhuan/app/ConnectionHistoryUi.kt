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
    onClear: () -> Unit,
    onRename:(ConnectionProfile,String)->Unit={_,_->}
) {
    var confirmClear by remember { mutableStateOf(false) }
    var renaming by remember {mutableStateOf<ConnectionProfile?>(null)}
    var alias by remember {mutableStateOf("")}
    val dateFormat = remember { SimpleDateFormat("MM-dd HH:mm", Locale.CHINA) }
    AlertDialog(onDismissRequest = { if (!busy) onDismiss() }, title = { Text("历史连接") },
        confirmButton = { TextButton(enabled = !busy, onClick = onDismiss) { Text("关闭") } },
        dismissButton = {
            if (state.entries.isNotEmpty() || state.unavailable) TextButton(enabled = !busy, onClick = { confirmClear = true }) { Text("清空历史") }
        }, text = {
            Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                Text("成功连接后按设备归并，最多20台。换网时先查找并验证原电脑；口令加密保存在本机。旧版本仅记录地址。",
                    style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                if (state.unavailable) Text("历史记录暂不可读取，可清空后重新连接。", color = MaterialTheme.colorScheme.error)
                else if (state.entries.isEmpty()) Text("还没有历史连接。成功连接电脑后会自动记录。")
                LazyColumn(Modifier.heightIn(max = 420.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                    items(state.entries, key = { it.id }) { profile ->
                        Surface(shape = MaterialTheme.shapes.medium, color = MaterialTheme.colorScheme.surfaceContainerHigh) {
                            Column(Modifier.fillMaxWidth().padding(12.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                                Text(profile.title, style = MaterialTheme.typography.titleSmall)
                                Text("${profile.host}:${profile.port}"+if(profile.deviceId.isBlank())" · 旧地址记录" else " · ${profile.deviceId.take(8)}",style=MaterialTheme.typography.bodySmall)
                                if(profile.addresses.size>1)Text("已归并 ${profile.addresses.size} 个历史地址",style=MaterialTheme.typography.bodySmall)
                                Text("上次连接 ${dateFormat.format(Date(profile.lastConnected))}", style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant)
                                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                                    Button(enabled = !busy, onClick = { onConnect(profile) }) { Text(if (busy) "处理中…" else "连接") }
                                    TextButton(enabled=!busy,onClick={renaming=profile;alias=profile.alias}){Text("备注名")}
                                    TextButton(enabled = !busy, onClick = { onDelete(profile) }) { Text("删除") }
                                }
                            }
                        }
                    }
                }
            }
        })
    renaming?.let {profile->AlertDialog(onDismissRequest={renaming=null},title={Text("电脑备注名")},text={OutlinedTextField(alias,{if(it.length<=64)alias=it},placeholder={Text(profile.name.ifBlank {profile.endpoint})},singleLine=true)},confirmButton={TextButton(onClick={onRename(profile,alias);renaming=null}){Text("保存")}},dismissButton={TextButton(onClick={renaming=null}){Text("取消")}})}
    if (confirmClear) AlertDialog(onDismissRequest = { confirmClear = false }, title = { Text("清空历史连接？") },
        text = { Text("删除保存的历史地址和口令，不会断开当前连接或删除素材。") },
        confirmButton = { TextButton(onClick = { onClear(); confirmClear = false }) { Text("清空") } },
        dismissButton = { TextButton(onClick = { confirmClear = false }) { Text("取消") } })
}
