package com.liuzhuan.app.audio

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.layout.*
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import kotlin.math.sqrt

@Composable
fun MicrophoneWaveform(snapshot: MicLevelSnapshot, active: Boolean) {
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Text("手机实际采音", style = MaterialTheme.typography.titleSmall)
        MicrophoneBars(snapshot, active, Modifier.fillMaxWidth().height(72.dp).semantics { contentDescription = "麦克风实时音量波形" })
        Text(when {
            !active -> "麦克风未采集"
            !snapshot.capturing -> "等待第一帧采音…"
            snapshot.level.peak == 0f -> "采集帧已到达，当前振幅为零"
            else -> "正在采集 · ${snapshot.level.dbfs.toInt()} dBFS · 峰值 ${(snapshot.level.peak * 100).toInt()}%"
        }, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

@Composable
internal fun MicrophoneBars(snapshot: MicLevelSnapshot, active: Boolean, modifier: Modifier, compact: Boolean = false) {
    val color = MaterialTheme.colorScheme.primary
    val inactive = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.25f)
    val history = if (compact) snapshot.history.takeLast(18) else snapshot.history
    Canvas(modifier) {
        val step = size.width / history.size.coerceAtLeast(1)
        history.forEachIndexed { i, peak ->
            val height = if (active) (sqrt(peak.coerceIn(0f, 1f)) * size.height).coerceAtLeast(2.dp.toPx()) else 2.dp.toPx()
            drawRoundRect(if (active && peak > 0f) color else inactive,
                topLeft = Offset(i * step + step * 0.2f, (size.height - height) / 2),
                size = Size(step * 0.6f, height), cornerRadius = CornerRadius(step * 0.3f))
        }
    }
}
