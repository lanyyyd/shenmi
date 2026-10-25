package com.teatt.pocket.feature.wireguard

import android.content.Context

/**
 * WireGuard 配置的本地持久化。
 *
 * 存在的理由（两条，都必要）：
 *  1. 自动重连发生在 [WgService] 里，而 Service 可能被系统杀掉重启，
 *     那时内存里的配置就丢了。
 *  2. **离线可用**：首次联网拿到配置后存到本地，之后即使完全没网，
 *     打开「小工具」照样能连上 —— 逻辑对齐原「神秘WiFi」项目的
 *     loadConfigWithCache()：缓存优先，后台再尝试刷新。
 *
 * 同时缓存一份配额信息（已用流量 / 上限），离线时面板也能显示，
 * 而不是一片空白。
 */
object WgConfigCache {

    private const val PREFS = "pocket_wg_config"
    private const val KEY_CONFIG = "config"
    private const val KEY_TRAFFIC_USED = "traffic_used"
    private const val KEY_TRAFFIC_LIMIT = "traffic_limit"
    private const val KEY_PROFILE_AT = "profile_at"

    /** 存配置本身。 */
    fun save(context: Context, config: String) {
        context.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .edit().putString(KEY_CONFIG, config).apply()
    }

    /** 取缓存的配置；没有则 null（表示必须联网获取一次）。 */
    fun load(context: Context): String? =
        context.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .getString(KEY_CONFIG, null)

    /** 缓存的配额信息，供离线时显示。 */
    fun loadTraffic(context: Context): Pair<Long, Long> {
        val sp = context.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        return sp.getLong(KEY_TRAFFIC_USED, 0L) to sp.getLong(KEY_TRAFFIC_LIMIT, 0L)
    }

    /** 记录一次成功的配额快照。 */
    fun saveTraffic(context: Context, used: Long, limit: Long) {
        context.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .edit()
            .putLong(KEY_TRAFFIC_USED, used)
            .putLong(KEY_TRAFFIC_LIMIT, limit)
            .putLong(KEY_PROFILE_AT, System.currentTimeMillis())
            .apply()
    }

    /**
     * 清空配置缓存。
     *
     * 只在「用户主动断开 / 退出 WiFi 账号」时调用 ——
     * 断开连接不代表配置失效，下次打开仍然要能离线连上。
     */
    fun clear(context: Context) {
        context.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .edit()
            .remove(KEY_CONFIG)
            .remove(KEY_TRAFFIC_USED)
            .remove(KEY_TRAFFIC_LIMIT)
            .remove(KEY_PROFILE_AT)
            .apply()
    }
}
