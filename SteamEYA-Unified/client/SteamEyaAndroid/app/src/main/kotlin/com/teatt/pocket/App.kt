package com.teatt.pocket

import android.annotation.SuppressLint
import android.app.Application
import android.webkit.WebView
import coil3.ImageLoader
import coil3.PlatformContext
import coil3.SingletonImageLoader
import coil3.disk.DiskCache
import coil3.network.okhttp.OkHttpNetworkFetcherFactory
import com.teatt.pocket.core.Session
import okhttp3.OkHttpClient
import okio.Path.Companion.toOkioPath

@SuppressLint("StaticFieldLeak")
class App : Application(), SingletonImageLoader.Factory {

    override fun onCreate() {
        super.onCreate()
        instance = this
        // 启动即恢复登录态：首帧就要判断进登录页还是主界面
        Session.init(this)

        if (BuildConfig.DEBUG) {
            WebView.setWebContentsDebuggingEnabled(true)
        }
    }

    /**
     * 图片加载器：
     * 1) 网络层强制把 /uploads 的响应改写成 `Cache-Control: max-age=30d`。
     *    网关原本不带缓存头，Coil 认为每次都过期，冷启动会把整批图重下一遍（≈70MB）。
     * 2) 磁盘缓存固定到 cacheDir/image_cache，上限 256MB，保证跨进程复用。
     */
    private val imageClient: OkHttpClient by lazy {
        OkHttpClient.Builder()
            .addNetworkInterceptor { chain ->
                chain.proceed(chain.request()).newBuilder()
                    .header("Cache-Control", "public, max-age=2592000")
                    .build()
            }
            .build()
    }

    override fun newImageLoader(context: PlatformContext): ImageLoader =
        ImageLoader.Builder(context)
            .components { add(OkHttpNetworkFetcherFactory(callFactory = { imageClient })) }
            .diskCache {
                DiskCache.Builder()
                    .directory(context.cacheDir.resolve("image_cache").toOkioPath())
                    .maxSizeBytes(256L * 1024 * 1024)
                    .build()
            }
            .build()

    companion object {
        /** 只持有 Application，不会泄漏 Activity；WebView 资源缓存与更新下载要用。 */
        lateinit var instance: App
            private set
    }
}
