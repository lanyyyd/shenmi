package com.teatt.pocket.ui

import android.annotation.SuppressLint
import android.content.Intent
import android.graphics.Bitmap
import android.net.Uri
import android.view.View
import android.view.ViewGroup
import android.webkit.CookieManager
import android.webkit.WebChromeClient
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import android.webkit.WebSettings
import android.webkit.WebView
import android.webkit.WebViewClient
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import com.teatt.pocket.BuildConfig
import com.teatt.pocket.core.Api

/**
 * 网页 tab：聊天与"吃什么"直接复用现有 Web 前端。
 *
 * 登录态通过 /api/autologin?jwt=... 换取聊天会话 cookie，用户在 App 内登录一次，
 * WebView 里就不用再登（服务端这个接口本来就是给客户端内嵌浏览器设计的）。
 *
 * 底部玻璃栏不做 Compose padding（玻璃要采样栏下方的像素），
 * 而是把栏高以 CSS 变量 `--tt-bar` 注入网页，由网页自己把底部元素抬起来。
 *
 * 注意：这个 WebView **不能**套 alpha / scale 动画。它一旦被 graphicsLayer 改透明度，
 * 就得走离屏渲染再合成，切页时两层 WebView 半透明叠加 = 肉眼看到的闪屏。
 * 显隐一律走 View.visibility。
 */
@SuppressLint("SetJavaScriptEnabled")
@Composable
fun WebPane(
    url: String,
    visible: Boolean,
    barInsetPx: Int,
    backgroundColor: Color,
    modifier: Modifier = Modifier
) {
    var webView by remember { mutableStateOf<WebView?>(null) }
    var ready by remember { mutableStateOf(false) }
    var progress by remember { mutableIntStateOf(0) }

    // 用普通对象记录已加载地址：放 state 会在组合期写状态，多触发一轮重组
    val loaded = remember { UrlSlot() }
    val barPx by rememberUpdatedState(barInsetPx)
    val bgArgb = remember(backgroundColor) { backgroundColor.toArgb() }

    BackHandler(enabled = visible && webView?.canGoBack() == true) {
        webView?.goBack()
    }

    // 栏高变化（切 tab / 键盘弹起归零）时同步给页面
    LaunchedEffect(webView, barInsetPx) {
        webView?.applyBarInset(barInsetPx)
    }

    // URL 变了才重载（重新登录换了新 jwt），切 tab 不会触发
    LaunchedEffect(webView, url) {
        val view = webView ?: return@LaunchedEffect
        if (loaded.value != url) {
            loaded.value = url
            ready = false
            view.loadUrl(url)
        }
    }

    Box(modifier) {
        AndroidView(
            modifier = Modifier.fillMaxSize(),
            factory = { ctx ->
                WebView(ctx).apply {
                    val web = this
                    layoutParams = ViewGroup.LayoutParams(
                        ViewGroup.LayoutParams.MATCH_PARENT,
                        ViewGroup.LayoutParams.MATCH_PARENT
                    )
                    // 不透明底色：透明 WebView 在首帧之前会透出下层页面，就是"闪"的来源之一
                    setBackgroundColor(bgArgb)
                    overScrollMode = WebView.OVER_SCROLL_NEVER
                    // 固定硬件层，避免透明度/滚动时反复切软件层
                    setLayerType(View.LAYER_TYPE_HARDWARE, null)
                    // 切 tab 只是 INVISIBLE，不要让渲染进程被系统降级回收，
                    // 否则切回来的第一帧是空表面 = 白闪
                    setRendererPriorityPolicy(WebView.RENDERER_PRIORITY_IMPORTANT, false)

                    with(settings) {
                        javaScriptEnabled = true
                        domStorageEnabled = true
                        databaseEnabled = true
                        loadsImagesAutomatically = true
                        useWideViewPort = true
                        loadWithOverviewMode = false
                        mediaPlaybackRequiresUserGesture = false
                        mixedContentMode = WebSettings.MIXED_CONTENT_NEVER_ALLOW
                        setNeedInitialFocus(false)
                        // 带上渠道标识，网页可据此做内嵌适配
                        userAgentString = userAgentString + " TeaTTApp/" + BuildConfig.VERSION_NAME
                    }

                    CookieManager.getInstance().apply {
                        setAcceptCookie(true)
                        setAcceptThirdPartyCookies(web, true)
                    }

                    webChromeClient = object : WebChromeClient() {
                        override fun onProgressChanged(view: WebView?, newProgress: Int) {
                            progress = newProgress
                        }
                    }

                    webViewClient = object : WebViewClient() {
                        override fun onPageStarted(
                            view: WebView?,
                            url: String?,
                            favicon: Bitmap?
                        ) {
                            progress = 0
                            // 尽早写入，避免首帧后出现"输入框从底部跳上来"
                            view?.applyBarInset(barPx)
                        }

                        override fun onPageCommitVisible(view: WebView?, url: String?) {
                            ready = true
                            view?.applyBarInset(barPx)
                        }

                        override fun onPageFinished(view: WebView?, url: String?) {
                            // SPA 内跳转不会触发 onPageStarted，这里补一刀
                            // 同时兜住"commit 回调没来"的机型，避免占位层一直盖着
                            ready = true
                            view?.applyBarInset(barPx)
                        }

                        override fun onReceivedError(
                            view: WebView?,
                            request: WebResourceRequest?,
                            error: android.webkit.WebResourceError?
                        ) {
                            // 断网/超时也要撤掉占位层，露出 WebView 自己的错误页
                            ready = true
                        }

                        override fun shouldOverrideUrlLoading(
                            view: WebView,
                            request: WebResourceRequest
                        ): Boolean {
                            val target = request.url
                            // 站内页面留在 WebView，站外交给系统浏览器
                            if (target.host == Uri.parse(Api.BASE).host) return false
                            runCatching {
                                view.context.startActivity(Intent(Intent.ACTION_VIEW, target))
                            }
                            return true
                        }

                        // 图片/字体走本地磁盘缓存，避免每次进「吃什么」都把整页菜图重下一遍
                        override fun shouldInterceptRequest(
                            view: WebView,
                            request: WebResourceRequest
                        ): WebResourceResponse? {
                            val url = request.url.toString()
                            if (request.method != "GET" || url.isEmpty()) return null
                            if (!WebAssetCache.isCacheable(url)) return null
                            return WebAssetCache.fetch(url)
                        }
                    }

                    loadUrl(url)
                    loaded.value = url
                    webView = this
                }
            },
            update = { view ->
                // 只切可见性，**不要**调 onPause/onResume。
                // 一旦 pause，WebView 的渲染进程会被降级，切回来要重建表面，
                // 就是用户看到的那一下白闪（两个 WebView 交替暂停/恢复时尤其明显）。
                val want = if (visible) View.VISIBLE else View.INVISIBLE
                if (view.visibility != want) view.visibility = want
            },
            onRelease = { view ->
                CookieManager.getInstance().flush()
                (view.parent as? ViewGroup)?.removeView(view)
                view.stopLoading()
                view.destroy()
            }
        )

        // 首帧占位：commit 之前盖一层同色底，消掉白底闪一下
        if (!ready) {
            Box(Modifier.fillMaxSize().background(backgroundColor))
        }

        if (progress in 1..99) {
            LinearProgressIndicator(
                progress = { progress / 100f },
                modifier = Modifier
                    .fillMaxWidth()
                    .height(2.dp)
                    .align(Alignment.TopCenter)
            )
        }
    }
}

/** 只用来存一个字符串，不参与重组。 */
private class UrlSlot {
    var value: String? = null
}

/**
 * 把底部玻璃栏的高度写给网页。
 *
 * 用 CSSOM（`style.setProperty`）而不是插 `<style>` 标签写变量：
 * CSSOM 不受网页 CSP 的 style-src 限制，且换页后重新写入即可。
 *
 * 兜底样式统一用 `:where()`（特异性 0）：网页正式 CSS 一旦带上
 * `padding-bottom: var(--tt-bar)`，就会自动盖掉这里的兜底，两套不会打架。
 */
private fun WebView.applyBarInset(px: Int) {
    if (!isAttachedToWindow) return
    val js = """
        (function(){
          try{
            var d=document.documentElement;
            if(!d||!d.style)return;
            d.style.setProperty('--tt-bar','${px}px');
            if(!document.getElementById('tt-embed')){
              var eat=location.pathname.indexOf('eat')>=0;
              var css=eat
                ? ':where(#app){padding-bottom:calc(82px + var(--tt-bar,0px))}'
                 +':where(#page-store){padding-bottom:calc(100px + var(--tt-bar,0px))}'
                 +':where(.sticky-action){bottom:calc(86px + var(--tt-bar,0px))}'
                : ':where(.chat){padding-bottom:var(--tt-bar,0px)}';
              var s=document.createElement('style');
              s.id='tt-embed';
              s.textContent=css;
              (document.head||d).appendChild(s);
            }
          }catch(e){}
        })()
    """.trimIndent()
    runCatching { evaluateJavascript(js, null) }
}
