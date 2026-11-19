package com.teatt.pocket.feature.cloud

import android.annotation.SuppressLint
import android.graphics.Color as AndroidColor
import android.view.ViewGroup
import android.webkit.CookieManager
import android.webkit.WebChromeClient
import android.webkit.WebResourceRequest
import android.webkit.WebSettings
import android.webkit.WebView
import android.webkit.WebViewClient
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowBack
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.webkit.WebViewCompat
import androidx.webkit.WebViewFeature
import com.teatt.pocket.core.CloudGameDto
import com.teatt.pocket.core.CookieDto
import com.teatt.pocket.ui.BrandPink
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive

/**
 * 云游戏登录抓取页：WebView 里让用户手动登录，登录完点底部按钮，
 * 再从 CookieManager 收割各个域名下的 Cookie。
 *
 * 米哈游云游戏还需要 x-rpc-combo_token 才能判定账号有效 / 查剩余时长，
 * 而它不在 Cookie 里（页面是放在请求头里的），所以要在页面脚本执行前注入钩子，
 * 把 fetch / XHR 上的 x-rpc-combo_token 记下来，抓取时一并取走。
 *
 * 登录页必须先勾选「同意用户协议」，这里顺带注入 AGREE_ONLY_JS 自动勾上，
 * 用户只需要填账号密码。
 */
@SuppressLint("SetJavaScriptEnabled")
@Composable
fun CaptureView(
    game: CloudGameDto,
    onCancel: () -> Unit,
    onHarvest: (List<CookieDto>, String) -> Unit
) {
    var webView by remember { mutableStateOf<WebView?>(null) }
    var progress by remember { mutableStateOf(0) }
    var comboHint by remember { mutableStateOf("") }
    val startUrl = game.url.ifBlank { "https://ys.mihoyo.com/cloud/" }
    val needCombo = game.key == "genshin" || game.key == "starrail"

    BackHandler {
        val view = webView
        if (view != null && view.canGoBack()) view.goBack() else onCancel()
    }

    // 登录后米哈游会停在「正在检测您的账号安全」过渡页：停留 2 秒后直接重新加载云游戏页
    LaunchedEffect(Unit) {
        var hitSince = 0L
        var actionAt = 0L
        var cycles = 0
        while (isActive) {
            delay(1000)
            val view = webView ?: continue
            val hit = runCatching { jsString(view.evalJs(SECURITY_CHECK_JS)) }
                .getOrNull().orEmpty().isNotBlank()
            val now = System.currentTimeMillis()
            if (hit && cycles < 2) {
                if (hitSince == 0L) {
                    hitSince = now
                    comboHint = "检测到账号安全校验，2 秒后重新加载云游戏页"
                }
                if (actionAt == 0L && now - hitSince >= 2_000L) {
                    actionAt = now
                    cycles++
                    view.loadUrl(startUrl)
                    comboHint = "已重新加载云游戏页，等待 5 秒后再点抓取"
                }
            } else if (!hit && hitSince != 0L) {
                hitSince = 0L
                actionAt = 0L
            }
        }
    }

    Column(
        Modifier
            .fillMaxSize()
            .background(MaterialTheme.colorScheme.background)
            // 全屏 Dialog 里键盘不会自动压缩窗口，靠 ime inset 把底部按钮顶上来
            .imePadding()
    ) {
        Row(
            Modifier
                .fillMaxWidth()
                .statusBarsPadding()
                .padding(start = 6.dp, end = 12.dp, top = 6.dp, bottom = 6.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            IconButton(onClick = onCancel) {
                Icon(Icons.Filled.ArrowBack, contentDescription = "返回")
            }
            Column(Modifier.weight(1f)) {
                Text(
                    "登录 ${game.name}",
                    fontWeight = FontWeight.SemiBold,
                    fontSize = 15.sp,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
                Text(
                    "登录并进入云游戏后，点底部按钮抓取（协议会自动勾选）",
                    fontSize = 11.sp,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
            IconButton(onClick = { webView?.reload() }) {
                Icon(Icons.Filled.Refresh, contentDescription = "刷新", Modifier.size(19.dp))
            }
        }

        if (progress in 1..99) {
            LinearProgressIndicator(
                progress = { progress / 100f },
                modifier = Modifier.fillMaxWidth().height(2.dp),
                color = BrandPink
            )
        }

        AndroidView(
            modifier = Modifier.weight(1f),
            factory = { ctx ->
                WebView(ctx).apply {
                val web = this
                    layoutParams = ViewGroup.LayoutParams(
                        ViewGroup.LayoutParams.MATCH_PARENT,
                        ViewGroup.LayoutParams.MATCH_PARENT
                    )
                    setBackgroundColor(AndroidColor.WHITE)
                    overScrollMode = WebView.OVER_SCROLL_NEVER

                    with(settings) {
                        javaScriptEnabled = true
                        domStorageEnabled = true
                        databaseEnabled = true
                        useWideViewPort = true
                        loadWithOverviewMode = true
                        setSupportMultipleWindows(false)
                        javaScriptCanOpenWindowsAutomatically = true
                        mixedContentMode = WebSettings.MIXED_CONTENT_NEVER_ALLOW
                        // 桌面 UA：部分云游戏登录页对移动端 UA 会跳转到下载 App 页
                        userAgentString = userAgentString
                            .replace("Mobile", "")
                            .replace("Android", "")
                    }

                    CookieManager.getInstance().apply {
                        setAcceptCookie(true)
                        setAcceptThirdPartyCookies(web, true)
                    }

                    // 在页面脚本执行前注入，才能拿到登录后第一次云游戏请求里的 combo_token；
                    // AGREE_ONLY_JS 负责自动勾选用户协议（登录页在 iframe 里也能生效）
                    val hooked = WebViewFeature.isFeatureSupported(WebViewFeature.DOCUMENT_START_SCRIPT) &&
                        runCatching {
                            WebViewCompat.addDocumentStartJavaScript(web, COMBO_HOOK_JS, setOf("*"))
                            WebViewCompat.addDocumentStartJavaScript(web, AGREE_ONLY_JS, setOf("*"))
                        }.isSuccess

                    webChromeClient = object : WebChromeClient() {
                        override fun onProgressChanged(view: WebView?, newProgress: Int) {
                            progress = newProgress
                        }
                    }

                    webViewClient = object : WebViewClient() {
                        override fun onPageStarted(view: WebView, url: String?, favicon: android.graphics.Bitmap?) {
                            super.onPageStarted(view, url, favicon)
                            // 老版本 WebView 不支持 document-start 时的降级：每次导航都补注一次
                            if (!hooked) {
                                runCatching { view.evaluateJavascript(COMBO_HOOK_JS, null) }
                                runCatching { view.evaluateJavascript(AGREE_ONLY_JS, null) }
                            }
                        }

                        override fun shouldOverrideUrlLoading(
                            view: WebView,
                            request: WebResourceRequest
                        ): Boolean {
                            // 登录链路会跳多个域名，全部留在 WebView 内，否则会丢 cookie
                            val scheme = request.url.scheme.orEmpty()
                            return scheme != "http" && scheme != "https"
                        }
                    }

                    loadUrl(startUrl)
                    webView = this
                }
            },
            onRelease = { view ->
                // 只落盘，绝对不要清 Cookie：清了会重置设备信任，下次登录又要验证码
                CookieManager.getInstance().flush()
                (view.parent as? ViewGroup)?.removeView(view)
                view.stopLoading()
                view.destroy()
            }
        )

        Column(
            Modifier
                .fillMaxWidth()
                .navigationBarsPadding()
                .padding(horizontal = 16.dp, vertical = 10.dp)
        ) {
            if (comboHint.isNotBlank()) {
                Text(
                    comboHint,
                    fontSize = 12.sp,
                    color = MaterialTheme.colorScheme.error,
                    modifier = Modifier.padding(bottom = 8.dp)
                )
            }
            Box(
                Modifier
                    .fillMaxWidth()
                    .height(50.dp)
                    .background(BrandPink, RoundedCornerShape(16.dp))
                    .padding(0.dp),
                contentAlignment = Alignment.Center
            ) {
                TextButton(
                    onClick = {
                        val domains = CookieHarvester.domainsFor(game.key, startUrl)
                        val view = webView
                        if (view == null) {
                            onHarvest(CookieHarvester.harvest(domains), "")
                            return@TextButton
                        }
                        // 先向页面要 combo_token，再连 Cookie 一起交给上传流程
                        view.evaluateJavascript(COMBO_READ_JS) { raw ->
                            val combo = jsString(raw)
                            if (needCombo && combo.isBlank()) {
                                comboHint = "还没抓到云游戏凭证（combo_token）。可以尝试点击返回，重新进入抓取，" +
                                    "然后重新点击下方抓取按钮。"
                            } else {
                                comboHint = ""
                                onHarvest(CookieHarvester.harvest(domains), combo)
                            }
                        }
                    },
                    modifier = Modifier.fillMaxSize()
                ) {
                    Text("我已登录完成，抓取", color = Color.White, fontWeight = FontWeight.SemiBold)
                }
            }
            Spacer(Modifier.height(6.dp))
            Row(
                Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.Center
            ) {
                Text(
                    "抓取只读取 Cookie，账号密码不会离开这台手机",
                    fontSize = 11.sp,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
        }
    }
}
