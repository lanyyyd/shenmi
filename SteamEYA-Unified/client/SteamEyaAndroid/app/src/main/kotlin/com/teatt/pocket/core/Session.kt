package com.teatt.pocket.core

import android.content.Context
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking

/** 登录态快照。token 用于 REST（Bearer）与 WS（?jwt=）。 */
data class SessionState(
    val token: String = "",
    val userId: Int = 0,
    val username: String = "",
    val nickname: String = "",
    val avatar: String = "",
    val level: Int = 0
) {
    val loggedIn: Boolean get() = token.isNotBlank()

    /** 头像可能是相对路径（/uploads/avatars/x.jpg）。 */
    val avatarUrl: String? get() = Api.absolute(avatar)
}

private val Context.dataStore by preferencesDataStore(name = "pocket_session")

private object Keys {
    val TOKEN = stringPreferencesKey("token")
    val USER_ID = stringPreferencesKey("userId")
    val USERNAME = stringPreferencesKey("username")
    val NICKNAME = stringPreferencesKey("nickname")
    val AVATAR = stringPreferencesKey("avatar")
    val LEVEL = stringPreferencesKey("level")
}

object Session {

    private lateinit var appContext: Context

    private val _state = MutableStateFlow(SessionState())
    val state: StateFlow<SessionState> = _state

    fun init(context: Context) {
        appContext = context.applicationContext
        // 启动时同步读一次：首帧就要判断该显示登录页还是主界面。
        val snapshot = runBlocking {
            appContext.dataStore.data.first()
        }
        _state.value = SessionState(
            token = snapshot[Keys.TOKEN].orEmpty(),
            userId = snapshot[Keys.USER_ID]?.toIntOrNull() ?: 0,
            username = snapshot[Keys.USERNAME].orEmpty(),
            nickname = snapshot[Keys.NICKNAME].orEmpty(),
            avatar = snapshot[Keys.AVATAR].orEmpty(),
            level = snapshot[Keys.LEVEL]?.toIntOrNull() ?: 0
        )
    }

    /** 同步取 token：OkHttp 拦截器与 WebView URL 拼接都需要即时值。 */
    fun token(): String = _state.value.token

    suspend fun save(dto: LoginDto) {
        val user = dto.user
        _state.value = SessionState(
            token = dto.accessToken,
            userId = user.id,
            username = user.username,
            nickname = user.nickname.ifBlank { user.username },
            avatar = user.avatar,
            level = user.level
        )
        appContext.dataStore.edit { prefs ->
            prefs[Keys.TOKEN] = dto.accessToken
            prefs[Keys.USER_ID] = user.id.toString()
            prefs[Keys.USERNAME] = user.username
            prefs[Keys.NICKNAME] = user.nickname
            prefs[Keys.AVATAR] = user.avatar
            prefs[Keys.LEVEL] = user.level.toString()
        }
    }

    /** 改完昵称/头像后局部刷新，不用重新登录。 */
    suspend fun updateUser(user: UserDto) {
        val current = _state.value
        _state.value = current.copy(
            nickname = user.nickname.ifBlank { user.username },
            avatar = user.avatar,
            level = user.level
        )
        appContext.dataStore.edit { prefs ->
            prefs[Keys.NICKNAME] = user.nickname
            prefs[Keys.AVATAR] = user.avatar
            prefs[Keys.LEVEL] = user.level.toString()
        }
    }

    suspend fun updateNickname(nickname: String) {
        _state.value = _state.value.copy(nickname = nickname)
        appContext.dataStore.edit { it[Keys.NICKNAME] = nickname }
    }

    suspend fun updateAvatar(avatar: String) {
        _state.value = _state.value.copy(avatar = avatar)
        appContext.dataStore.edit { it[Keys.AVATAR] = avatar }
    }

    suspend fun clear() {
        _state.value = SessionState()
        appContext.dataStore.edit { it.clear() }
    }

    /**
     * WebView 复用登录态：/api/autologin 用 JWT 换聊天会话 cookie 再 302 到目标页。
     * 服务端这个接口本来就是给客户端内嵌浏览器准备的（注释里写的是 WebView2）。
     */
    fun webUrl(path: String = "/"): String {
        val token = token()
        val redirect = java.net.URLEncoder.encode(path, "UTF-8")
        val jwt = java.net.URLEncoder.encode(token, "UTF-8")
        return "${Api.BASE}/api/autologin?jwt=$jwt&redirect=$redirect"
    }
}
