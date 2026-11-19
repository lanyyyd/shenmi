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
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowBack
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
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
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
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
 *
 * 正式版默认只显示右侧悬浮窗（App 内，不需要悬浮窗权限）；点一下展开成完整 WebView
 * 手动接管。测试模式直接展开，并显示每一步的操作日志。
 */
@SuppressLint("SetJavaScriptEnabled")
@Composable
fun SilentLoginView(
    game: CloudGameDto,
    credential: CloudCredential,
    expanded: Boolean,
    debug: Boolean,
    onExpand: () -> Unit,
    onCollapse: () -> Unit,
    onClose: () -> Unit,
    onResult: (SilentOutcome) -> Unit
) {
    var webView by remember { mutableStateOf<WebView?>(null) }
    var manual by remember { mutableStateOf(false) }
    var status by remember { mutableStateOf("正在检查现有登录态…") }
    var finished by remember { mutableStateOf(false) }
    var showLogs by remember(debug) { mutableStateOf(debug) }
    val logs = remember { mutableStateListOf<String>() }
    val logScroll = rememberScrollState()
    val clipboard = LocalClipboardManager.current

    fun log(line: String) {
        val stamp = SimpleDateFormat("HH:mm:ss", Locale.getDefault()).format(Date())
        logs += "$stamp $line"
        if (logs.size > 300) logs.removeAt(0)
    }

    fun setStatus(text: String) {
        if (status == text) return
        status = text
        log(text)
    }

    fun finish(outcome: SilentOutcome) {
        if (finished) return
        finished = true
        when (outcome) {
            is SilentOutcome.Success ->
                log("完成：${outcome.cookies.size} 项 Cookie，combo_token 长度 ${outcome.comboToken.length}")
            is SilentOutcome.Failed -> log("失败：${outcome.message}")
            SilentOutcome.Cancelled -> log("已取消")
        }
        onResult(outcome)
    }

    val startUrl = game.url.ifBlank {
        if (game.key == "starrail") "https://sr.mihoyo.com/cloud/#/" else "https://ys.mihoyo.com/cloud/#/"
    }

    LaunchedEffect(Unit) {
        log("开始静默登录 ${game.name}")
        log("账号 ${credential.account}（密码只在本机）")
        log("保留 WebView 现有 Cookie，不清除设备登录态")
    }

    // 页面被销毁（退出登录 / 进程回收）时释放等待方，避免调度器一直卡在 await
    DisposableEffect(Unit) {
        onDispose { if (!finished) onResult(SilentOutcome.Cancelled) }
    }

    LaunchedEffect(webView) {
        val view = webView ?: return@LaunchedEffect
        val startedAt = System.currentTimeMillis()
        var lastUrl = ""
        var jsLogSeen = 0
        var loggedForm = false
        var loggedCaptcha = false
        while (!finished) {
            delay(1000)
            val raw = runCatching { view.evalJs(SILENT_PROBE_JS) }.getOrNull()
            val probe = runCatching { JSONObject(raw.orEmpty()) }.getOrNull() ?: continue
            val elapsed = System.currentTimeMillis() - startedAt

            val url = probe.optString("url")
            if (url.isNotBlank() && url != lastUrl) {
                lastUrl = url
                log("页面 $url")
            }

            // 注入脚本自己的操作日志，页面跳转后数组会重置
            val jsLogs = probe.optJSONArray("logs")
            if (jsLogs != null) {
                if (jsLogs.length() < jsLogSeen) jsLogSeen = 0
                for (i in jsLogSeen until jsLogs.length()) {
                    val line = jsLogs.optString(i)
                    if (line.isNotBlank()) log("网页 $line")
                }
                jsLogSeen = jsLogs.length()
            }

            val combo = probe.optString("combo")
            val error = probe.optString("error")
            val captcha = probe.optBoolean("captcha")
            val login = probe.optBoolean("login")

            if (login && !loggedForm) {
                loggedForm = true
                log("检测到账号密码输入框")
            }
            if (captcha) {
                if (!loggedCaptcha) {
                    loggedCaptcha = true
                    log("检测到安全验证（滑块/验证码）")
                }
            } else {
                loggedCaptcha = false
            }

            if (error.isNotBlank()) {
                finish(SilentOutcome.Failed(error))
                return@LaunchedEffect
            }

            // 钩子没抓到时，翻一次 localStorage / cookie 兜底（登录成功但凭证已缓存的场景）
            var comboValue = combo
            if (comboValue.isBlank() && elapsed > 3_000L) {
                comboValue = jsString(view.evalJs(COMBO_READ_JS))
                if (comboValue.isNotBlank() && !loggedForm) log("从页面缓存里读到登录凭证")
            }

            if (comboValue.isNotBlank()) {
                val domains = CookieHarvester.domainsFor(game.key, startUrl)
                val cookies = CookieHarvester.harvest(domains)
                log("抓取 Cookie：${cookies.size} 项")
                if (cookies.isEmpty()) {
                    finish(SilentOutcome.Failed("已登录成功，但没读到 Cookie，请手动重抓"))
                } else {
                    finish(SilentOutcome.Success(cookies, comboValue))
                }
                return@LaunchedEffect
            } else if (captcha && !manual) {
                manual = true
                setStatus("米哈游要求安全验证，请在页面里手动完成")
                log("验证码挡住自动登录，已展开页面等待手动接管")
                if (!expanded) onExpand()
            } else if (login && !manual) {
                setStatus("已出现登录框，正在自动填写账号密码…")
            } else if (!manual) {
                setStatus("正在打开云游戏登录页…")
            }

            if (!manual && elapsed > 45_000L) {
                // 一直拿不到凭证：展开页面，让用户点一下签到 / 开始游戏触发云游戏请求
                manual = true
                setStatus("没能自动拿到凭证，请点一下页面里的「签到」或「开始游戏」")
                log("45 秒未拿到凭证，展开页面等待手动触发")
                if (!expanded) onExpand()
            }
            if (manual && elapsed > 180_000L) {
                finish(SilentOutcome.Failed("验证未完成，已停止自动更新"))
                return@LaunchedEffect
            }
        }
    }

    LaunchedEffect(logs.size, showLogs, expanded) {
        if (showLogs && expanded) runCatching { logScroll.animateScrollTo(logScroll.maxValue) }
    }

    BackHandler(enabled = expanded) { onCollapse() }

    Box(
        Modifier
            .fillMaxSize()
            .clipToBounds()
    ) {
        Column(
            Modifier
                .fillMaxSize()
                .background(if (expanded) MaterialTheme.colorScheme.background else Color.Transparent)
        ) {
            if (expanded) {
                Row(
                    Modifier
                        .fillMaxWidth()
                        .background(Color(0xF7FFFFFF))
                        .statusBarsPadding()
                        .padding(start = 2.dp, end = 4.dp, top = 2.dp, bottom = 2.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    IconButton(onClick = onCollapse) {
                        Icon(Icons.Filled.ArrowBack, contentDescription = "收起", tint = Color(0xFF111111))
                    }
                    Column(Modifier.weight(1f)) {
                        Text(
                            "${game.name} · 静默登录${if (debug) "测试" else ""}",
                            fontSize = 14.sp,
                            fontWeight = FontWeight.SemiBold,
                            color = Color(0xFF111111)
                        )
                        Text(
                            status,
                            fontSize = 11.sp,
                            color = Color(0xFF666666),
                            maxLines = 2,
                            overflow = TextOverflow.Ellipsis
                        )
                    }
                    TextButton(onClick = { showLogs = !showLogs }) {
                        Text(
                            if (showLogs) "收起日志" else "日志",
                            fontSize = 12.sp,
                            color = Color(0xFF111111)
                        )
                    }
                    TextButton(onClick = {
                        if (finished) onClose() else finish(SilentOutcome.Cancelled)
                    }) {
                        Text(
                            if (finished) "关闭" else "取消",
                            fontSize = 12.sp,
                            color = Color(0xFFB91C1C)
                        )
                    }
                }
            }

            // 页面始终按全屏尺寸布局（登录页对宽高敏感），收起时整体移到窗口外，
            // 既不遮挡 App、也不接收触摸，但 WebView 本身不销毁，JS 继续跑。
            Box(Modifier.weight(1f).fillMaxWidth()) {
                Box(
                    Modifier
                        .fillMaxSize()
                        .then(if (expanded) Modifier else Modifier.offset(x = 3000.dp))
                        .clipToBounds()
                ) {
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
                                        if (!manual && newProgress in 1..99) {
                                            setStatus("正在加载登录页…")
                                        }
                                    }
                                }

                                webViewClient = object : WebViewClient() {
                                    override fun onPageStarted(
                                        view: WebView,
                                        url: String?,
                                        favicon: android.graphics.Bitmap?
                                    ) {
                                        super.onPageStarted(view, url, favicon)
                                        log("开始加载 ${url.orEmpty()}")
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

                                    override fun onPageFinished(view: WebView, url: String?) {
                                        super.onPageFinished(view, url)
                                        log("加载完成 ${url.orEmpty()}")
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
                                            log("页面加载失败：${error.description}")
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
                }
            }

            if (expanded && showLogs) {
                Surface(color = Color(0xFF0F172A)) {
                    Column(Modifier.fillMaxWidth().height(190.dp)) {
                        Row(
                            Modifier
                                .fillMaxWidth()
                                .padding(start = 12.dp, end = 6.dp, top = 2.dp, bottom = 2.dp),
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            Text(
                                "操作日志（${logs.size}）",
                                fontSize = 11.sp,
                                color = Color(0xFF94A3B8)
                            )
                            Spacer(Modifier.weight(1f))
                            TextButton(onClick = {
                                clipboard.setText(AnnotatedString(logs.joinToString("\n")))
                            }) {
                                Text("复制", fontSize = 11.sp, color = Color(0xFF93C5FD))
                            }
                            TextButton(onClick = { showLogs = false }) {
                                Text("收起", fontSize = 11.sp, color = Color(0xFF93C5FD))
                            }
                        }
                        Column(
                            Modifier
                                .weight(1f)
                                .fillMaxWidth()
                                .verticalScroll(logScroll)
                                .padding(horizontal = 12.dp)
                        ) {
                            if (logs.isEmpty()) {
                                Text(
                                    "等待操作…",
                                    fontSize = 10.sp,
                                    color = Color(0xFF64748B),
                                    fontFamily = FontFamily.Monospace
                                )
                            }
                            logs.forEach { line ->
                                Text(
                                    line,
                                    fontSize = 10.sp,
                                    color = Color(0xFFE2E8F0),
                                    fontFamily = FontFamily.Monospace
                                )
                            }
                            Spacer(Modifier.height(8.dp))
                        }
                    }
                }
            }
        }

        // 收起状态：只在 App 内右侧显示一个悬浮窗，点击展开
        if (!expanded) {
            Surface(
                modifier = Modifier
                    .align(Alignment.CenterEnd)
                    .padding(end = 10.dp)
                    .width(152.dp)
                    .clickable(onClick = onExpand),
                shape = RoundedCornerShape(16.dp),
                color = MaterialTheme.colorScheme.surface,
                shadowElevation = 10.dp
            ) {
                Column(
                    Modifier.padding(12.dp),
                    verticalArrangement = Arrangement.spacedBy(5.dp)
                ) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        if (finished) {
                            Icon(
                                Icons.Filled.CheckCircle,
                                contentDescription = null,
                                tint = Color(0xFF22C55E),
                                modifier = Modifier.size(14.dp)
                            )
                        } else {
                            CircularProgressIndicator(
                                modifier = Modifier.size(13.dp),
                                strokeWidth = 1.5.dp,
                                color = BrandPink
                            )
                        }
                        Spacer(Modifier.width(6.dp))
                        Text(
                            if (debug) "静默测试" else "静默续期",
                            fontSize = 11.sp,
                            fontWeight = FontWeight.SemiBold
                        )
                    }
                    Text(
                        game.name,
                        fontSize = 11.sp,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis
                    )
                    if (!finished) {
                        LinearProgressIndicator(
                            modifier = Modifier
                                .fillMaxWidth()
                                .height(2.dp)
                                .clip(RoundedCornerShape(1.dp)),
                            color = BrandPink,
                            trackColor = MaterialTheme.colorScheme.surfaceVariant
                        )
                    }
                    Text(
                        status,
                        fontSize = 10.sp,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        maxLines = 3,
                        overflow = TextOverflow.Ellipsis
                    )
                    Text(
                        if (finished) "点击查看日志" else "点击接管",
                        fontSize = 10.sp,
                        color = BrandPink
                    )
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
