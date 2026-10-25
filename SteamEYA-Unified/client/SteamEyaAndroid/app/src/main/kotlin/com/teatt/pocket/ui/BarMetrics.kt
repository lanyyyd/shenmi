package com.teatt.pocket.ui

import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.asPaddingValues
import androidx.compose.foundation.layout.calculateBottomPadding
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.runtime.Composable
import androidx.compose.runtime.compositionLocalOf
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp

/** 玻璃胶囊本体高度。 */
val BarCoreHeight: Dp = 66.dp

/** 胶囊与系统导航栏之间的留白。 */
val BarBottomGap: Dp = 14.dp

/**
 * 底部玻璃栏占用的总高度 = 胶囊 + 留白 + 系统导航栏。
 *
 * 内容层**不做** padding（玻璃要采样栏下方的像素，内容必须画到底），
 * 但原生列表用它做 `contentPadding.bottom`，WebView 用它换算成
 * CSS 变量 `--tt-bar` 交给网页，由网页自己把底部元素抬上来。
 */
val LocalBarInset = compositionLocalOf { 0.dp }

@Composable
fun rememberBarInset(): Dp {
    val navBottom = WindowInsets.navigationBars.asPaddingValues().calculateBottomPadding()
    return BarCoreHeight + BarBottomGap + navBottom
}
