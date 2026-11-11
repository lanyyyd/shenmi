package com.teatt.pocket.feature.cloud

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import org.json.JSONArray
import org.json.JSONObject

/** 云游戏里自动抓到的抽卡链接记录。 */
data class GachaCapture(
    val time: Long,
    val link: String,
    val gameName: String,
    val gameUid: String,
    val nickname: String
)

/**
 * 抽卡链接自动抓取的开关 + 本地历史。
 *
 * 开关打开后，进云原神/云崩铁时会监听页面里加载的抽卡历史页（webstatic.mihoyo.com 的
 * gacha 页，带 authkey），抓到就复制到剪贴板并记一条历史，不经过服务端。
 */
object CloudGachaHistory {

    private const val PREF = "cloud_gacha"
    private const val KEY_ENABLED = "enabled"
    private const val KEY_LIST = "history"
    private const val MAX_ITEMS = 50

    private fun prefs(context: Context) = context.getSharedPreferences(PREF, Context.MODE_PRIVATE)

    fun isEnabled(context: Context): Boolean = prefs(context).getBoolean(KEY_ENABLED, false)

    fun setEnabled(context: Context, enabled: Boolean) {
        prefs(context).edit().putBoolean(KEY_ENABLED, enabled).apply()
    }

    fun list(context: Context): List<GachaCapture> {
        val raw = prefs(context).getString(KEY_LIST, "").orEmpty()
        if (raw.isBlank()) return emptyList()
        return runCatching {
            val arr = JSONArray(raw)
            (0 until arr.length()).map { i ->
                val o = arr.optJSONObject(i) ?: JSONObject()
                GachaCapture(
                    time = o.optLong("time"),
                    link = o.optString("link"),
                    gameName = o.optString("game"),
                    gameUid = o.optString("uid"),
                    nickname = o.optString("nickname")
                )
            }.filter { it.link.isNotBlank() }
        }.getOrDefault(emptyList())
    }

    /** 新增一条；同样的链接只保留最新一条，最多 [MAX_ITEMS] 条。 */
    fun add(context: Context, item: GachaCapture) {
        val items = list(context).filterNot { it.link == item.link }
        val merged = (listOf(item) + items).take(MAX_ITEMS)
        val arr = JSONArray()
        merged.forEach { c ->
            arr.put(
                JSONObject().apply {
                    put("time", c.time)
                    put("link", c.link)
                    put("game", c.gameName)
                    put("uid", c.gameUid)
                    put("nickname", c.nickname)
                }
            )
        }
        prefs(context).edit().putString(KEY_LIST, arr.toString()).apply()
    }

    fun copy(context: Context, text: String) {
        val cm = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        cm.setPrimaryClip(ClipData.newPlainText("抽卡链接", text))
    }
}
