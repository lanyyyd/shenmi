package com.teatt.pocket.ui

import android.annotation.SuppressLint
import android.content.Intent
import android.graphics.Color as AndroidColor
import android.net.Uri
import android.view.ViewGroup
import android.webkit.CookieManager
import android.webkit.WebChromeClient
import android.webkit.WebResourceRequest
import android.webkit.WebSettings
import android.webkit.WebView
import android.webkit.WebViewClient
import androidx.activity.compose.BackHandler
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.viewinterop.AndroidView
import com.teatt.pocket.core.Api

/**
 * 网页 tab：聊天与"吃什么"直接复用现有 Web 前端。
 *
 * 登录态通过 /api/autologin?jwt=... 换取聊天会话 cookie，用户在 App 内登录一次，
 * WebView 里就不用再登（服务端这个接口本来就是给客户端内嵌浏览器设计的）。
 */
@SuppressLint("SetJavaScriptEnabled")
@Composable
fun WebPane(
    url: String,
    visible: Boolean,
    modifier: Modifier = Modifier
) {
    val context = LocalContext.current
    var webView by remember { mutableStateOf<WebView?>(null) }
    var loadedUrl by remember { mutableStateOf<String?>(null) }

    BackHandler(enabled = visible && webView?.canGoBack() == true) {
        webView?.goBack()
    }

    AndroidView(
        modifier = modifier,
        factory = { ctx ->
            WebView(ctx).apply {
                val web = this
                layoutParams = ViewGroup.LayoutParams(
                    ViewGroup.LayoutParams.MATCH_PARENT,
                    ViewGroup.LayoutParams.MATCH_PARENT
                )
                setBackgroundColor(AndroidColor.TRANSPARENT)
                overScrollMode = WebView.OVER_SCROLL_NEVER

                with(settings) {
                    javaScriptEnabled = true
                    domStorageEnabled = true
                    databaseEnabled = true
                    loadsImagesAutomatically = true
                    useWideViewPort = true
                    loadWithOverviewMode = false
                    mediaPlaybackRequiresUserGesture = false
                    mixedContentMode = WebSettings.MIXED_CONTENT_NEVER_ALLOW
                    // 让页面能以"独立 WebView"形态布局（100vh 之类更准）
                    userAgentString = userAgentString
                }

                CookieManager.getInstance().apply {
                    setAcceptCookie(true)
                    setAcceptThirdPartyCookies(web, true)
                }

                webChromeClient = WebChromeClient()
                webViewClient = object : WebViewClient() {
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
                }

                loadUrl(url)
                loadedUrl = url
                webView = this
            }
        },
        update = { view ->
            // url 变化（例如重新登录后换了新 jwt）才重新加载，切 tab 不会触发
            if (loadedUrl != url) {
                loadedUrl = url
                view.loadUrl(url)
            }
        },
        onRelease = { view ->
            CookieManager.getInstance().flush()
            (view.parent as? ViewGroup)?.removeView(view)
            view.stopLoading()
            view.destroy()
        }
    )
}
