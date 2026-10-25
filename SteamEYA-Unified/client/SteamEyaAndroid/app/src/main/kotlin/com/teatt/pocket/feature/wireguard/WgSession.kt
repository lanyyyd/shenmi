package com.teatt.pocket.feature.wireguard

import android.content.Context
import android.content.SharedPreferences

/**
 * 「神秘WiFi」账号的登录态。
 *
 * 与 [com.teatt.pocket.core.Session]（SteamEYA 主账号）**完全独立**：
 * 退出 WiFi 不影响主账号，退出主账号也不清 WiFi 的 token。
 *
 * 用 SharedPreferences 而不是 DataStore：这个值需要在非 Compose 的地方
 * （Service、OkHttp 拦截器）同步读取，SharedPreferences 的同步读更直接。
 */
object WgSession {

    private const val PREFS = "pocket_wg_session"
    private const val KEY_TOKEN = "auth_token"
    private const val KEY_USER = "username"

    @Volatile private var prefs: SharedPreferences? = null

    fun init(context: Context) {
        if (prefs == null) {
            synchronized(this) {
                if (prefs == null) {
                    prefs = context.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                }
            }
        }
    }

    private fun sp(): SharedPreferences =
        prefs ?: throw IllegalStateException("WgSession.init(context) 未调用")

    /** 同步读 token（OkHttp / Service 里要用）。 */
    fun token(): String = sp().getString(KEY_TOKEN, "") ?: ""

    fun username(): String = sp().getString(KEY_USER, "") ?: ""

    val loggedIn: Boolean get() = token().isNotBlank()

    fun save(token: String, username: String) {
        sp().edit()
            .putString(KEY_TOKEN, token)
            .putString(KEY_USER, username)
            .apply()
    }

    fun clear() {
        sp().edit().clear().apply()
    }
}
