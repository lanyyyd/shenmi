package com.teatt.pocket.feature

import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.ime
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Cloud
import androidx.compose.material.icons.filled.Forum
import androidx.compose.material.icons.filled.Person
import androidx.compose.material.icons.filled.Restaurant
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.dp
import androidx.compose.ui.zIndex
import com.kyant.backdrop.backdrops.layerBackdrop
import com.kyant.backdrop.backdrops.rememberLayerBackdrop
import com.teatt.pocket.core.Session
import com.teatt.pocket.feature.cloud.CloudScreen
import com.teatt.pocket.feature.profile.ProfileScreen
import com.teatt.pocket.feature.update.UpdateCenter
import com.teatt.pocket.feature.update.UpdateDialogHost
import com.teatt.pocket.ui.LiquidBottomBar
import com.teatt.pocket.ui.LocalBarInset
import com.teatt.pocket.ui.LocalOverlayHost
import com.teatt.pocket.ui.OverlayHostState
import com.teatt.pocket.ui.TabSpec
import com.teatt.pocket.ui.WebPane
import com.teatt.pocket.ui.rememberBarInset

private val TABS = listOf(
    TabSpec(Icons.Filled.Forum, "聊天"),
    TabSpec(Icons.Filled.Restaurant, "吃什么"),
    TabSpec(Icons.Filled.Cloud, "云账号"),
    TabSpec(Icons.Filled.Person, "我的")
)

/**
 * 主界面：底部液态玻璃功能栏 + 四个常驻页面。
 *
 * 关键约定（改之前先看这段）：
 * 1. 内容层铺满全屏、**不**为底部栏留 Compose padding——玻璃采样的是栏下方的像素，
 *    内容一旦让位，玻璃就退化成一块纯色磨砂。避让由各页面自己做：
 *    原生列表用 LocalBarInset 做 contentPadding，WebView 用 --tt-bar 交给网页。
 * 2. WebView 页面绝不能套 alpha / scale 动画，显隐只走 View.visibility，
 *    否则会触发离屏合成 + 两层 WebView 半透明叠加，就是肉眼可见的闪屏。
 * 3. 任何全屏覆盖页走 LocalOverlayHost，由这里画在底部栏之上，否则一定被栏压住。
 */
@Composable
fun RootScreen(
    onLoggedOut: () -> Unit
) {
    val state by Session.state.collectAsState()
    val backdrop = rememberLayerBackdrop()
    var tab by remember { mutableIntStateOf(0) }

    // 每次进入主界面（冷启动 / 登录成功）自动查一次更新，有新版本才弹窗。
    // 只在这里挂一次：切 tab 不会重复请求。
    LaunchedEffect(state.token) {
        if (state.token.isNotBlank()) UpdateCenter.check(state.token, manual = false)
    }

    // 只记住"访问过的 tab"，没点过的 WebView 不创建：一进主界面就同时拉起两个 WebView
    // 既吃内存也抢 GPU，是闪屏的放大器。
    var visited by remember { mutableStateOf(setOf(0)) }
    val known = visited + tab

    val overlay = remember { OverlayHostState() }
    val barInset = rememberBarInset()

    val density = LocalDensity.current
    val imeVisible = WindowInsets.ime.getBottom(density) > 0
    val overlayOpen = overlay.content != null
    val barHidden = imeVisible || overlayOpen

    val chatUrl = remember(state.token) { Session.webUrl("/") }
    val eatUrl = remember(state.token) { Session.webUrl("/eat.html") }

    // 网页 tab 的底部让位：直接在这层把 WebView 缩到玻璃栏之上。
    // 之前靠 JS 给网页塞 --tt-bar + 兜底样式，网页 DOM 一改（聊天/吃什么的后端前端是独立演进的）
    // 就对不上，表现就是输入框、列表尾部被栏压住。缩高是布局层面的事实，不依赖网页配合。
    // 键盘弹出时 WebView 已被 adjustResize 压矮，这里必须归零，否则会多留一条空白。
    val webBottomPad = if (imeVisible) 0.dp else barInset
    val pageBg = MaterialTheme.colorScheme.background

    val barHide by animateFloatAsState(
        targetValue = if (barHidden) 1f else 0f,
        animationSpec = tween(durationMillis = 220),
        label = "barHide"
    )

    SideEffect { if (tab !in visited) visited = visited + tab }

    CompositionLocalProvider(
        LocalBarInset provides barInset,
        LocalOverlayHost provides overlay
    ) {
        Box(Modifier.fillMaxSize()) {
            // 这一层会被底部玻璃栏采样，所以必须打上 layerBackdrop
            Box(
                Modifier
                    .fillMaxSize()
                    .layerBackdrop(backdrop)
            ) {
                if (0 in known) {
                    WebPageLayer(visible = tab == 0) {
                        WebPane(
                            url = chatUrl,
                            visible = tab == 0,
                            barInsetPx = 0,
                            backgroundColor = pageBg,
                            modifier = Modifier.fillMaxSize().padding(bottom = webBottomPad)
                        )
                    }
                }
                if (1 in known) {
                    WebPageLayer(visible = tab == 1) {
                        WebPane(
                            url = eatUrl,
                            visible = tab == 1,
                            barInsetPx = 0,
                            backgroundColor = pageBg,
                            modifier = Modifier.fillMaxSize().padding(bottom = webBottomPad)
                        )
                    }
                }
                if (2 in known) {
                    NativePage(visible = tab == 2) {
                        CloudScreen(token = state.token, modifier = Modifier.fillMaxSize())
                    }
                }
                if (3 in known) {
                    NativePage(visible = tab == 3) {
                        ProfileScreen(
                            state = state,
                            onLoggedOut = onLoggedOut,
                            modifier = Modifier.fillMaxSize()
                        )
                    }
                }
            }

            LiquidBottomBar(
                backdrop = backdrop,
                selectedIndex = tab,
                tabs = TABS,
                onSelect = { tab = it },
                modifier = Modifier
                    .align(Alignment.BottomCenter)
                    // 键盘弹起 / 全屏页打开时整条滑出屏幕，避免压在键盘上挤压消息区
                    .graphicsLayer {
                        translationY = barHide * (barInset + 24.dp).toPx()
                    }
            )

            // 全屏覆盖页：必须画在底部栏之后（zIndex 更高），否则一定被栏压住
            overlay.content?.let { content ->
                Box(Modifier.fillMaxSize().zIndex(10f)) {
                    content()
                }
            }
        }

        // 更新弹窗（独立 Window，不受层级影响）
        UpdateDialogHost(UpdateCenter.dialog) { UpdateCenter.dismiss() }
    }
}

/**
 * WebView 页容器：只做层级和触摸隔离，**不做任何透明度/缩放变换**。
 * 隐藏时 WebView 自己变 INVISIBLE（见 WebPane），这里再兜一层吞事件，防止点穿。
 *
 * zIndex 固定不动：以前用 `if (visible) 1f else -1f`，每次切页都会重排 interop View 的绘制顺序，
 * WebView 的表面被重新合成，就是切 tab 时那一下白闪。
 */
@Composable
private fun WebPageLayer(
    visible: Boolean,
    content: @Composable () -> Unit
) {
    Box(
        Modifier
            .fillMaxSize()
            .zIndex(1f)
            .then(if (visible) Modifier else Modifier.swallowTouches())
    ) {
        content()
    }
}

/** 原生页容器：淡入 + 轻微缩放，不涉及 WebView，随便动画。 */
@Composable
private fun NativePage(
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
            // 可见时必须压在 WebView 层（1f）之上
            .zIndex(if (visible) 2f else 0f)
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
