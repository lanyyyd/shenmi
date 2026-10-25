package com.teatt.pocket.feature.wireguard

import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties

/**
 * 「小工具」面板的状态。
 *
 * 只有一个入口，但内部有两态：没登录先登录，登录了直接进连接面板。
 */
sealed interface WgUiState {
    data object Login : WgUiState
    data object Panel : WgUiState
}

/**
 * 「小工具」的全屏宿主。
 *
 * 关键点：关闭这个 Dialog **只关界面**，不碰 [WgService]，
 * 所以隧道继续跑，连接保持。
 */
@Composable
fun WgToolsHost(
    onDismiss: () -> Unit,
    modifier: Modifier = Modifier
) {
    var uiState by remember { mutableStateOf<WgUiState>(initialState()) }

    Dialog(
        onDismissRequest = onDismiss,
        properties = DialogProperties(
            usePlatformDefaultWidth = false,
            decorFitsSystemWindows = false
        ),
        modifier = modifier.fillMaxSize()
    ) {
        when (uiState) {
            WgUiState.Login -> WgLoginScreen(
                onBack = onDismiss,
                onLoggedIn = { uiState = WgUiState.Panel }
            )

            WgUiState.Panel -> WgSheet(
                onBack = onDismiss
            )
        }
    }
}

private fun initialState(): WgUiState =
    if (WgSession.loggedIn) WgUiState.Panel else WgUiState.Login
