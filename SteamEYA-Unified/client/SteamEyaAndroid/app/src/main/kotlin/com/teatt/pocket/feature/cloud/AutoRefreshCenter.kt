package com.teatt.pocket.feature.cloud

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import com.teatt.pocket.core.CloudAccountDto
import com.teatt.pocket.core.CloudCredential
import com.teatt.pocket.core.CloudCredentialStore
import com.teatt.pocket.core.CloudGameDto
import com.teatt.pocket.data.CloudApi
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/** 一次静默续期要加载的页面与凭据。 */
data class SilentJob(
    val game: CloudGameDto,
    val credential: CloudCredential
)

/**
 * 云原神 / 云崩铁登录态静默续期调度。
 *
 * 只在 App 冷启动后跑一次：服务端说账号失效、本机又存了账号密码、且不在 6 小时
 * 失败退避期内，才打开 [SilentLoginView] 后台登录并上传新 Cookie。
 * 登录全程不清 WebView Cookie，避免米哈游重新做设备验证。
 */
object AutoRefreshCenter {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)

    private var started = false
    private var running = false
    private var waiter: CompletableDeferred<SilentOutcome>? = null

    /** 当前正在执行的静默任务；RootScreen 监听它来弹 WebView。 */
    var job by mutableStateOf<SilentJob?>(null)
        private set

    /** 执行结果提示（成功 / 失败原因），RootScreen 弹窗展示后调用 [dismissNotice]。 */
    var notice by mutableStateOf<String?>(null)
        private set

    /** 每次自动更新结束后自增，云账号页监听它来刷新列表。 */
    var revision by mutableStateOf(0)
        private set

    fun dismissNotice() {
        notice = null
    }

    /** 冷启动只触发一次。 */
    fun startIfNeeded(token: String) {
        if (started || token.isBlank()) return
        started = true
        scope.launch { run(token, force = false, only = null) }
    }

    /** 云账号页手动触发（保存凭据后点「立即更新」）。 */
    fun refreshNow(token: String, account: CloudAccountDto) {
        if (running) return
        started = true
        scope.launch { run(token, force = true, only = listOf(account)) }
    }

    /** SilentLoginView 结束后回调。 */
    fun finish(outcome: SilentOutcome) {
        val target = waiter
        waiter = null
        target?.complete(outcome)
    }

    /** 10 分钟内检测过且有效，认为列表数据可信，不必重复请求。 */
    private fun recentlyChecked(account: CloudAccountDto): Boolean {
        val checkedAt = account.checkedAt ?: return false
        val nowSeconds = System.currentTimeMillis() / 1000
        return nowSeconds - checkedAt < 600
    }

    private suspend fun run(
        token: String,
        force: Boolean,
        only: List<CloudAccountDto>?
    ) {
        if (running) return
        running = true
        try {
            val accounts = only ?: runCatching { CloudApi.my(token) }.getOrNull().orEmpty()
            val games = runCatching { CloudApi.games(token) }.getOrNull().orEmpty()
            val credentials = runCatching { CloudCredentialStore.list() }.getOrNull().orEmpty()
            val targets = accounts.filter {
                it.game in setOf("genshin", "starrail") && it.gameUid.isNotBlank()
            }
            val messages = mutableListOf<String>()

            for (account in targets) {
                val credential = credentials.firstOrNull {
                    it.game == account.game && it.gameUid == account.gameUid && it.enabled
                } ?: continue

                if (!force && account.valid == 1 && recentlyChecked(account)) continue
                if (!force && credential.coolingDown()) continue

                // 列表里的 valid 可能过期，再向服务端确认一次；网络异常直接跳过，不记失败
                val checkResult = runCatching {
                    CloudApi.check(token, account.game, account.gameUid)
                }
                if (checkResult.isFailure) continue
                if (!force && checkResult.getOrNull()?.valid == true) continue

                val game = games.firstOrNull { it.key == account.game } ?: CloudGameDto(
                    key = account.game,
                    name = account.displayName,
                    url = if (account.game == "starrail") {
                        "https://sr.mihoyo.com/cloud/#/"
                    } else {
                        "https://ys.mihoyo.com/cloud/#/"
                    }
                )

                val deferred = CompletableDeferred<SilentOutcome>()
                waiter = deferred
                job = SilentJob(game, credential)
                val outcome = deferred.await()
                job = null

                when (outcome) {
                    is SilentOutcome.Success -> {
                        val uploaded = runCatching {
                            CloudApi.upload(token, game.key, outcome.cookies, outcome.comboToken)
                        }
                        val result = uploaded.getOrNull()
                        if (result?.valid == true) {
                            CloudCredentialStore.markSuccess(account.game, account.gameUid)
                            messages += "${account.displayName} 登录态已自动静默更新"
                        } else {
                            val msg = result?.msg?.takeIf { it.isNotBlank() }
                                ?: uploaded.exceptionOrNull()?.message
                                ?: "服务端校验未通过"
                            CloudCredentialStore.markFailure(account.game, account.gameUid, msg)
                            messages += "${account.displayName} 自动更新失败：$msg"
                        }
                    }
                    is SilentOutcome.Failed -> {
                        CloudCredentialStore.markFailure(account.game, account.gameUid, outcome.message)
                        messages += "${account.displayName} 自动更新失败：${outcome.message}"
                    }
                    SilentOutcome.Cancelled -> return
                }
                delay(1000)
            }

            if (messages.isNotEmpty()) {
                notice = messages.joinToString("\n")
                revision++
            }
        } finally {
            running = false
            waiter = null
            job = null
        }
    }
}
