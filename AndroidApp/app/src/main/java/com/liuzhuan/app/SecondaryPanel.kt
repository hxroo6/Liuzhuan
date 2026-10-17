package com.liuzhuan.app

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp

/** UI dismissal only. Capture and transfers remain owned by their existing services. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun SecondaryPanel(onDismiss: () -> Unit, content: @Composable () -> Unit) {
    ModalBottomSheet(
        onDismissRequest = onDismiss,
        sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true),
        modifier = Modifier.statusBarsPadding().fillMaxHeight(0.95f),
        sheetMaxWidth = 680.dp,
        containerColor = MaterialTheme.colorScheme.background
    ) {
        // The standard handle, velocity/position thresholds and nested scrolling cooperate
        // with verticalScroll/LazyColumn: downward content scrolling reaches the top first.
        Box(Modifier.fillMaxWidth().weight(1f)) { content() }
    }
}
