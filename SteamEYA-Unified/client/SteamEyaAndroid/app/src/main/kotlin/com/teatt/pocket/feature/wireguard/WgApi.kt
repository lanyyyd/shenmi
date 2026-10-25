package com.teatt.pocket.feature.wireguard

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import java.io.IOException
import java.util.concurrent.TimeUnit

/**
 * 「神秘WiFi」服务端 API 客户端。
 *
 * 这套服务端是**独立部署**的（47.104.210.199:8999），有自己的一套用户表和 JWT，
 * 与 SteamEYA 的账号完全独立。所以这里单独一个 HTTP 客户端，
 * 不复用 core.Api（那个指向 chat.teatt.top）。
 *
 * 服务端只有 HTTP，所以 App 的 network_security_config 里放行了这一个 IP。
 */
object WgApi {

    /** 「神秘WiFi」服务端地址（明文 HTTP，已在 network_security_config 放行）。 */
    const val BASE = "http://47.104.210.199:8999"

    private val json = Json {
        ignoreUnknownKeys = true
        isLenient = true
        coerceInputValues = true
    }

    private val client = OkHttpClient.Builder()
        .connectTimeout(8, TimeUnit.SECONDS)
        .readTimeout(10, TimeUnit.SECONDS)
        .writeTimeout(10, TimeUnit.SECONDS)
        // 整体调用上限：离线时别让界面等太久（缓存优先，后台刷新失败就放弃）
        .callTimeout(12, TimeUnit.SECONDS)
        .retryOnConnectionFailure(true)
        .build()

    private val JSON_MEDIA = "application/json; charset=utf-8".toMediaType()

    // ---------------- 传输模型 ----------------
    //
    // 字段名严格对齐服务端（shenmi-wifi-server/app/main.py）的返回结构：
    //   POST /api/login     -> {token, user:{id, username, expire_at, traffic_limit_bytes, is_admin}}
    //   GET  /api/profile   -> {id, username, is_active, is_admin, created_at,
    //                          expire_at, traffic_limit_bytes, traffic_used_bytes,
    //                          traffic_rx, traffic_tx, has_vpn_config}
    //   GET  /api/vpn-config-> {config, client_ip, traffic_used, traffic_limit}
    // 服务端全是 snake_case，所以这里用 @SerialName 显式映射。

    @Serializable
    data class LoginUser(
        @SerialName("id") val id: Int = 0,
        @SerialName("username") val username: String = "",
        @SerialName("expire_at") val expireAt: Double = 0.0,
        @SerialName("traffic_limit_bytes") val trafficLimitBytes: Long = 0,
        @SerialName("is_admin") val isAdmin: Boolean = false
    )

    @Serializable
    data class LoginResult(
        @SerialName("token") val token: String? = null,
        @SerialName("user") val user: LoginUser? = null,
        val success: Boolean = false,
        val error: String? = null
    ) {
        /** 顶层取不到时回落到嵌套的 user 对象。 */
        val username: String get() = user?.username.orEmpty()
        val trafficLimit: Long get() = user?.trafficLimitBytes ?: 0
        val expireAt: Long get() = user?.expireAt?.toLong() ?: 0
    }

    @Serializable
    data class UserProfile(
        @SerialName("id") val id: Int = 0,
        @SerialName("username") val username: String = "",
        @SerialName("is_active") val isActive: Boolean = true,
        @SerialName("is_admin") val isAdmin: Boolean = false,
        @SerialName("expire_at") val expireAt: Double = 0.0,
        @SerialName("traffic_limit_bytes") val trafficLimit: Long = 0,
        @SerialName("traffic_used_bytes") val trafficUsed: Long = 0,
        @SerialName("traffic_rx") val trafficRx: Long = 0,
        @SerialName("traffic_tx") val trafficTx: Long = 0,
        @SerialName("has_vpn_config") val hasVpnConfig: Boolean = false,
        val success: Boolean = true,
        val error: String? = null
    )

    @Serializable
    data class ConfigResult(
        @SerialName("config") val config: String? = null,
        @SerialName("client_ip") val clientIp: String = "",
        @SerialName("traffic_used") val trafficUsed: Long = 0,
        @SerialName("traffic_limit") val trafficLimit: Long = 0,
        val success: Boolean = false,
        val error: String? = null
    )

    // ---------------- 接口 ----------------

    /** 登录。服务端没有注册入口，账号由管理员在后台创建。 */
    suspend fun login(username: String, password: String): LoginResult {
        val body = buildString {
            append("{")
            append("\"username\":").append(quote(username)).append(",")
            append("\"password\":").append(quote(password))
            append("}")
        }
        return try {
            val raw = post("$BASE/api/login", body, null)
            val dto = json.decodeFromString<LoginResult>(raw)
            // 服务端登录失败时返回 {"detail":"..."}，不是标准结构
            if (dto.token.isNullOrEmpty()) {
                val detail = runCatching {
                    json.parseToJsonElement(raw).let { it as? kotlinx.serialization.json.JsonObject }
                        ?.get("detail")?.toString()?.trim('"')
                }.getOrNull()
                dto.copy(success = false, error = detail ?: "账号或密码错误")
            } else {
                dto.copy(success = true)
            }
        } catch (e: Exception) {
            LoginResult(success = false, error = e.message ?: "连接服务器失败")
        }
    }

    /** 拉取用户信息（配额、已用流量、到期时间）。 */
    suspend fun profile(token: String): UserProfile {
        return try {
            val raw = get("$BASE/api/profile", token)
            json.decodeFromString<UserProfile>(raw)
        } catch (e: Exception) {
            UserProfile(success = false, error = e.message ?: "获取用户信息失败")
        }
    }

    /** 取 WireGuard 配置（服务端首次调用时会自动生成密钥并注册 peer）。 */
    suspend fun vpnConfig(token: String): ConfigResult {
        return try {
            val raw = get("$BASE/api/vpn-config", token)
            val dto = json.decodeFromString<ConfigResult>(raw)
            if (dto.config.isNullOrEmpty()) {
                dto.copy(success = false, error = "服务端未返回配置")
            } else {
                dto.copy(success = true)
            }
        } catch (e: Exception) {
            ConfigResult(success = false, error = e.message ?: "获取配置失败")
        }
    }

    // ---------------- HTTP ----------------

    private suspend fun get(url: String, token: String?): String =
        execute(Request.Builder().url(url).get().apply {
            token?.let { header("Authorization", "Bearer $it") }
        }.build())

    private suspend fun post(url: String, body: String, token: String?): String =
        execute(Request.Builder().url(url).post(body.toRequestBody(JSON_MEDIA)).apply {
            token?.let { header("Authorization", "Bearer $it") }
        }.build())

    private suspend fun execute(request: Request): String = kotlinx.coroutines.withContext(
        kotlinx.coroutines.Dispatchers.IO
    ) {
        val response = try {
            client.newCall(request).execute()
        } catch (e: IOException) {
            throw WgApiException("无法连接 WiFi 服务器，请检查网络")
        }
        response.use { resp ->
            val body = resp.body?.string().orEmpty()
            if (!resp.isSuccessful) {
                val detail = runCatching {
                    json.parseToJsonElement(body).let { it as? kotlinx.serialization.json.JsonObject }
                        ?.get("detail")?.toString()?.trim('"')
                }.getOrNull()
                throw WgApiException(detail ?: "请求失败（HTTP ${resp.code}）")
            }
            body.ifBlank { "{}" }
        }
    }

    private fun quote(s: String): String = buildString {
        append('"')
        s.forEach { c ->
            when (c) {
                '"' -> append("\\\"")
                '\\' -> append("\\\\")
                '\n' -> append("\\n")
                '\r' -> append("\\r")
                '\t' -> append("\\t")
                else -> if (c.code < 0x20) append("\\u%04x".format(c.code)) else append(c)
            }
        }
        append('"')
    }
}

class WgApiException(override val message: String) : Exception(message)
