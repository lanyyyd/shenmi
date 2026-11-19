package com.teatt.pocket.feature.cloud

import android.annotation.SuppressLint
import android.graphics.Color as AndroidColor
import android.view.ViewGroup
import android.webkit.CookieManager
import android.webkit.WebChromeClient
import android.webkit.WebResourceError
import android.webkit.WebResourceRequest
import android.webkit.WebSettings
import android.webkit.WebView
import android.webkit.WebViewClient
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.webkit.WebViewCompat
import androidx.webkit.WebViewFeature
import com.teatt.pocket.core.CloudCredential
import com.teatt.pocket.core.CloudGameDto
import com.teatt.pocket.core.CookieDto
import com.teatt.pocket.ui.BrandPink
import kotlinx.coroutines.delay
import kotlinx.coroutines.suspendCancellableCoroutine
import org.json.JSONObject
import kotlin.coroutines.resume

/** 静默登录的结果；由 [AutoRefreshCenter] 消费并上传服务端。 */
sealed interface SilentOutcome {
    data class Success(val cookies: List<CookieDto>, val comboToken: String) : SilentOutcome
    data class Failed(val message: String) : SilentOutcome
    object Cancelled : SilentOutcome
}

/**
 * 后台静默登录云游戏并抓取登录态。
 *
 * 关键约束（别改）：
 * 1. **不清 WebView Cookie**。清了 Cookie 米哈游会重新做设备验证，登录必出验证码，
 *    静默续期就失去意义。这里只读 Cookie、只让页面自己续写 Cookie。
 * 2. 页面加载后由注入脚本自动勾选用户协议、填账号密码、点登录；
 *    遇到滑块/验证码时把页面展开交给用户手动完成，完成后继续轮询 combo_token。
 * 3. 拿到 combo_token 后立刻收 Cookie 返回，不进入云游戏串流页。
 */
@SuppressLint("SetJavaScriptEnabled")
@Composable
fun SilentLoginView(
    game: CloudGameDto,
    credential: CloudCredential,
    onResult: (SilentOutcome) -> Unit
) {
    var webView by remember { mutableStateOf<WebView?>(null) }
    var manual by remember { mutableStateOf(false) }
    var status by remember { mutableStateOf("正在检查现有登录态…") }
    var finished by remember { mutableStateOf(false) }

    val startUrl = game.url.ifBlank {
        if (game.key == "starrail") "https://sr.mihoyo.com/cloud/#/" else "https://ys.mihoyo.com/cloud/#/"
    }

    fun finish(outcome: SilentOutcome) {
        if (finished) return
        finished = true
        onResult(outcome)
    }

    // 页面被销毁（退出登录 / 进程回收）时释放等待方，避免调度器一直卡在 await
    DisposableEffect(Unit) {
        onDispose { AutoRefreshCenter.finish(SilentOutcome.Cancelled) }
    }

    LaunchedEffect(webView) {
        val view = webView ?: return@LaunchedEffect
        val startedAt = System.currentTimeMillis()
        while (!finished) {
            delay(1000)
            val probe = runCatching { JSONObject(jsString(view.evalJs(SILENT_PROBE_JS))) }.getOrNull()
            val elapsed = System.currentTimeMillis() - startedAt
            val combo = probe?.optString("combo").orEmpty()
            val error = probe?.optString("error").orEmpty()
            val captcha = probe?.optBoolean("captcha") == true
            val login = probe?.optBoolean("login") == true

            if (error.isNotBlank()) {
                finish(SilentOutcome.Failed(error))
                return@LaunchedEffect
            }

            // 钩子没抓到时，翻一次 localStorage / cookie 兜底（登录成功但凭证已缓存的场景）
            var comboValue = combo
            if (comboValue.isBlank() && elapsed > 3_000L) {
                comboValue = jsString(view.evalJs(COMBO_READ_JS))
            }

            if (comboValue.isNotBlank()) {
                val cookies = CookieHarvester.harvest(CookieHarvester.domainsFor(game.key, startUrl))
                if (cookies.isEmpty()) {
                    finish(SilentOutcome.Failed("已登录成功，但没读到 Cookie，请手动重抓"))
                } else {
                    finish(SilentOutcome.Success(cookies, comboValue))
                }
                return@LaunchedEffect
            } else if (captcha && !manual) {
                manual = true
                status = "米哈游要求安全验证，请在下方页面完成验证"
            } else if (login && !manual) {
                status = "正在自动填写账号密码…"
            } else if (!manual) {
                status = "正在打开云游戏登录页…"
            }

            if (!manual && elapsed > 45_000L) {
                // 一直拿不到凭证：展开页面，让用户点一下签到 / 开始游戏触发云游戏请求
                manual = true
                status = "没能自动拿到凭证，请点一下页面里的「签到」或「开始游戏」"
            }
            if (manual && elapsed > 180_000L) {
                finish(SilentOutcome.Failed("验证未完成，已停止自动更新"))
                return@LaunchedEffect
            }
        }
    }

    Box(
        Modifier
            .fillMaxSize()
            .background(MaterialTheme.colorScheme.background)
    ) {
        // 页面始终铺满（放在不透明进度层后面），避免 1dp 尺寸让登录页渲染成异常布局；
        // 需要人工接管时把进度层拿掉即可，WebView 不会重建
        AndroidView(
            modifier = Modifier.fillMaxSize(),
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
                        userAgentString = userAgentString
                            .replace("Mobile", "")
                            .replace("Android", "")
                    }

                    // 只打开 Cookie 读写，绝不清除已有 Cookie（设备信任全靠它）
                    CookieManager.getInstance().apply {
                        setAcceptCookie(true)
                        setAcceptThirdPartyCookies(web, true)
                    }

                    val hooked = WebViewFeature.isFeatureSupported(WebViewFeature.DOCUMENT_START_SCRIPT) &&
                        runCatching {
                            WebViewCompat.addDocumentStartJavaScript(web, COMBO_HOOK_JS, setOf("*"))
                            WebViewCompat.addDocumentStartJavaScript(
                                web,
                                buildAutoFillJs(credential.account, credential.password),
                                setOf("*")
                            )
                        }.isSuccess

                    webChromeClient = object : WebChromeClient() {
                        override fun onProgressChanged(view: WebView?, newProgress: Int) {
                            if (!manual && newProgress in 1..99) status = "正在加载登录页…"
                        }
                    }

                    webViewClient = object : WebViewClient() {
                        override fun onPageStarted(view: WebView, url: String?, favicon: android.graphics.Bitmap?) {
                            super.onPageStarted(view, url, favicon)
                            // 老 WebView 不支持 document-start 时，至少保证顶层页面能自动填写
                            if (!hooked) {
                                runCatching { view.evaluateJavascript(COMBO_HOOK_JS, null) }
                                runCatching {
                                    view.evaluateJavascript(
                                        buildAutoFillJs(credential.account, credential.password),
                                        null
                                    )
                                }
                            }
                        }

                        override fun shouldOverrideUrlLoading(
                            view: WebView,
                            request: WebResourceRequest
                        ): Boolean {
                            val scheme = request.url.scheme.orEmpty()
                            return scheme != "http" && scheme != "https"
                        }

                        override fun onReceivedError(
                            view: WebView,
                            request: WebResourceRequest,
                            error: WebResourceError
                        ) {
                            super.onReceivedError(view, request, error)
                            if (request.isForMainFrame && !finished) {
                                finish(SilentOutcome.Failed("网络连接失败，请检查网络后重试"))
                            }
                        }
                    }

                    loadUrl(startUrl)
                    webView = this
                }
            },
            onRelease = { view ->
                // 只落盘，不清 Cookie
                runCatching { CookieManager.getInstance().flush() }
                (view.parent as? ViewGroup)?.removeView(view)
                view.stopLoading()
                view.destroy()
            }
        )

        if (manual) {
            Row(
                Modifier
                    .fillMaxWidth()
                    .align(Alignment.TopCenter)
                    .background(Color(0xF2FFFFFF))
                    .statusBarsPadding()
                    .padding(horizontal = 14.dp, vertical = 8.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Column(Modifier.weight(1f)) {
                    Text(
                        "${game.name} · 需要手动完成",
                        fontSize = 14.sp,
                        fontWeight = FontWeight.SemiBold,
                        color = Color(0xFF111111)
                    )
                    Text(
                        status,
                        fontSize = 11.sp,
                        color = Color(0xFF666666)
                    )
                }
                TextButton(onClick = { finish(SilentOutcome.Cancelled) }) {
                    Text("取消", color = Color(0xFF111111))
                }
            }
        } else {
            Box(
                Modifier
                    .fillMaxSize()
                    .background(MaterialTheme.colorScheme.background),
                contentAlignment = Alignment.Center
            ) {
                Column(
                    horizontalAlignment = Alignment.CenterHorizontally,
                    verticalArrangement = Arrangement.spacedBy(10.dp),
                    modifier = Modifier.padding(32.dp)
                ) {
                    CircularProgressIndicator(color = BrandPink, strokeWidth = 2.dp)
                    Spacer(Modifier.height(2.dp))
                    Text(
                        "正在静默更新${game.name}登录态",
                        fontSize = 15.sp,
                        fontWeight = FontWeight.SemiBold,
                        color = MaterialTheme.colorScheme.onBackground,
                        textAlign = TextAlign.Center
                    )
                    Text(
                        status,
                        fontSize = 12.sp,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        textAlign = TextAlign.Center
                    )
                    Text(
                        "账号 ${credential.account} · 密码只保存在本机",
                        fontSize = 11.sp,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        textAlign = TextAlign.Center
                    )
                    Spacer(Modifier.height(4.dp))
                    TextButton(onClick = { finish(SilentOutcome.Cancelled) }) { Text("取消") }
                }
            }
        }
    }
}

/** evaluateJavascript 一定要在主线程调用；这里封成挂起函数方便轮询。 */
private suspend fun WebView.evalJs(script: String): String? =
    suspendCancellableCoroutine { continuation ->
        runCatching {
            evaluateJavascript(script) { result ->
                if (continuation.isActive) continuation.resume(result)
            }
        }.onFailure {
            if (continuation.isActive) continuation.resume(null)
        }
    }
