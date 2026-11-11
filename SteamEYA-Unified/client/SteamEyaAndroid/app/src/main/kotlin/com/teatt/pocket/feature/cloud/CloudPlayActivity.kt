package com.teatt.pocket.feature.cloud

import android.annotation.SuppressLint
import android.app.AlertDialog
import android.content.Context
import android.content.Intent
import android.graphics.Color
import android.net.Uri
import android.os.Bundle
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.view.WindowManager
import android.webkit.CookieManager
import android.webkit.PermissionRequest
import android.webkit.WebChromeClient
import android.webkit.WebResourceError
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import android.webkit.WebSettings
import android.webkit.WebView
import android.webkit.WebViewClient
import android.widget.Button
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.TextView
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.OnBackPressedCallback
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.lifecycle.lifecycleScope
import com.teatt.pocket.core.Session
import com.teatt.pocket.data.CloudApi
import kotlinx.coroutines.launch

/**
 * 云游戏上号：独立的横屏全屏 Activity。
 *
 * 为什么不用 Compose Dialog：Dialog 没法单独锁横屏，HTML5 全屏和返回键也只能在
 * Activity 这一层处理；而转屏如果不写 configChanges，Activity 会重建，WebView 直接重载、
 * 串流中断。
 *
 * 登录态来自 /api/cloud/download（服务端已按需用 stoken 换票），注入 CookieManager 后
 * 云游戏页面会自己换 combo_token，不需要额外塞 header。
 */
class CloudPlayActivity : ComponentActivity() {

    companion object {
        private const val EXTRA_GAME = "game"
        private const val EXTRA_GAME_NAME = "game_name"
        private const val EXTRA_UID = "uid"
        private const val EXTRA_NICKNAME = "nickname"
        private const val EXTRA_URL = "url"

        fun intent(
            context: Context,
            game: String,
            gameName: String,
            uid: String,
            nickname: String,
            url: String
        ): Intent = Intent(context, CloudPlayActivity::class.java).apply {
            putExtra(EXTRA_GAME, game)
            putExtra(EXTRA_GAME_NAME, gameName)
            putExtra(EXTRA_UID, uid)
            putExtra(EXTRA_NICKNAME, nickname)
            putExtra(EXTRA_URL, url)
        }

        // 小米浏览器 UA（Android 16 / 真机机型），带 Mobile 且不带 WebView 的 "; wv" 标记。
        // 报成桌面 UA 会被云游戏平台当成 PC 客户端，下发 16:9 的键鼠流。
        private const val MOBILE_UA =
            "Mozilla/5.0 (Linux; U; Android 16; zh-cn; 2311DRK48C Build/BP2A.250605.031.A3) " +
                "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/135.0.7049.79 Mobile Safari/537.36 " +
                "XiaoMi/MiuiBrowser/20.28.1020918"
    }

    private lateinit var root: FrameLayout
    private lateinit var webView: WebView
    private lateinit var progressBar: ProgressBar
    private lateinit var errorPane: LinearLayout
    private var fullscreenView: View? = null
    private var fullscreenCallback: WebChromeClient.CustomViewCallback? = null
    private var exitDialog: AlertDialog? = null
    private var released = false

    private lateinit var game: String
    private lateinit var gameName: String
    private lateinit var uid: String
    private lateinit var nickname: String
    private lateinit var startUrl: String
    private var gachaArmed = false
    private var gachaCaptured = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        game = intent.getStringExtra(EXTRA_GAME).orEmpty()
        gameName = intent.getStringExtra(EXTRA_GAME_NAME).orEmpty()
        uid = intent.getStringExtra(EXTRA_UID).orEmpty()
        nickname = intent.getStringExtra(EXTRA_NICKNAME).orEmpty()
        startUrl = intent.getStringExtra(EXTRA_URL).orEmpty().ifBlank { "https://ys.mihoyo.com/cloud/" }
        // 开关打开且是原神/星铁时，本次上号尝试顺手抓抽卡链接
        gachaArmed = CloudGachaHistory.isEnabled(this) &&
            game in setOf("genshin", "starrail")

        WindowCompat.setDecorFitsSystemWindows(window, false)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        hideSystemBars()

        root = FrameLayout(this).apply { setBackgroundColor(Color.BLACK) }
        setContentView(root)

        buildWebView()
        buildOverlays()
        if (gachaArmed) showGachaTip()

        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() {
                // 网页内的 HTML5 全屏优先退出，再按一次才问是否结束游戏
                if (fullscreenView != null) hideFullscreen() else confirmExit()
            }
        })

        injectCookiesThenLoad()
    }

    @SuppressLint("SetJavaScriptEnabled")
    private fun buildWebView() {
        webView = WebView(this).apply {
            val web = this
            layoutParams = FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.MATCH_PARENT
            )
            setBackgroundColor(Color.BLACK)
            overScrollMode = View.OVER_SCROLL_NEVER
            // 游戏里长按容易误触文字选择/上下文菜单，直接吃掉
            isLongClickable = false
            setOnLongClickListener { true }
            isFocusable = true
            requestFocus()

            with(settings) {
                javaScriptEnabled = true
                domStorageEnabled = true
                databaseEnabled = true
                mediaPlaybackRequiresUserGesture = false
                useWideViewPort = true
                // 关掉整页缩放：开着时页面会按内容宽度整体缩放，游戏区两侧留白
                loadWithOverviewMode = false
                setInitialScale(0)
                setSupportMultipleWindows(false)
                javaScriptCanOpenWindowsAutomatically = true
                mixedContentMode = WebSettings.MIXED_CONTENT_NEVER_ALLOW
                setSupportZoom(false)
                builtInZoomControls = false
                userAgentString = MOBILE_UA
            }

            CookieManager.getInstance().apply {
                setAcceptCookie(true)
                setAcceptThirdPartyCookies(web, true)
            }

            webViewClient = object : WebViewClient() {
                override fun shouldOverrideUrlLoading(
                    view: WebView,
                    request: WebResourceRequest
                ): Boolean {
                    val scheme = request.url.scheme.orEmpty()
                    if (scheme == "http" || scheme == "https") return false
                    runCatching { startActivity(Intent(Intent.ACTION_VIEW, request.url)) }
                    return true
                }

                override fun onReceivedError(
                    view: WebView?,
                    request: WebResourceRequest?,
                    error: WebResourceError?
                ) {
                    if (request?.isForMainFrame == true) showError()
                }

                // 云游戏里打开「抽卡历史」时，页面会在本地 WebView 加载带 authkey 的 gacha 页；
                // 这里截住这个请求就能拿到链接，不需要读游戏画面。
                override fun shouldInterceptRequest(
                    view: WebView?,
                    request: WebResourceRequest?
                ): WebResourceResponse? {
                    // 这个回调在 WebView 的 IO 线程，写偏好/剪贴板/Toast 必须切回主线程
                    val raw = request?.url?.toString()
                    if (raw != null && gachaArmed && !gachaCaptured) {
                        runOnUiThread { maybeCaptureGacha(raw) }
                    }
                    return super.shouldInterceptRequest(view, request)
                }
            }

            webChromeClient = object : WebChromeClient() {
                override fun onProgressChanged(view: WebView?, newProgress: Int) {
                    progressBar.progress = newProgress
                    progressBar.visibility =
                        if (newProgress in 1..99) View.VISIBLE else View.GONE
                }

                // 云游戏不需要摄像头/麦克风，直接拒绝
                override fun onPermissionRequest(request: PermissionRequest?) {
                    request?.deny()
                }

                override fun onShowCustomView(view: View?, callback: CustomViewCallback?) {
                    if (view == null) return
                    if (fullscreenView != null) {
                        callback?.onCustomViewHidden()
                        return
                    }
                    fullscreenView = view
                    fullscreenCallback = callback
                    root.addView(
                        view,
                        FrameLayout.LayoutParams(
                            ViewGroup.LayoutParams.MATCH_PARENT,
                            ViewGroup.LayoutParams.MATCH_PARENT
                        )
                    )
                    webView.visibility = View.GONE
                    hideSystemBars()
                }

                override fun onHideCustomView() {
                    hideFullscreen()
                }
            }

            root.addView(this)
        }
    }

    private fun buildOverlays() {
        progressBar = ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal).apply {
            max = 100
            visibility = View.GONE
            layoutParams = FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                3.dp()
            ).apply { gravity = Gravity.TOP }
        }
        root.addView(progressBar)

        errorPane = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER
            visibility = View.GONE
            setPadding(40.dp(), 40.dp(), 40.dp(), 40.dp())
            layoutParams = FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.MATCH_PARENT
            )
        }
        errorPane.addView(
            TextView(this).apply {
                text = "云游戏页面加载失败"
                setTextColor(Color.WHITE)
                textSize = 16f
                gravity = Gravity.CENTER
            }
        )
        errorPane.addView(
            Button(this).apply {
                text = "重试"
                setOnClickListener {
                    errorPane.visibility = View.GONE
                    webView.visibility = View.VISIBLE
                    webView.reload()
                }
            },
            LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT,
                ViewGroup.LayoutParams.WRAP_CONTENT
            ).apply { topMargin = 16.dp() }
        )
        errorPane.addView(
            Button(this).apply {
                text = "用浏览器打开"
                setOnClickListener {
                    val target = webView.url?.takeIf { it.startsWith("http") } ?: startUrl
                    runCatching { startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(target))) }
                }
            },
            LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT,
                ViewGroup.LayoutParams.WRAP_CONTENT
            ).apply { topMargin = 8.dp() }
        )
        root.addView(errorPane)
    }

    /** 先拉新鲜 Cookie 注入，再打开云游戏页；拉取失败也照常打开，用户可在页面内手动登录。 */
    private fun injectCookiesThenLoad() {
        lifecycleScope.launch {
            val result = runCatching { CloudApi.download(Session.token(), game, uid) }
            result.onFailure {
                Toast.makeText(
                    this@CloudPlayActivity,
                    "账号信息读取失败，可能需要在页面内登录",
                    Toast.LENGTH_LONG
                ).show()
            }
            CookieHarvester.inject(game, startUrl, result.getOrNull()?.cookies.orEmpty())
            if (!isFinishing) webView.loadUrl(startUrl)
        }
    }

    private fun showError() {
        progressBar.visibility = View.GONE
        webView.visibility = View.INVISIBLE
        errorPane.visibility = View.VISIBLE
    }

    /** 顶部悬浮提示：只在本次上号开启抽卡抓取时出现，8 秒后自动消失。 */
    private fun showGachaTip() {
        val tip = TextView(this).apply {
            text = "请前往抽卡历史记录页面，软件会尝试自动获取抽卡链接"
            setTextColor(Color.WHITE)
            textSize = 13f
            gravity = Gravity.CENTER
            setBackgroundColor(0xCC000000.toInt())
            setPadding(24.dp(), 12.dp(), 24.dp(), 12.dp())
            layoutParams = FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT
            ).apply { gravity = Gravity.TOP }
        }
        root.addView(tip)
        tip.postDelayed({ runCatching { root.removeView(tip) } }, 8000)
    }

    /** 匹配 webstatic.mihoyo.com 上带 authkey 的抽卡历史页，抓到后复制并记录。 */
    private fun maybeCaptureGacha(raw: String) {
        if (!gachaArmed || gachaCaptured) return
        val uri = runCatching { Uri.parse(raw) }.getOrNull() ?: return
        if (uri.host != "webstatic.mihoyo.com") return
        if (!uri.path.orEmpty().contains("gacha", ignoreCase = true)) return
        val authkey = uri.getQueryParameter("authkey").orEmpty()
        if (authkey.isBlank()) return
        gachaCaptured = true
        CloudGachaHistory.add(
            this,
            GachaCapture(
                time = System.currentTimeMillis(),
                link = raw,
                gameName = gameName.ifBlank { if (game == "starrail") "星穹铁道" else "原神" },
                gameUid = uid,
                nickname = nickname
            )
        )
        CloudGachaHistory.copy(this, raw)
        Toast.makeText(this, "已获取抽卡链接，并复制到剪贴板", Toast.LENGTH_LONG).show()
    }

    private fun hideFullscreen() {
        val view = fullscreenView ?: return
        root.removeView(view)
        fullscreenView = null
        webView.visibility = View.VISIBLE
        fullscreenCallback?.onCustomViewHidden()
        fullscreenCallback = null
    }

    private fun confirmExit() {
        if (exitDialog?.isShowing == true) return
        exitDialog = AlertDialog.Builder(this)
            .setTitle("退出游戏")
            .setMessage("是否退出游戏，建议先在云游戏退出后再关闭软件")
            .setNegativeButton("继续游戏", null)
            .setPositiveButton("退出") { _, _ -> closePlay() }
            .create()
            .also { it.show() }
    }

    private fun closePlay() {
        if (released) return
        runCatching {
            CookieManager.getInstance().flush()
            webView.stopLoading()
            webView.loadUrl("about:blank")
        }
        finish()
    }

    private fun hideSystemBars() {
        WindowInsetsControllerCompat(window, window.decorView).apply {
            hide(WindowInsetsCompat.Type.systemBars())
            systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        }
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) hideSystemBars()
    }

    override fun onResume() {
        super.onResume()
        if (::webView.isInitialized) webView.onResume()
        hideSystemBars()
    }

    override fun onPause() {
        if (::webView.isInitialized) webView.onPause()
        super.onPause()
    }

    override fun onDestroy() {
        released = true
        if (::root.isInitialized && ::webView.isInitialized) {
            runCatching {
                hideFullscreen()
                root.removeView(webView)
                webView.stopLoading()
                webView.webChromeClient = null
                webView.destroy()
            }
        }
        super.onDestroy()
    }

    private fun Int.dp(): Int = (this * resources.displayMetrics.density).toInt()
}
