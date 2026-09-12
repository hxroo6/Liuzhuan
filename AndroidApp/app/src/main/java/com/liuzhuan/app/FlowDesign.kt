package com.liuzhuan.app

import androidx.compose.foundation.background
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.tween
import androidx.compose.animation.expandVertically
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.shrinkVertically
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.stateDescription
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
        Spacer(Modifier.width(12.dp))
        Surface(shape = RoundedCornerShape(18.dp), color = Color(0xFF31594F),
            border = BorderStroke(1.dp, Color(0xFF4F7869))) {
            Box(Modifier.size(52.dp), contentAlignment = Alignment.Center) {
                FlowNavIcon(page, selected = true, modifier = Modifier.size(28.dp))
            }
        }
    }
}

// Compose animations use the platform MotionDurationScale, including scale = 0.
// Only the incoming page is composed; switching never duplicates lifecycle effects.
@Composable
fun Modifier.flowPageMotion(page: Int): Modifier {
    var previous by remember { mutableIntStateOf(page) }
    val direction = remember(page) { (page - previous).coerceIn(-1, 1) }
    val arrival = remember(page) { Animatable(if (previous == page) 1f else 0f) }
    LaunchedEffect(page) {
        previous = page
        arrival.animateTo(1f, tween(220, easing = FastOutSlowInEasing))
    }
    return graphicsLayer {
        alpha = .8f + .2f * arrival.value
        translationX = direction * 12.dp.toPx() * (1f - arrival.value)
    }
}

@Composable
fun FlowReveal(visible: Boolean, content: @Composable ColumnScope.() -> Unit) {
    AnimatedVisibility(visible,
        enter = expandVertically(tween(220), expandFrom = Alignment.Top) + fadeIn(tween(160)),
        exit = shrinkVertically(tween(180), shrinkTowards = Alignment.Top) + fadeOut(tween(120))) {
        Column(Modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(10.dp), content = content)
    }
}

@Composable
fun FlowDisclosure(expanded: Boolean, expandedLabel: String, collapsedLabel: String, onClick: () -> Unit) {
    val rotation by animateFloatAsState(if (expanded) 180f else 0f, tween(180), label = "disclosure")
    TextButton(onClick = onClick, modifier = Modifier.fillMaxWidth().semantics {
        stateDescription = if (expanded) "已展开" else "已收起"
    }, contentPadding = PaddingValues(horizontal = 0.dp, vertical = 8.dp)) {
        Text(if (expanded) expandedLabel else collapsedLabel, modifier = Modifier.weight(1f))
        val tint = LocalContentColor.current
        Canvas(Modifier.size(20.dp).graphicsLayer { rotationZ = rotation }) {
            val stroke = 1.7.dp.toPx()
            drawLine(tint, Offset(size.width * .25f, size.height * .4f), Offset(size.width * .5f, size.height * .65f), stroke, StrokeCap.Round)
            drawLine(tint, Offset(size.width * .5f, size.height * .65f), Offset(size.width * .75f, size.height * .4f), stroke, StrokeCap.Round)
        }
    }
}

@Composable
fun FlowNavIcon(page: Int, selected: Boolean, modifier: Modifier = Modifier.size(24.dp)) {
    val color by animateColorAsState(if (selected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant,
        tween(160), label = "navigation tint")
    Canvas(modifier) {
        val stroke = 1.8.dp.toPx()
        fun line(x1: Float, y1: Float, x2: Float, y2: Float) = drawLine(color,
            Offset(size.width * x1, size.height * y1), Offset(size.width * x2, size.height * y2), stroke, StrokeCap.Round)
        when (page) {
            0 -> {
                line(.15f,.32f,.84f,.32f); line(.65f,.13f,.84f,.32f); line(.65f,.51f,.84f,.32f)
                line(.85f,.7f,.16f,.7f); line(.35f,.51f,.16f,.7f); line(.35f,.89f,.16f,.7f)
            }
            1 -> {
                line(.5f,.68f,.5f,.14f); line(.27f,.37f,.5f,.14f); line(.73f,.37f,.5f,.14f)
                line(.18f,.64f,.18f,.86f); line(.18f,.86f,.82f,.86f); line(.82f,.86f,.82f,.64f)
            }
            else -> {
                line(.5f,.13f,.5f,.66f); line(.27f,.44f,.5f,.66f); line(.73f,.44f,.5f,.66f)
                line(.18f,.64f,.18f,.86f); line(.18f,.86f,.82f,.86f); line(.82f,.86f,.82f,.64f)
            }
        }
    }
}

@Composable
fun FlowProgressIndicator(progress: Float, modifier: Modifier = Modifier, running: Boolean = true) {
    val target = progress.coerceIn(0f, 1f)
    val smooth by animateFloatAsState(target, tween(180), label = "transfer progress")
    // Completed/failed rows immediately show the real final count, without a trailing animation.
    LinearProgressIndicator(progress = { if (running) smooth else target }, modifier = modifier)
}
