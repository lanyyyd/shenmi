package com.teatt.pocket

import android.app.Application
import android.webkit.WebView
import com.teatt.pocket.core.Session

class App : Application() {
    override fun onCreate() {
        super.onCreate()
        // 启动即恢复登录态：首帧就要判断进登录页还是主界面
        Session.init(this)

        if (BuildConfig.DEBUG) {
            WebView.setWebContentsDebuggingEnabled(true)
        }
    }
}
