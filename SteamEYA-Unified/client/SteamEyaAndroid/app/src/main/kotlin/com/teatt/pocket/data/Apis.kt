package com.teatt.pocket.data

import com.teatt.pocket.core.Api
import com.teatt.pocket.core.CloudAccountDto
import com.teatt.pocket.core.CloudCheckDto
import com.teatt.pocket.core.CloudGameDto
import com.teatt.pocket.core.CloudGamesDto
import com.teatt.pocket.core.CloudMyDto
import com.teatt.pocket.core.CookieDto
import com.teatt.pocket.core.LoginDto
import com.teatt.pocket.core.MeDto
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject

@Serializable
private data class OkDto(val ok: Boolean = false, val msg: String = "", val nickname: String = "")

@Serializable
private data class AvatarDto(val ok: Boolean = false, val avatar: String = "")

object AuthApi {

    suspend fun login(username: String, password: String): LoginDto {
        val body = buildJsonObject {
            put("username", JsonPrimitive(username))
            put("password", JsonPrimitive(password))
        }.toString()
        return Api.decode(Api.postJson("/api/auth/login", body))
    }

    suspend fun register(username: String, password: String, nickname: String): LoginDto {
        val body = buildJsonObject {
            put("username", JsonPrimitive(username))
            put("password", JsonPrimitive(password))
            put("nickname", JsonPrimitive(nickname))
        }.toString()
        return Api.decode(Api.postJson("/api/auth/register", body))
    }

    suspend fun me(token: String): MeDto = Api.decode(Api.get("/api/auth/me", token))
}

object CloudApi {

    suspend fun my(token: String): List<CloudAccountDto> {
        val dto: CloudMyDto = Api.decode(Api.get("/api/cloud/my", token))
        return dto.games
    }

    suspend fun games(token: String): List<CloudGameDto> {
        val dto: CloudGamesDto = Api.decode(Api.get("/api/cloud/games", token))
        return dto.games
    }

    /** 上传 Cookie。服务端会先跑一次有效性检测，用 game_uid 区分同游戏多账号。 */
    suspend fun upload(token: String, game: String, cookies: List<CookieDto>, comboToken: String = ""): CloudCheckDto {
        val body = buildJsonObject {
            put("game", JsonPrimitive(game))
            put("combo_token", JsonPrimitive(comboToken))
            put("cookies", buildJsonArray {
                cookies.forEach { c ->
                    add(buildJsonObject {
                        put("name", JsonPrimitive(c.name))
                        put("value", JsonPrimitive(c.value))
                        put("domain", JsonPrimitive(c.domain))
                        put("path", JsonPrimitive(c.path))
                    })
                }
            })
        }.toString()
        return Api.decode(Api.postJson("/api/cloud/upload", body, token))
    }

    suspend fun check(token: String, game: String, uid: String = ""): CloudCheckDto {
        val body = buildJsonObject {
            put("game", JsonPrimitive(game))
            if (uid.isNotBlank()) put("uid", JsonPrimitive(uid))
        }.toString()
        return Api.decode(Api.postJson("/api/cloud/check", body, token))
    }

    /** 删除自己的某个云账号；uid 为空表示删该游戏下最新一条。 */
    suspend fun delete(token: String, game: String, uid: String = "") {
        val query = buildMap {
            put("game", game)
            if (uid.isNotBlank()) put("uid", uid)
        }
        Api.delete("/api/cloud/my", query, token)
    }

    suspend fun addGame(token: String, name: String, url: String) {
        val body = buildJsonObject {
            put("name", JsonPrimitive(name))
            put("url", JsonPrimitive(url))
        }.toString()
        Api.postJson("/api/cloud/games", body, token)
    }
}

object ProfileApi {

    suspend fun updateNickname(token: String, nickname: String): String {
        val body = buildJsonObject { put("nickname", JsonPrimitive(nickname)) }.toString()
        val dto: OkDto = Api.decode(Api.postJson("/api/profile", body, token))
        return dto.nickname.ifBlank { nickname }
    }

    suspend fun changePassword(token: String, oldPassword: String, newPassword: String) {
        val body = buildJsonObject {
            put("oldPassword", JsonPrimitive(oldPassword))
            put("newPassword", JsonPrimitive(newPassword))
        }.toString()
        Api.postJson("/api/profile", body, token)
    }

    suspend fun uploadAvatar(token: String, bytes: ByteArray, fileName: String, mime: String): String {
        val dto: AvatarDto = Api.decode(
            Api.postFile("/api/profile/avatar", "file", fileName, mime, bytes, token = token)
        )
        return dto.avatar
    }
}
