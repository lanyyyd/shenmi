package com.teatt.pocket.core

import kotlinx.serialization.SerialName
import kotlinx.serialization.decodeFromString
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.MultipartBody
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import java.io.IOException
import java.util.concurrent.TimeUnit

/** 服务端返回的业务错误（HTTP 非 2xx 或 ok=false）。 */
class ApiException(val code: Int, override val message: String) : Exception(message)

/**
 * 统一网关。生产走 chat.teatt.top（HTTPS + HTTP/2 + WebSocket 都已验证可用），
 * 离线本机网关是另一条链，安卓暂不支持。
 */
object Api {

    const val BASE = "https://chat.teatt.top"

    private val json = Json {
        ignoreUnknownKeys = true
        isLenient = true
        coerceInputValues = true
    }

    private val client = OkHttpClient.Builder()
        .connectTimeout(12, TimeUnit.SECONDS)
        .readTimeout(30, TimeUnit.SECONDS)
        .writeTimeout(30, TimeUnit.SECONDS)
        .retryOnConnectionFailure(true)
        .build()

    val http: OkHttpClient get() = client
    val format: Json get() = json

    fun url(path: String): String = if (path.startsWith("http")) path else BASE + path

    /** 把服务端返回的相对路径（/uploads/xxx）拼成可直接加载的绝对地址。 */
    fun absolute(path: String?): String? {
        if (path.isNullOrBlank()) return null
        return if (path.startsWith("http")) path else BASE + path
    }

    suspend fun get(path: String, token: String? = null, query: Map<String, String> = emptyMap()): String {
        val builder = Request.Builder().url(buildUrl(path, query)).get()
        token?.let { builder.header("Authorization", "Bearer $it") }
        return execute(builder.build())
    }

    suspend fun delete(path: String, query: Map<String, String> = emptyMap(), token: String? = null): String {
        val builder = Request.Builder().url(buildUrl(path, query)).delete()
        token?.let { builder.header("Authorization", "Bearer $it") }
        return execute(builder.build())
    }

    suspend fun postJson(path: String, body: String, token: String? = null): String {
        val builder = Request.Builder()
            .url(buildUrl(path, emptyMap()))
            .post(body.toRequestBody(JSON))
        token?.let { builder.header("Authorization", "Bearer $it") }
        return execute(builder.build())
    }

    suspend fun putJson(path: String, body: String, token: String? = null): String {
        val builder = Request.Builder()
            .url(buildUrl(path, emptyMap()))
            .put(body.toRequestBody(JSON))
        token?.let { builder.header("Authorization", "Bearer $it") }
        return execute(builder.build())
    }

    suspend fun postForm(path: String, parts: Map<String, String>, token: String? = null): String {
        val form = MultipartBody.Builder().setType(MultipartBody.FORM)
        parts.forEach { (k, v) -> form.addFormDataPart(k, v) }
        val builder = Request.Builder().url(buildUrl(path, emptyMap())).post(form.build())
        token?.let { builder.header("Authorization", "Bearer $it") }
        return execute(builder.build())
    }

    suspend fun postFile(
        path: String,
        field: String,
        fileName: String,
        mediaType: String,
        bytes: ByteArray,
        extra: Map<String, String> = emptyMap(),
        token: String? = null
    ): String {
        val form = MultipartBody.Builder().setType(MultipartBody.FORM)
        extra.forEach { (k, v) -> form.addFormDataPart(k, v) }
        form.addFormDataPart(field, fileName, bytes.toRequestBody(mediaType.toMediaType()))
        val builder = Request.Builder().url(buildUrl(path, emptyMap())).post(form.build())
        token?.let { builder.header("Authorization", "Bearer $it") }
        return execute(builder.build())
    }

    private fun buildUrl(path: String, query: Map<String, String>): String {
        if (query.isEmpty()) return url(path)
        val qs = query.entries.joinToString("&") { (k, v) -> "$k=${java.net.URLEncoder.encode(v, "UTF-8")}" }
        return url(path) + (if (path.contains('?')) "&" else "?") + qs
    }

    private suspend fun execute(request: Request): String = kotlinx.coroutines.withContext(
        kotlinx.coroutines.Dispatchers.IO
    ) {
        val response = try {
            client.newCall(request).execute()
        } catch (e: IOException) {
            throw ApiException(-1, "网络连接失败，请检查网络后重试")
        }

        response.use { resp ->
            val body = resp.body?.string().orEmpty()
            if (!resp.isSuccessful) {
                throw ApiException(resp.code, errorMessage(body) ?: "请求失败（HTTP ${resp.code}）")
            }
            if (body.isBlank()) return@use "{}"

            // 业务层失败：{"ok":false,"error":"..."}。注意不能用 runCatching——
            // 它会把下面主动抛的 ApiException 一起吞掉，导致上层拿不到错误原因。
            val obj = try {
                json.parseToJsonElement(body).jsonObject
            } catch (_: Exception) {
                null
            }
            if (obj != null && obj["ok"]?.jsonPrimitive?.content == "false") {
                throw ApiException(200, obj["error"]?.jsonPrimitive?.content ?: "操作失败")
            }

            body
        }
    }

    /** 兼容两种错误体：FastAPI 的 {"detail":"..."} 与聊天模块的 {"ok":false,"error":"..."}。 */
    private fun errorMessage(body: String): String? = runCatching {
        val obj = json.parseToJsonElement(body).jsonObject
        obj["detail"]?.jsonPrimitive?.content
            ?: obj["error"]?.jsonPrimitive?.content
            ?: obj["msg"]?.jsonPrimitive?.content
    }.getOrNull()

    /**
     * public inline 函数不能碰 private 成员，这里走公开的 format。
     */
    inline fun <reified T> decode(body: String): T = format.decodeFromString<T>(body)

    private val JSON = "application/json; charset=utf-8".toMediaType()
}

// ---------------- 传输模型 ----------------

@Serializable
data class UserDto(
    val id: Int = 0,
    val username: String = "",
    val nickname: String = "",
    val avatar: String = "",
    val email: String = "",
    val level: Int = 0,
    val exp: Long = 0,
    @SerialName("isAdmin") val isAdmin: Boolean = false
)

@Serializable
data class LoginDto(
    @SerialName("access_token") val accessToken: String = "",
    @SerialName("token_type") val tokenType: String = "bearer",
    val user: UserDto = UserDto()
)

@Serializable
data class MeDto(val user: UserDto? = null)

@Serializable
data class CloudAccountDto(
    val game: String = "",
    @SerialName("game_name") val gameName: String = "",
    val nickname: String = "",
    @SerialName("game_uid") val gameUid: String = "",
    val valid: Int? = null,
    @SerialName("checked_at") val checkedAt: Long? = null,
    @SerialName("updated_at") val updatedAt: Long? = null,
    @SerialName("free_duration") val freeDuration: Int? = null,
    @SerialName("total_duration") val totalDuration: Int? = null,
    /** 最近一次自动续期时间（Unix 秒）；0 表示还没续过。 */
    @SerialName("refresh_at") val refreshAt: Long = 0,
    /** 1 成功、0 失败、null 未续期。 */
    @SerialName("refresh_ok") val refreshOk: Int? = null,
    @SerialName("refresh_msg") val refreshMsg: String = "",
    /** 是否已补长期 stoken（能自动续期）；米哈游游戏才有意义。 */
    @SerialName("has_stoken") val hasStoken: Boolean = false
) {
    val displayName: String get() = gameName.ifBlank { game }

    /** 米哈游账号缺 stoken 时提示去扫码，会话型游戏不提示。 */
    val stokenHint: String get() = if (!hasStoken && game in setOf("genshin", "starrail")) {
        "stoken 未补，点「扫码」后 Cookie 可自动续期"
    } else ""
    val statusText: String get() = when (valid) {
        1 -> "有效"
        0 -> "已失效"
        else -> "未检测"
    }

    /** 服务端用 stoken 换票后的续期状态，直接在卡片上展示。 */
    val renewalText: String get() {
        if (refreshAt <= 0) return ""
        val whenText = java.time.Instant.ofEpochSecond(refreshAt)
            .atZone(java.time.ZoneId.systemDefault())
            .format(java.time.format.DateTimeFormatter.ofPattern("MM-dd HH:mm"))
        return if (refreshMsg.isBlank()) "自动续期 $whenText" else "自动续期 $whenText（$refreshMsg）"
    }
}

@Serializable
data class CloudMyDto(val ok: Boolean = false, val games: List<CloudAccountDto> = emptyList())

@Serializable
data class CloudGameDto(
    val key: String = "",
    val name: String = "",
    val url: String = "",
    val builtin: Boolean = false,
    val visible: Boolean = false,
    val mine: Boolean = false
)

@Serializable
data class CloudGamesDto(val ok: Boolean = false, val games: List<CloudGameDto> = emptyList())

/** /api/cloud/download：注入 WebView 用的新鲜 Cookie（服务端已按需换票续期）。 */
@Serializable
data class CloudDownloadDto(
    val ok: Boolean = false,
    val cookies: List<CookieDto> = emptyList(),
    @SerialName("combo_token") val comboToken: String = "",
    @SerialName("game_uid") val gameUid: String = "",
    @SerialName("updated_at") val updatedAt: Long? = null,
    @SerialName("refresh_at") val refreshAt: Long? = null,
    @SerialName("refresh_ok") val refreshOk: Boolean? = null,
    @SerialName("refresh_msg") val refreshMsg: String = "",
    @SerialName("need_stoken") val needStoken: Boolean = false
)

@Serializable
data class CloudCheckDto(
    val ok: Boolean = false,
    val valid: Boolean? = null,
    val msg: String = "",
    val nickname: String = "",
    @SerialName("game_uid") val gameUid: String = "",
    @SerialName("free_duration") val freeDuration: Int? = null,
    /** 米哈游游戏上传后服务端立刻换票的结果；null 表示该游戏不支持自动续期。 */
    @SerialName("refresh_ok") val refreshOk: Boolean? = null,
    @SerialName("refresh_msg") val refreshMsg: String = "",
    /** Cookie 缺少 stoken，需要先扫码补。 */
    @SerialName("need_stoken") val needStoken: Boolean = false
)

@Serializable
data class StokenCreateDto(
    val ok: Boolean = false,
    val session: String = "",
    val url: String = "",
    val size: Int = 0,
    /** 二维码 0/1 矩阵，每行一个字符串。 */
    val rows: List<String> = emptyList(),
    val msg: String = ""
)

@Serializable
data class StokenPollDto(
    val ok: Boolean = false,
    /** waiting / scanned / confirmed / expired / canceled / error */
    val state: String = "",
    val msg: String = "",
    val mid: String = "",
    val stuid: String = "",
    val matched: Int = 0
)

@Serializable
data class GachaRoleDto(
    @SerialName("game_biz") val gameBiz: String = "",
    val game: String = "",
    @SerialName("game_name") val gameName: String = "",
    @SerialName("game_uid") val gameUid: String = "",
    val region: String = "",
    @SerialName("region_name") val regionName: String = "",
    val nickname: String = "",
    val level: Int? = null,
    @SerialName("is_official") val isOfficial: Boolean = true,
    val mid: String = ""
)

@Serializable
data class GachaRolesDto(
    val ok: Boolean = false,
    val roles: List<GachaRoleDto> = emptyList(),
    val errors: List<String> = emptyList(),
    val detail: String = ""
)

@Serializable
data class GachaLinkDto(
    val ok: Boolean = false,
    val game: String = "",
    @SerialName("game_name") val gameName: String = "",
    @SerialName("game_uid") val gameUid: String = "",
    val region: String = "",
    val url: String = "",
    @SerialName("api_url") val apiUrl: String = "",
    val authkey: String = "",
    @SerialName("expires_in") val expiresIn: Int = 0,
    val detail: String = ""
)

/** 上传用的 Cookie 项；服务端只用 name/value，domain 放宽即可。 */
@Serializable
data class CookieDto(
    val name: String,
    val value: String,
    val domain: String = "",
    val path: String = "/"
)
