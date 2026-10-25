package com.teatt.pocket

import android.annotation.SuppressLint
import android.app.Application
import android.webkit.WebView
import coil3.ImageLoader
import coil3.PlatformContext
import coil3.SingletonImageLoader
import coil3.disk.DiskCache
import com.teatt.pocket.core.Session
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
     * 图片磁盘缓存显式固定到 cacheDir/image_cache 并给到 256MB。
     * 网关的 /uploads 是 StaticFiles 直出、没有 Cache-Control，靠默认策略容易退化；
     * 这里把缓存目录和上限写死，冷启动后直接按 URL 命中，不再重下一整批图。
     */
    override fun newImageLoader(context: PlatformContext): ImageLoader =
        ImageLoader.Builder(context)
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
