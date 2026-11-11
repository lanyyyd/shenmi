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

    /** 决定登录态的 Cookie：换号上号前必须清掉旧值。 */
    private val AUTH_COOKIE_NAMES = listOf(
        "stoken", "stoken_v2", "ltoken", "ltoken_v2",
        "cookie_token", "cookie_token_v2",
        "account_id", "account_id_v2", "account_mid_v2",
        "ltuid", "ltuid_v2", "ltmid_v2", "mid",
        "login_ticket", "login_ticket_token", "login_uid"
    )

    /**
     * 把服务端下发的 Cookie 写回 WebView（云游戏上号用）。
     *
     * 收割时拿到的 domain 是"当时查询的域名"，不是 cookie 的真实域，直接按它写会漏掉
     * passport / 游戏域。统一按游戏根域写（mihoyo.com / wanmei.com / bilibili.com），
     * 和真实站点一样让子域共享，登录态才完整。
     *
     * 写入前先删掉旧账号残留的登录 Cookie：CookieManager 按「名字 + 域名 + 路径」存，
     * 以前在 sr.mihoyo.com 等具体域名下存过的同名旧值会盖过新注入的 .mihoyo.com 值，
     * 表现就是必须手动清一次 Cookie 才能上号（电脑端同一个问题已在上号前清 profile）。
     * 只删登录相关的 Cookie，不影响网页 tab 的会话。
     */
    fun inject(game: String, url: String, cookies: List<CookieDto>) {
        if (cookies.isEmpty()) return
        val root = when (game) {
            "genshin", "starrail" -> "mihoyo.com"
            "yihuan" -> "wanmei.com"
            "bilibili" -> "bilibili.com"
            else -> runCatching { Uri.parse(url).host }.getOrNull().orEmpty()
                .split(".").takeLast(2).joinToString(".")
        }
        if (root.isBlank() || !root.contains(".")) return

        val manager = CookieManager.getInstance()
        manager.setAcceptCookie(true)
        val origin = "https://$root/"
        // 每个具体域名删一次（清掉 host-only 的旧值），再按根域删一次（清掉 domain 级旧值）
        val hosts = domainsFor(game, url).ifEmpty { listOf(origin) }
        // 已知登录 Cookie + 本次要写入的 Cookie 名（异环/B站等非米哈游站点靠后半部分覆盖）
        val names = (AUTH_COOKIE_NAMES + cookies.map { it.name }).filter { it.isNotBlank() }.distinct()
        hosts.forEach { host ->
            names.forEach { name ->
                runCatching {
                    manager.setCookie(host, "$name=deleted; Max-Age=0; path=/")
                    manager.setCookie(host, "$name=deleted; Max-Age=0; domain=.$root; path=/")
                }
            }
        }
        cookies.forEach { c ->
            if (c.name.isBlank() || c.value.isBlank()) return@forEach
            runCatching {
                manager.setCookie(origin, "${c.name}=${c.value}; domain=.$root; path=/")
            }
        }
        manager.flush()
    }
}
