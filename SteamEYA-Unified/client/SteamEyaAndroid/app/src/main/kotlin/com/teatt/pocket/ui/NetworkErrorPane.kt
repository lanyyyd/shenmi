package com.teatt.pocket.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.size
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.WifiOff
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

/**
 * 断网 / 加载失败时的整页占位：图标 + 提示语 + 重试按钮。
 *
 * 聊天（WebView 主文档加载失败）和云账号（接口拉取失败）共用，保证两处观感一致。
 */
@Composable
fun NetworkErrorPane(
    onRetry: () -> Unit,
    modifier: Modifier = Modifier,
    message: String = "网络连接失败，请检查网络后重试",
    backgroundColor: Color = MaterialTheme.colorScheme.background
) {
    Box(
        modifier.fillMaxSize().background(backgroundColor),
        contentAlignment = Alignment.Center
    ) {
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
            Icon(
                Icons.Filled.WifiOff,
                contentDescription = null,
                modifier = Modifier.size(34.dp),
                tint = MaterialTheme.colorScheme.onSurfaceVariant
            )
            Spacer(Modifier.height(10.dp))
            Text(
                message,
                fontSize = 13.sp,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                textAlign = TextAlign.Center
            )
            Spacer(Modifier.height(6.dp))
            TextButton(onClick = onRetry) {
                Text("重试")
            }
        }
    }
}
