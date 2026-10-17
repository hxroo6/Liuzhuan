package com.liuzhuan.app.audio

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle

/** Own recomposition boundary: 10Hz real levels never invalidate the connection/material page. */
@Composable
fun MicrophoneMiniControl(onOpen: () -> Unit) {
    val status by MicrophoneState.status.collectAsStateWithLifecycle()
    val levels by MicrophoneLevels.state.collectAsStateWithLifecycle()
    val label = when {
        !status.active -> "麦克风"
        status.localOnly -> "本机试音"
        !status.connected -> "连接收音"
        status.paused -> "暂停发送"
        status.quiet -> "安静待命"
        status.forwarding -> "正在传音"
        else -> "采音待命"
    }
    Surface(color = if (status.active) MaterialTheme.colorScheme.primaryContainer else MaterialTheme.colorScheme.surfaceVariant,
        shape = MaterialTheme.shapes.medium,
        modifier = Modifier.padding(start = 12.dp).width(94.dp).heightIn(min = 48.dp)
            .semantics(mergeDescendants = true) { contentDescription = "$label，点击打开手机麦克风" }
            .clickable(role = Role.Button, onClickLabel = "打开手机麦克风", onClick = onOpen)) {
        Column(Modifier.padding(horizontal = 10.dp, vertical = 5.dp), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            MicrophoneBars(levels, status.active, Modifier.fillMaxWidth().height(20.dp), compact = true)
            Text(label, style = MaterialTheme.typography.labelSmall,
                color = if (status.active) MaterialTheme.colorScheme.onPrimaryContainer else MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}
