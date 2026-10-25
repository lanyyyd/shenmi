package com.teatt.pocket.data

import com.teatt.pocket.core.Api
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/**
 * 更新源与桌面端共用：`GET /api/update/latest?platform=android`。
 * 管理员在网关的「更新管理 → 手机端」上传 APK 并设为当前版本，客户端从这里取。
 */
@Serializable
data class UpdateInfoDto(
    val ok: Boolean = false,
    val available: Boolean = false,
    val platform: String = "",
    val id: Int = 0,
    val version: String = "",
    val notes: String = "",
    val fileName: String = "",
    val fileSize: Long = 0,
    val sha256: String = "",
    val url: String = "",
    @SerialName("updatedAt") val updatedAt: Long = 0
)

object UpdateApi {

    suspend fun latest(token: String): UpdateInfoDto =
        Api.decode(Api.get("/api/update/latest", token, mapOf("platform" to PLATFORM)))

    /** 服务端返回的是 /api/update/download/12 这种相对路径。 */
    fun downloadUrl(info: UpdateInfoDto): String = Api.absolute(info.url).orEmpty()

    const val PLATFORM = "android"
}
