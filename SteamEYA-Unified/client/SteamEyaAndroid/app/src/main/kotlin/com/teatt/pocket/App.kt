package com.teatt.pocket

import android.annotation.SuppressLint
import android.app.Application
import android.webkit.WebView
import coil3.ImageLoader
import coil3.PlatformContext
import coil3.SingletonImageLoader
import com.teatt.pocket.core.Session

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
     * Coil 默认 respectCacheHeaders=true：网关的 /uploads 由 StaticFiles/nginx 提供，
     * 没有 Cache-Control，Coil 每次冷启动都会把这批图重新拉一遍（≈70MB）。
     * 这里关掉它，按 URL 直接吃本地磁盘缓存。
     */
    override fun newImageLoader(context: PlatformContext): ImageLoader =
        ImageLoader.Builder(context)
            .respectCacheHeaders(false)
            .build()

    companion object {
        /** 只持有 Application，不会泄漏 Activity；WebView 资源缓存与更新下载要用。 */
        lateinit var instance: App
            private set
    }
}
