package com.teatt.pocket

import android.annotation.SuppressLint
import android.app.Application
import android.webkit.WebView
import com.teatt.pocket.core.Session

@SuppressLint("StaticFieldLeak")
class App : Application() {

    override fun onCreate() {
        super.onCreate()
        instance = this
        // 启动即恢复登录态：首帧就要判断进登录页还是主界面
        Session.init(this)

        if (BuildConfig.DEBUG) {
            WebView.setWebContentsDebuggingEnabled(true)
        }
    }

    companion object {
        /** 只持有 Application，不会泄漏 Activity；WebView 资源缓存与更新下载要用。 */
        lateinit var instance: App
            private set
    }
}
