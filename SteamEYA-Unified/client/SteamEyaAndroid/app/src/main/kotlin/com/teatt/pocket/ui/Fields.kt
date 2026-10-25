package com.teatt.pocket.ui

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.runtime.Composable

/**
 * 输入框配色：聚焦时用品牌粉描边，背景半透明。
 *
 * 从 feature/LoginScreen.kt 提出来，给主登录页和「小工具」的 WiFi 登录页共用，
 * 保证两个登录界面的输入框观感完全一致。
 */
@Composable
fun fieldColors() = OutlinedTextFieldDefaults.colors(
    focusedBorderColor = BrandPink,
    unfocusedBorderColor = MaterialTheme.colorScheme.outline,
    focusedContainerColor = MaterialTheme.colorScheme.surface.copy(alpha = 0.6f),
    unfocusedContainerColor = MaterialTheme.colorScheme.surface.copy(alpha = 0.45f)
)
