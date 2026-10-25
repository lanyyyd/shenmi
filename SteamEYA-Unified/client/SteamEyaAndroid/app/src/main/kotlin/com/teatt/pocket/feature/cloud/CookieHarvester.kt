package com.teatt.pocket.feature.cloud

import android.net.Uri
import android.webkit.CookieManager
import com.teatt.pocket.core.CookieDto

/**
 * 从 WebView 的 CookieManager 收割登录态。
 *
 * 必须逐个域名去问：`getCookie(url)` 只返回匹配该 URL 的 cookie，
 * passport 域的票据不会出现在游戏域名上，只问一个域名会漏掉关键项。
 * 同一个名字以先出现的为准（列表前面的域名更"主"）。
 */
object CookieHarvester {

    private val MI_HOYO = listOf(
        "https://ys.mihoyo.com",
        "https://sr.mihoyo.com",
        "https://user.mihoyo.com",
        "https://api-takumi.mihoyo.com",
        "https://passport-api.mihoyo.com",
        "https://mihoyo.com"
    )

    private val WANMEI = listOf(
        "https://yh.wanmei.com",
        "https://wanmei.com"
    )

    private val BILI = listOf(
        "https://www.bilibili.com",
        "https://passport.bilibili.com",
        "https://api.bilibili.com"
    )

    /** 按游戏挑要遍历的域名；自定义游戏用它自己的地址推父域。 */
    fun domainsFor(game: String, url: String): List<String> = when (game) {
        "genshin", "starrail" -> MI_HOYO
        "yihuan" -> WANMEI
        "bilibili" -> BILI
        else -> {
            val host = runCatching { Uri.parse(url).host }.getOrNull()
            if (host.isNullOrBlank()) {
                emptyList()
            } else {
                val parts = host.split(".")
                val root = if (parts.size >= 2) parts.takeLast(2).joinToString(".") else host
                listOf("https://$host", "https://$root")
            }
        }
    }

    fun harvest(domains: List<String>): List<CookieDto> {
        val manager = CookieManager.getInstance()
        val collected = LinkedHashMap<String, CookieDto>()

        domains.forEach { domainUrl ->
            val raw = runCatching { manager.getCookie(domainUrl) }.getOrNull()
            if (raw.isNullOrBlank()) return@forEach
            val host = runCatching { Uri.parse(domainUrl).host }.getOrNull().orEmpty()

            raw.split(";").forEach { pair ->
                val index = pair.indexOf('=')
                if (index <= 0) return@forEach
                val name = pair.substring(0, index).trim()
                val value = pair.substring(index + 1).trim()
                if (name.isEmpty() || value.isEmpty()) return@forEach
                collected.putIfAbsent(name, CookieDto(name = name, value = value, domain = host, path = "/"))
            }
        }

        return collected.values.toList()
    }

    /** 粘贴导入：把 "k=v; k2=v2" 解析成服务端要的数组。 */
    fun parse(raw: String, host: String = ""): List<CookieDto> {
        val collected = LinkedHashMap<String, CookieDto>()
        raw.replace("\n", ";").split(";").forEach { pair ->
            val index = pair.indexOf('=')
            if (index <= 0) return@forEach
            val name = pair.substring(0, index).trim()
            val value = pair.substring(index + 1).trim()
            if (name.isEmpty() || value.isEmpty()) return@forEach
            collected[name] = CookieDto(name = name, value = value, domain = host, path = "/")
        }
        return collected.values.toList()
    }
}
