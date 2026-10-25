package com.teatt.pocket.feature

import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Cloud
import androidx.compose.material.icons.filled.Forum
import androidx.compose.material.icons.filled.Person
import androidx.compose.material.icons.filled.Restaurant
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.zIndex
import com.teatt.pocket.core.Session
import com.teatt.pocket.core.SessionState
import com.teatt.pocket.feature.cloud.CloudScreen
import com.teatt.pocket.feature.profile.ProfileScreen
import com.teatt.pocket.ui.LiquidBottomBar
import com.teatt.pocket.ui.TabSpec
import com.teatt.pocket.ui.WebPane
import com.kyant.backdrop.backdrops.layerBackdrop
import com.kyant.backdrop.backdrops.rememberLayerBackdrop
import androidx.compose.runtime.collectAsState

private val TABS = listOf(
    TabSpec(Icons.Filled.Forum, "聊天"),
    TabSpec(Icons.Filled.Restaurant, "吃什么"),
    TabSpec(Icons.Filled.Cloud, "云账号"),
    TabSpec(Icons.Filled.Person, "我的")
)

/**
 * 主界面：底部液态玻璃功能栏 + 四个常驻页面。
 *
 * 四个页面都不销毁（用 alpha 切换），因为聊天和"吃什么"是 WebView——
 * 一旦被移出组合就会重新加载，那点淡入动画换一次白屏不划算。
 */
@Composable
fun RootScreen(
    onLoggedOut: () -> Unit
) {
    val state by Session.state.collectAsState()
    val backdrop = rememberLayerBackdrop()
    var tab by remember { mutableIntStateOf(0) }

    // token 变了就换一套 autologin 地址，WebPane 检测到 url 变化会重新加载
    val chatUrl = remember(state.token) { Session.webUrl("/") }
    val eatUrl = remember(state.token) { Session.webUrl("/eat.html") }

    Box(Modifier.fillMaxSize()) {
        // 这一层会被底部玻璃栏采样，所以必须打上 layerBackdrop
        Box(
            Modifier
                .fillMaxSize()
                .layerBackdrop(backdrop)
        ) {
            TabPane(visible = tab == 0) {
                WebPane(url = chatUrl, visible = tab == 0, modifier = Modifier.fillMaxSize())
            }
            TabPane(visible = tab == 1) {
                WebPane(url = eatUrl, visible = tab == 1, modifier = Modifier.fillMaxSize())
            }
            TabPane(visible = tab == 2) {
                CloudScreen(token = state.token, modifier = Modifier.fillMaxSize())
            }
            TabPane(visible = tab == 3) {
                ProfileScreen(state = state, onLoggedOut = onLoggedOut, modifier = Modifier.fillMaxSize())
            }
        }

        LiquidBottomBar(
            backdrop = backdrop,
            selectedIndex = tab,
            tabs = TABS,
            onSelect = { tab = it },
            modifier = Modifier.align(Alignment.BottomCenter)
        )
    }
}

/**
 * 页面容器：淡入 + 轻微缩放过渡，隐藏时彻底不吃触摸事件。
 * 不做位移，避免 WebView 里的滚动位置观感错乱。
 */
@Composable
private fun TabPane(
    visible: Boolean,
    content: @Composable () -> Unit
) {
    val alpha by animateFloatAsState(
        targetValue = if (visible) 1f else 0f,
        animationSpec = tween(durationMillis = 200),
        label = "paneAlpha"
    )
    val scale by animateFloatAsState(
        targetValue = if (visible) 1f else 0.985f,
        animationSpec = tween(durationMillis = 260),
        label = "paneScale"
    )

    Box(
        Modifier
            .fillMaxSize()
            .zIndex(if (visible) 1f else 0f)
            .graphicsLayer {
                this.alpha = alpha
                scaleX = scale
                scaleY = scale
            }
            .background(MaterialTheme.colorScheme.background)
            .then(if (visible) Modifier else Modifier.swallowTouches())
    ) {
        content()
    }
}

/** 隐藏页仍参与命中测试，必须显式吞掉事件，否则会点穿到底下的 WebView。 */
private fun Modifier.swallowTouches(): Modifier = pointerInput(Unit) {
    awaitPointerEventScope {
        while (true) {
            awaitPointerEvent(PointerEventPass.Initial).changes.forEach { it.consume() }
        }
    }
}
