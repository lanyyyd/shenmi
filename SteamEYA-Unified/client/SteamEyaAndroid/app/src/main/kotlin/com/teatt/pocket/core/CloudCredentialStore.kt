package com.teatt.pocket.core

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import kotlinx.coroutines.flow.first
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * 云游戏「静默续期」用的本机账号密码。
 *
 * 只落在手机本地（DataStore），密码用 Android Keystore 的 AES-GCM 密钥加密后存，
 * 不上传服务器。换机 / 清除 App 数据后 Keystore 密钥丢失，旧密文解不出来，
 * 表现为需要重新填写，属于预期行为。
 */
@Serializable
data class CloudCredential(
    val game: String = "",
    @SerialName("game_uid") val gameUid: String = "",
    val account: String = "",
    /** 内存里是明文；写入 DataStore 前会加密。 */
    val password: String = "",
    val enabled: Boolean = false,
    /** 最近一次自动尝试时间（毫秒）；0 表示没试过。 */
    @SerialName("last_attempt") val lastAttempt: Long = 0,
    /** 最近一次成功时间（毫秒）。 */
    @SerialName("last_success") val lastSuccess: Long = 0,
    @SerialName("last_error") val lastError: String = ""
) {
    val key: String get() = "$game:$gameUid"

    /** 失败后 6 小时内不再自动重试，避免反复登录触发风控。 */
    fun coolingDown(now: Long = System.currentTimeMillis()): Boolean =
        lastAttempt > 0 && lastError.isNotBlank() && now - lastAttempt < BACKOFF_MS

    companion object {
        const val BACKOFF_MS = 6 * 3600_000L
    }
}

private val Context.cloudCredentialStore by preferencesDataStore(name = "cloud_credentials")

object CloudCredentialStore {

    private const val KEY_ALIAS = "pocket_cloud_credential"
    private const val PREFIX = "cred:"

    private lateinit var appContext: Context
    private val json = Json {
        ignoreUnknownKeys = true
        encodeDefaults = true
    }

    fun init(context: Context) {
        appContext = context.applicationContext
    }

    fun isReady(): Boolean = this::appContext.isInitialized

    private fun prefKey(game: String, uid: String) = PREFIX + game + ":" + uid

    suspend fun get(game: String, uid: String): CloudCredential? {
        if (!isReady()) return null
        val prefs = appContext.cloudCredentialStore.data.first()
        val raw = prefs[stringPreferencesKey(prefKey(game, uid))] ?: return null
        return decode(raw)
    }

    suspend fun list(): List<CloudCredential> {
        if (!isReady()) return emptyList()
        val prefs = appContext.cloudCredentialStore.data.first()
        return prefs.asMap().entries.mapNotNull { (key, value) ->
            if (!key.name.startsWith(PREFIX) || value !is String) return@mapNotNull null
            decode(value)
        }
    }

    /** 保存成功返回 true；Keystore 加密失败时返回 false，避免把空密码当成有效凭据。 */
    suspend fun save(credential: CloudCredential): Boolean {
        if (!isReady()) return false
        if (credential.game.isBlank() || credential.gameUid.isBlank()) return false
        val encrypted = encrypt(credential.password)
        if (credential.password.isNotBlank() && encrypted.isBlank()) return false
        val stored = credential.copy(password = encrypted)
        appContext.cloudCredentialStore.edit { prefs ->
            prefs[stringPreferencesKey(prefKey(credential.game, credential.gameUid))] =
                json.encodeToString(CloudCredential.serializer(), stored)
        }
        return true
    }

    suspend fun delete(game: String, uid: String) {
        if (!isReady()) return
        appContext.cloudCredentialStore.edit { prefs ->
            prefs.remove(stringPreferencesKey(prefKey(game, uid)))
        }
    }

    suspend fun markSuccess(game: String, uid: String) {
        val current = get(game, uid) ?: return
        val now = System.currentTimeMillis()
        save(current.copy(lastAttempt = now, lastSuccess = now, lastError = ""))
    }

    suspend fun markFailure(game: String, uid: String, message: String) {
        val current = get(game, uid) ?: return
        save(current.copy(lastAttempt = System.currentTimeMillis(), lastError = message))
    }

    private fun decode(raw: String): CloudCredential? = runCatching {
        val stored = json.decodeFromString(CloudCredential.serializer(), raw)
        val plain = decrypt(stored.password)
        // 密钥丢了 / 数据损坏时按「没有凭据」处理，让用户重新填一次，不要拿空密码去登录
        if (stored.password.isNotBlank() && plain.isBlank()) null else stored.copy(password = plain)
    }.getOrNull()

    private fun secretKey(): SecretKey {
        val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (store.getEntry(KEY_ALIAS, null) as? KeyStore.SecretKeyEntry)?.secretKey?.let { return it }
        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        generator.init(
            KeyGenParameterSpec.Builder(
                KEY_ALIAS,
                KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT
            )
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build()
        )
        return generator.generateKey()
    }

    private fun encrypt(plain: String): String {
        if (plain.isBlank()) return ""
        return runCatching {
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(Cipher.ENCRYPT_MODE, secretKey())
            val body = cipher.doFinal(plain.toByteArray(Charsets.UTF_8))
            "v1:" + Base64.encodeToString(cipher.iv, Base64.NO_WRAP) + ":" +
                Base64.encodeToString(body, Base64.NO_WRAP)
        }.getOrElse { "" }
    }

    private fun decrypt(stored: String): String {
        if (!stored.startsWith("v1:")) return ""
        return runCatching {
            val parts = stored.removePrefix("v1:").split(":")
            if (parts.size != 2) return@runCatching ""
            val iv = Base64.decode(parts[0], Base64.NO_WRAP)
            val body = Base64.decode(parts[1], Base64.NO_WRAP)
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(Cipher.DECRYPT_MODE, secretKey(), GCMParameterSpec(128, iv))
            String(cipher.doFinal(body), Charsets.UTF_8)
        }.getOrElse { "" }
    }
}
