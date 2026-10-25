package com.teatt.pocket.feature.wireguard

import android.content.Context

/**
 * WireGuard 配置的本地持久化。
 *
 * 存在的理由：自动重连发生在 [WgService] 里，而 Service 可能被系统杀掉重启，
 * 那时内存里的配置就丢了。把配置落到 SharedPreferences，
 * 进程重启后仍能拿回来重连（不用再请求服务端）。
 */
object WgConfigCache {

    private const val PREFS = "pocket_wg_config"
    private const val KEY_CONFIG = "config"

    fun save(context: Context, config: String) {
        context.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .edit().putString(KEY_CONFIG, config).apply()
    }

    fun load(context: Context): String? =
        context.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .getString(KEY_CONFIG, null)

    fun clear(context: Context) {
        context.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .edit().remove(KEY_CONFIG).apply()
    }
}
