package com.liuzhuan.app

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

private val FlowColors = darkColorScheme(
    primary = Color(0xFF85E2CD), onPrimary = Color(0xFF073A31),
    primaryContainer = Color(0xFF204A43), onPrimaryContainer = Color(0xFFB8F3E3),
    secondary = Color(0xFFE6C58C), onSecondary = Color(0xFF3C2E16),
    secondaryContainer = Color(0xFF344841), onSecondaryContainer = Color(0xFFC4F0DF),
    background = Color(0xFF101D21), onBackground = Color(0xFFEDF6F2),
    surface = Color(0xFF192C30), onSurface = Color(0xFFEDF6F2),
    surfaceVariant = Color(0xFF22383D), onSurfaceVariant = Color(0xFFB2C8C5),
    outline = Color(0xFF607A77), outlineVariant = Color(0xFF304A4D),
    error = Color(0xFFFFB4AB)
)

@Composable
fun FlowTheme(content: @Composable () -> Unit) {
    MaterialTheme(colorScheme = FlowColors,
        shapes = Shapes(small = RoundedCornerShape(12.dp), medium = RoundedCornerShape(18.dp), large = RoundedCornerShape(24.dp)),
        content = content)
}

@Composable
fun FlowIntro(page: Int, connected: Boolean, state: String) {
    val titles = listOf("让设备，连在一起", "把灵感，送到电脑", "你的素材，随时取用")
    val subtitles = listOf("同一 Wi-Fi 下，扫码或搜索即可开始", "文字、照片与文件，都有一条近路", "来自电脑的素材，在这里实时汇合")
    Row(Modifier.fillMaxWidth().background(
        Brush.linearGradient(listOf(Color(0xFF24483F), Color(0xFF192E32))), RoundedCornerShape(24.dp)
    ).padding(20.dp), verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text("LIUZHUAN / ${listOf("CONNECT", "SEND", "COLLECT")[page]}", fontSize = 10.sp,
                letterSpacing = 2.sp, color = MaterialTheme.colorScheme.secondary)
            Text(titles[page], style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold)
            Text(subtitles[page], style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Text(if (connected) "● 已连接 · 局域网直传" else "○ $state", style = MaterialTheme.typography.labelMedium,
                color = if (connected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant)
        }
        Text("⇄", fontSize = 36.sp, color = MaterialTheme.colorScheme.primary, modifier = Modifier.padding(start = 12.dp))
    }
}
