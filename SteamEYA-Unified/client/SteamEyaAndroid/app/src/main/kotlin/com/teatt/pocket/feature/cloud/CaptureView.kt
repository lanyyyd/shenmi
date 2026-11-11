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
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
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
import org.json.JSONTokener

/**
 * 云游戏登录抓取页：WebView 里让用户手动登录，登录完点底部按钮，
 * 再从 CookieManager 收割各个域名下的 Cookie。
 *
 * 米哈游云游戏还需要 x-rpc-combo_token 才能判定账号有效 / 查剩余时长，
 * 而它不在 Cookie 里（页面是放在请求头里的），所以要在页面脚本执行前注入钩子，
 * 把 fetch / XHR 上的 x-rpc-combo_token 记下来，抓取时一并取走。
 */
private const val COMBO_HOOK_JS = """
(function () {
  try {
    window.__smCombo = window.__smCombo || '';
    function notify(v) {
      try {
        if (window.top && window.top !== window) window.top.postMessage({ __smCombo: String(v) }, '*');
      } catch (e) {}
    }
    try {
      window.addEventListener('message', function (e) {
        var d = e && e.data;
        if (d && d.__smCombo) window.__smCombo = String(d.__smCombo);
      });
    } catch (e) {}
    function hook(headers) {
      if (!headers) return;
      try {
        if (typeof headers.forEach === 'function' && !Array.isArray(headers)) {
          headers.forEach(function (v, k) {
            if (k && String(k).toLowerCase() === 'x-rpc-combo_token' && v) {
              window.__smCombo = String(v); notify(v);
            }
          });
        } else {
          Object.keys(headers).forEach(function (k) {
            if (String(k).toLowerCase() === 'x-rpc-combo_token' && headers[k]) {
              window.__smCombo = String(headers[k]); notify(headers[k]);
            }
          });
        }
      } catch (e) {}
    }
    var of = window.fetch;
    if (of) {
      window.fetch = function (input, init) {
        try {
          if (init && init.headers) hook(init.headers);
          if (input && input.headers) hook(input.headers);
        } catch (e) {}
        return of.apply(this, arguments);
      };
    }
    var ox = XMLHttpRequest.prototype.setRequestHeader;
    XMLHttpRequest.prototype.setRequestHeader = function (name, value) {
      try {
        if (name && String(name).toLowerCase() === 'x-rpc-combo_token' && value) {
          window.__smCombo = String(value); notify(value);
        }
      } catch (e) {}
      return ox.apply(this, arguments);
    };
  } catch (e) {}
})();
"""

/**
 * 抓取时兜底：先看网络钩子，再翻 localStorage / sessionStorage 里 key 含 combo 的值，
 * 最后看 document.cookie。页面把凭证落到缓存里时（用户说的「重开 App 就能抓到」）靠这一段。
 */
private const val COMBO_READ_JS = """
(function () {
  try { if (window.__smCombo) return String(window.__smCombo); } catch (e) {}
  function scan(store) {
    try {
      for (var i = 0; i < store.length; i++) {
        var k = store.key(i) || '';
        if (!/combo/i.test(k)) continue;
        var v = store.getItem(k);
        if (!v) continue;
        v = String(v);
        try {
          var o = JSON.parse(v);
          if (o && typeof o === 'object') {
            for (var kk in o) if (/combo/i.test(kk) && o[kk]) return String(o[kk]);
            if (o.value) return String(o.value);
            if (o.token) return String(o.token);
            if (o.data && typeof o.data === 'string') return o.data;
          }
        } catch (e) {}
        return v;
      }
    } catch (e) {}
    return '';
  }
  var v = scan(window.localStorage) || scan(window.sessionStorage);
  if (v) return v;
  var parts = (document.cookie || '').split(';');
  for (var j = 0; j < parts.length; j++) {
    var p = parts[j].trim();
    var idx = p.indexOf('=');
    if (idx > 0 && /combo/i.test(p.substring(0, idx))) {
      try { return decodeURIComponent(p.substring(idx + 1)); } catch (e) { return p.substring(idx + 1); }
    }
  }
  return '';
})();
"""

/** evaluateJavascript 返回值是 JSON 字面量（字符串会带引号），这里解回普通字符串。 */
private fun jsString(raw: String?): String {
    val text = raw?.trim().orEmpty()
    if (text.isEmpty() || text == "null") return ""
    return runCatching { JSONTokener(text).nextValue() as? String }.getOrNull().orEmpty()
}

@SuppressLint("SetJavaScriptEnabled")
@Composable
fun CaptureView(
    game: CloudGameDto,
    onCancel: () -> Unit,
    onHarvest: (List<CookieDto>, String) -> Unit
) {
    var webView by remember { mutableStateOf<WebView?>(null) }
    var progress by remember { mutableStateOf(0) }
    val startUrl = game.url.ifBlank { "https://ys.mihoyo.com/cloud/" }

    BackHandler {
        val view = webView
        if (view != null && view.canGoBack()) view.goBack() else onCancel()
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
                    "登录并进入云游戏后，点底部按钮抓取",
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

                    // 在页面脚本执行前注入，才能拿到登录后第一次云游戏请求里的 combo_token
                    val hooked = WebViewFeature.isFeatureSupported(WebViewFeature.DOCUMENT_START_SCRIPT) &&
                        runCatching {
                            WebViewCompat.addDocumentStartJavaScript(web, COMBO_HOOK_JS, setOf("*"))
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
                            if (!hooked) runCatching { view.evaluateJavascript(COMBO_HOOK_JS, null) }
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
                            onHarvest(CookieHarvester.harvest(domains), jsString(raw))
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

