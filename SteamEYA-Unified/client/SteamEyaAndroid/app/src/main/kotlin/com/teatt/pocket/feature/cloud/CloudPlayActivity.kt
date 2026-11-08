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
        private const val EXTRA_UID = "uid"
        private const val EXTRA_URL = "url"

        fun intent(context: Context, game: String, uid: String, url: String): Intent =
            Intent(context, CloudPlayActivity::class.java).apply {
                putExtra(EXTRA_GAME, game)
                putExtra(EXTRA_UID, uid)
                putExtra(EXTRA_URL, url)
            }
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
    private lateinit var uid: String
    private lateinit var startUrl: String

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        game = intent.getStringExtra(EXTRA_GAME).orEmpty()
        uid = intent.getStringExtra(EXTRA_UID).orEmpty()
        startUrl = intent.getStringExtra(EXTRA_URL).orEmpty().ifBlank { "https://ys.mihoyo.com/cloud/" }

        WindowCompat.setDecorFitsSystemWindows(window, false)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        hideSystemBars()

        root = FrameLayout(this).apply { setBackgroundColor(Color.BLACK) }
        setContentView(root)

        buildWebView()
        buildOverlays()

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

            with(settings) {
                javaScriptEnabled = true
                domStorageEnabled = true
                databaseEnabled = true
                mediaPlaybackRequiresUserGesture = false
                useWideViewPort = true
                loadWithOverviewMode = true
                setSupportMultipleWindows(false)
                javaScriptCanOpenWindowsAutomatically = true
                mixedContentMode = WebSettings.MIXED_CONTENT_NEVER_ALLOW
                setSupportZoom(false)
                builtInZoomControls = false
                // 桌面 UA：云游戏网页版对移动 UA 可能跳去下载 App
                userAgentString = userAgentString.replace("Mobile", "").replace("Android", "")
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
