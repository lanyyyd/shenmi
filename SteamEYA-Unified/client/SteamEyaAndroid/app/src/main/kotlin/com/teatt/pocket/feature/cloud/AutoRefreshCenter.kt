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
    val credential: CloudCredential,
    /** 测试模式：默认展开页面并显示操作日志，不写失败退避。 */
    val debug: Boolean = false
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

    /** 当前正在执行的静默任务；RootScreen 监听它来画悬浮窗。 */
    var job by mutableStateOf<SilentJob?>(null)
        private set

    /** 静默任务是否展开成完整 WebView（点右侧悬浮窗后展开，可手动接管）。 */
    var expanded by mutableStateOf(false)
        private set

    fun expand() {
        if (job != null) expanded = true
    }

    fun collapse() {
        expanded = false
    }

    /** 测试结束后手动关闭留在屏幕上的 WebView。 */
    fun closeView() {
        job = null
        expanded = false
        notice = null
    }

    /** 执行结果提示（成功 / 失败原因），RootScreen 弹窗展示后调用 [dismissNotice]。 */
    var notice by mutableStateOf<String?>(null)
        private set

    /** 每次自动更新结束后自增，云账号页监听它来刷新列表。 */
    var revision by mutableStateOf(0)
        private set

    fun dismissNotice() {
        notice = null
        // 测试模式结束后页面留着，等用户看完日志再关
        if (job?.debug == true) {
            job = null
            expanded = false
        }
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

    /** 测试模式：展开 WebView，把自动登录的每一步和日志都显示出来，方便排查。 */
    fun testLogin(token: String, account: CloudAccountDto) {
        if (running || token.isBlank()) return
        if (account.game !in setOf("genshin", "starrail")) return
        scope.launch { run(token, force = true, only = listOf(account), debug = true) }
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
        only: List<CloudAccountDto>?,
        debug: Boolean = false
    ) {
        if (running) return
        running = true
        try {
            val accounts = only ?: runCatching { CloudApi.my(token) }.getOrNull().orEmpty()
            // 测试模式不等网络：立刻用兜底地址打开页面，让操作过程马上可见
            val games = if (debug) emptyList()
            else runCatching { CloudApi.games(token) }.getOrNull().orEmpty()
            val credentials = runCatching { CloudCredentialStore.list() }.getOrNull().orEmpty()
            val targets = accounts.filter {
                it.game in setOf("genshin", "starrail") && it.gameUid.isNotBlank()
            }
            val messages = mutableListOf<String>()

            for (account in targets) {
                val credential = credentials.firstOrNull {
                    it.game == account.game && it.gameUid == account.gameUid && (it.enabled || debug)
                }
                if (credential == null) {
                    if (debug) messages += "${account.displayName} 还没有保存账号密码，请先点「续期」填写"
                    continue
                }

                if (!debug) {
                    if (!force && account.valid == 1 && recentlyChecked(account)) continue
                    if (!force && credential.coolingDown()) continue

                    // 列表里的 valid 可能过期，再向服务端确认一次；网络异常直接跳过，不记失败
                    val checkResult = runCatching {
                        CloudApi.check(token, account.game, account.gameUid)
                    }
                    if (checkResult.isFailure) continue
                    if (!force && checkResult.getOrNull()?.valid == true) continue
                }
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
                job = SilentJob(game, credential, debug)
                expanded = debug
                val outcome = deferred.await()
                // 正式静默流程拿到结果就收掉页面；测试模式留在原地，方便看日志
                if (!debug) job = null

                when (outcome) {
                    is SilentOutcome.Success -> {
                        val uploaded = runCatching {
                            CloudApi.upload(token, game.key, outcome.cookies, outcome.comboToken)
                        }
                        val result = uploaded.getOrNull()
                        // msg 在上传接口里固定是「上传成功」，失败原因要用 detail
                        val detail = result?.detail?.takeIf { it.isNotBlank() }
                            ?: result?.msg?.takeIf { it.isNotBlank() && it != "上传成功" }
                            ?: uploaded.exceptionOrNull()?.message
                            ?: "服务端没返回原因"
                        if (result?.valid == true) {
                            if (!debug) CloudCredentialStore.markSuccess(account.game, account.gameUid)
                            messages += if (debug) {
                                "${account.displayName} 测试完成：登录成功，服务端检测通过（$detail）"
                            } else {
                                "${account.displayName} 登录态已自动静默更新"
                            }
                        } else {
                            if (!debug) CloudCredentialStore.markFailure(account.game, account.gameUid, detail)
                            val prefix = if (debug) "测试结束：已抓到登录态，但服务端检测未通过"
                            else "自动更新失败"
                            messages += "${account.displayName} $prefix：$detail"
                        }
                    }
                    is SilentOutcome.Failed -> {
                        if (!debug) CloudCredentialStore.markFailure(account.game, account.gameUid, outcome.message)
                        messages += "${account.displayName} " +
                            (if (debug) "测试失败：${outcome.message}" else "自动更新失败：${outcome.message}")
                    }
                    SilentOutcome.Cancelled -> {
                        job = null
                        expanded = false
                        return
                    }
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
            if (!debug) {
                job = null
                expanded = false
            }
        }
    }
}
