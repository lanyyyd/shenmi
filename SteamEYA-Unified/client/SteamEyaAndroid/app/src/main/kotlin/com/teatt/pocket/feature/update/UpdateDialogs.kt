package com.teatt.pocket.feature.update

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.teatt.pocket.core.AppVersion
import com.teatt.pocket.data.UpdateInfoDto

/**
 * 更新弹窗宿主，挂在 RootScreen 上。
 * 自动检查只在有新版本时弹，手动检查则总是给结果。
 */
@Composable
fun UpdateDialogHost(state: UpdateDialogState?, onDismiss: () -> Unit) {
    val context = LocalContext.current

    when (state) {
        is UpdateDialogState.Available -> {
            val info = state.info
            AlertDialog(
                onDismissRequest = onDismiss,
                title = { Text("发现新版本 ${info.version}") },
                text = {
                    Column(Modifier.heightIn(max = 320.dp).verticalScroll(rememberScrollState())) {
                        Text(
                            "当前 ${AppVersion.current} · ${formatSize(info.fileSize)}",
                            fontSize = 12.sp,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                        Text(
                            if (info.notes.isNotBlank()) info.notes.trim() else "修复问题并优化体验",
                            fontSize = 13.sp
                        )
                    }
                },
                confirmButton = {
                    TextButton(onClick = {
                        if (!UpdateCenter.download(context, info)) {
                            UpdateCenter.dismiss()
                        }
                    }) { Text("立即更新") }
                },
                dismissButton = {
                    TextButton(onClick = onDismiss) { Text("稍后") }
                }
            )
        }

        is UpdateDialogState.Message -> AlertDialog(
            onDismissRequest = onDismiss,
            title = { Text(state.title) },
            text = { Text(state.body) },
            confirmButton = { TextButton(onClick = onDismiss) { Text("好") } }
        )

        null -> Unit
    }
}

private fun formatSize(bytes: Long): String = when {
    bytes <= 0 -> "大小未知"
    bytes < 1024 -> "$bytes B"
    bytes < 1024 * 1024 -> "${bytes / 1024} KB"
    else -> String.format("%.1f MB", bytes / 1024.0 / 1024.0)
}
