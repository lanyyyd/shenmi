package com.teatt.pocket.ui

import android.net.Uri
import android.webkit.CookieManager
import android.webkit.WebResourceResponse
import com.teatt.pocket.App
import okhttp3.Cache
import okhttp3.OkHttpClient
import okhttp3.Request
import java.io.File

/**
 * 网页静态资源（图片/字体）的本地磁盘缓存。
 *
 * 现状：网关的静态资源只带 ETag/Last-Modified，没有 Cache-Control，
 * WebView 每次进「吃什么」都要把整页菜图重新协商一遍，弱网下就是"打开就转圈、流量哗哗走"。
 *
 * 这里用 OkHttp 的磁盘缓存顶掉那段协商：命中缓存直接返回，7 天内不再发请求。
 * 只缓存图片和字体——HTML/JS/CSS 不缓存，避免前端发版后拿到旧脚本。
 *
 * 命中与否由 OkHttp 的 CacheStrategy 决定；网络响应统一被改写成
 * `Cache-Control: public, max-age=604800`，否则服务端没给缓存头时 OkHttp 不会落盘。
 */
object WebAssetCache {

    private const val MAX_BYTES = 64L * 1024 * 1024
    private const val MAX_AGE_SECONDS = 7 * 24 * 60 * 60

    private val EXTS = setOf(
        "png", "jpg", "jpeg", "webp", "gif", "svg", "ico", "bmp", "avif",
        "woff", "woff2", "ttf", "otf"
    )

    private val client: OkHttpClient by lazy {
        OkHttpClient.Builder()
            .cache(Cache(File(App.instance.cacheDir, "web-assets"), MAX_BYTES))
            .addNetworkInterceptor { chain ->
                chain.proceed(chain.request()).newBuilder()
                    .removeHeader("Pragma")
                    .header("Cache-Control", "public, max-age=$MAX_AGE_SECONDS")
                    .build()
            }
            .build()
    }

    fun isCacheable(url: String): Boolean {
        if (!url.startsWith("https://")) return false
        val path = runCatching { Uri.parse(url).path }.getOrNull().orEmpty()
        if (path.startsWith("/api/")) return false
        val ext = path.substringAfterLast('.', "").lowercase()
        return ext in EXTS
    }

    /** 同步取（WebView 在子线程回调），失败返回 null 由 WebView 走默认网络。 */
    fun fetch(url: String): WebResourceResponse? {
        return try {
            val builder = Request.Builder().url(url)
            val cookie = CookieManager.getInstance().getCookie(url)
            if (!cookie.isNullOrBlank()) builder.header("Cookie", cookie)

            val response = client.newCall(builder.build()).execute()
            if (!response.isSuccessful) {
                response.close()
                return null
            }
            val body = response.body
            val mime = body.contentType()?.toString()?.substringBefore(';') ?: "application/octet-stream"
            val headers = response.headers.toMultimap()
                .mapValues { (_, v) -> v.firstOrNull().orEmpty() }
            // 流关闭时 OkHttp 会归还连接，这里不要提前 close response
            WebResourceResponse(mime, null, response.code, response.message, headers, body.byteStream())
        } catch (_: Exception) {
            null
        }
    }
}
