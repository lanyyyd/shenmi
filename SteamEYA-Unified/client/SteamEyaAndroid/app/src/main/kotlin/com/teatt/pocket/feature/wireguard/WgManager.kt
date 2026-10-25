package com.teatt.pocket.feature.wireguard

import android.content.Context
import android.util.Log
import com.wireguard.android.backend.Backend
import com.wireguard.android.backend.GoBackend
import com.wireguard.android.backend.Tunnel
import com.wireguard.config.Config
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.ByteArrayInputStream

/**
 * WireGuard 隧道管理器（单例）。
 *
 * 移植自「神秘WiFi」WireguardManager.kt，并做了一处关键调整：
 * 隧道由 [WgService] 这个前台服务持有（而不是 Activity），
 * 所以「我的」页里那个全屏面板关掉之后，连接依然保持。
 *
 * 单例的原因同源项目：Activity 可能因返回键/配置变化被销毁重建，
 * 但 VpnService 和隧道实际还在运行，单例保证状态随时可查。
 */
class WgManager private constructor(private val context: Context) {

    companion object {
        private const val TAG = "WgManager"
        private const val TUNNEL_NAME = "shenmi_wg"

        @Volatile private var instance: WgManager? = null

        fun getInstance(context: Context): WgManager {
            return instance ?: synchronized(this) {
                instance ?: WgManager(context.applicationContext).also { instance = it }
            }
        }

        fun getInstanceOrNull(): WgManager? = instance
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private val futureBackend = CompletableDeferred<Backend>()
    private var tunnel: WgTunnel? = null
    private var config: Config? = null

    /** backend 的直接引用，供非挂起方法（如 statistics）同步读取。 */
    @Volatile private var backend: Backend? = null

    private val _state = MutableStateFlow(Tunnel.State.DOWN)
    /** 隧道状态，供 UI（面板 / 我的页副标题）订阅。 */
    val state: StateFlow<Tunnel.State> = _state.asStateFlow()

    /** 最近一次连接用的配置文本，自动重连时直接复用，不再请求服务端。 */
    @Volatile private var cachedConfig: String? = null

    private val stateListeners = mutableListOf<(Tunnel.State) -> Unit>()

    fun addStateListener(listener: (Tunnel.State) -> Unit) {
        if (!stateListeners.contains(listener)) stateListeners.add(listener)
        listener(_state.value)
    }

    fun removeStateListener(listener: (Tunnel.State) -> Unit) {
        stateListeners.remove(listener)
    }

    private fun notifyStateChanged(state: Tunnel.State) {
        _state.value = state
        stateListeners.forEach { it(state) }
    }

    /** 初始化 GoBackend（幂等，多次调用安全）。 */
    fun initialize() {
        if (futureBackend.isCompleted) {
            Log.i(TAG, "GoBackend 已初始化，跳过")
            return
        }
        scope.launch(Dispatchers.IO) {
            try {
                val created = GoBackend(context)
                // 先建隧道再 complete：否则 await() 返回的瞬间 tunnel 可能还是 null
                tunnel = WgTunnel(TUNNEL_NAME) { state ->
                    notifyStateChanged(state)
                }
                backend = created
                futureBackend.complete(created)
                Log.i(TAG, "GoBackend 初始化成功，隧道实例: $TUNNEL_NAME")
            } catch (e: Throwable) {
                Log.e(TAG, "GoBackend 初始化失败", e)
                futureBackend.completeExceptionally(e)
            }
        }
    }

    /**
     * 建立隧道。
     *
     * @param configText 服务端下发的 WireGuard 配置（INI 文本）
     */
    suspend fun connect(configText: String): Boolean = withContext(Dispatchers.IO) {
        try {
            initialize()
            // await 后 backend 与 tunnel 都已就绪
            val b = futureBackend.await()
            val parsed = Config.parse(ByteArrayInputStream(configText.toByteArray()))
            config = parsed
            cachedConfig = configText

            val t = tunnel ?: run {
                Log.e(TAG, "隧道未初始化")
                return@withContext false
            }

            val state = b.setState(t, Tunnel.State.UP, parsed)
            Log.i(TAG, "隧道状态: $state")
            state == Tunnel.State.UP
        } catch (e: Throwable) {
            Log.e(TAG, "连接失败", e)
            false
        }
    }

    /** 断开隧道（用户主动断开）。 */
    suspend fun disconnect() {
        withContext(Dispatchers.IO) {
            try {
                val t = tunnel
                val b = backend
                if (t != null && b != null) {
                    b.setState(t, Tunnel.State.DOWN, null)
                    Log.i(TAG, "隧道已断开（用户主动）")
                } else {
                    Log.w(TAG, "隧道未初始化或后端未就绪")
                }
            } catch (e: Throwable) {
                Log.e(TAG, "断开失败", e)
            }
        }
    }

    /**
     * 隧道累计流量 (rx, tx)，单位字节。
     *
     * 读的是 WireGuard 后端自己的计数器（只统计这个隧道），
     * 比系统全局的 TrafficStats 准得多 —— 后者会把 App 自己的下载也算进去。
     */
    fun statistics(): Pair<Long, Long>? {
        val t = tunnel ?: return null
        val b = backend ?: return null
        return try {
            val stats = b.getStatistics(t) ?: return null
            // Statistics 里是 Java 的 totalRx()/totalTx() 方法，不是 Kotlin 属性
            stats.totalRx() to stats.totalTx()
        } catch (e: Throwable) {
            null
        }
    }

    fun isConnected(): Boolean = _state.value == Tunnel.State.UP

    /** 自动重连用的配置缓存。 */
    fun cachedConfig(): String? = cachedConfig

    fun clearCachedConfig() {
        cachedConfig = null
    }
}
