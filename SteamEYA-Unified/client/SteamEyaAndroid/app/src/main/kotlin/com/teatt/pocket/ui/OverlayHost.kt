package com.teatt.pocket.ui

import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.runtime.staticCompositionLocalOf

/**
 * RootScreen 级全屏浮层。
 *
 * 底部玻璃栏画在 RootScreen 最上层，页面内部的任何全屏覆盖（比如云账号登录抓取页）
 * 都会被它压住。所以全屏页不放在页面里，而是投递到这里，由 RootScreen 在栏之上渲染。
 */
class OverlayHostState {

    var content by mutableStateOf<(@Composable () -> Unit)?>(null)
        private set

    fun show(block: @Composable () -> Unit) {
        content = block
    }

    fun clear() {
        content = null
    }
}

val LocalOverlayHost = staticCompositionLocalOf<OverlayHostState?> { null }
