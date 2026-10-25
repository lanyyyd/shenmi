package com.teatt.pocket.feature.wireguard

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.util.Log
import androidx.core.app.NotificationCompat
import com.teatt.pocket.MainActivity
import com.teatt.pocket.R
import com.wireguard.android.backend.Tunnel
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.cancel
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import java.util.concurrent.atomic.AtomicLong

/**
 * WireGuard 前台服务：★ 隧道的真正持有者。
 *
 * 为什么要做成前台服务而不是挂在 Activity 上：
 * 「我的」页里那个全屏面板关掉之后，连接必须继续保持。
 * 隧道交给 Service 持有，面板只是观察者，关闭面板不碰 Service。
 *
 * 同时承担三件事（合并了原项目里 TrafficMonitorService + KeepAliveService 两个服务）：
 *  1. 持有隧道，让进程不易被系统回收
 *  2. 实时流量统计（前台 2s / 后台 5s 采样）
 *  3. 常驻通知，显示连接状态与实时速度
 */
class WgService : Service() {

    companion object {
        private const val TAG = "WgService"
        private const val CHANNEL_ID = "wg_vpn_channel"
        private const val NOTIFICATION_ID = 3001

        private const val PREFS = "pocket_wg_settings"
        private const val KEY_AUTO_RECONNECT = "auto_reconnect"
        private const val KEY_KEEP_ALIVE = "notification_keep_alive"

        // 采样间隔
        private const val INTERVAL_FOREGROUND = 2000L
        private const val INTERVAL_BACKGROUND = 5000L

        /** 采样间隔（秒），用于把累计字节差换算成每秒速度。 */
        private const val SAMPLE_SECONDS = 2L

        const val ACTION_DISCONNECT = "com.teatt.pocket.wg.DISCONNECT"

        /** 断线重连的指数退避间隔，最后一档封顶 5 分钟。 */
        private val BACKOFF_DELAYS = longArrayOf(
            10_000L, 30_000L, 60_000L, 120_000L, 300_000L
        )

        @Volatile private var downSpeed = 0L
        @Volatile private var upSpeed = 0L
        @Volatile private var totalUsed = 0L

        /**
         * 用户主动断开标记（放在 companion 里，静态入口和实例逻辑共享同一个标记）。
         * 为 true 时隧道 DOWN 不触发自动重连。
         */
        @Volatile private var userDisconnected = false

        /** 标记用户主动断开：之后的隧道 DOWN 不触发自动重连。 */
        fun markUserDisconnect(context: Context) {
            userDisconnected = true
        }

        /** 清除主动断开标记（用户重新点连接时用）。 */
        fun clearUserDisconnect(context: Context) {
            userDisconnected = false
        }

        /** 当前下载速度（字节/秒），供面板订阅。 */
        fun downSpeedValue(): Long = downSpeed

        /** 当前上传速度（字节/秒）。 */
        fun upSpeedValue(): Long = upSpeed

        /** 启动前台服务（连接成功后调用，隧道由它持有）。 */
        fun start(context: Context) {
            val intent = Intent(context, WgService::class.java)
            runCatching {
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                    context.startForegroundService(intent)
                } else {
                    context.startService(intent)
                }
            }.onFailure { Log.e(TAG, "启动 WgService 失败", it) }
        }

        /** 停止前台服务（用户主动断开后调用，通知随之消失）。 */
        fun stop(context: Context) {
            runCatching {
                context.stopService(Intent(context, WgService::class.java))
            }
        }

        /** 断开并清理本地状态（用户主动断开时调用）。 */
        fun disconnectAndClean(context: Context) {
            markUserDisconnect(context)
            WgConfigCache.clear(context)
        }

        /** 自动重连开关（默认开）。 */
        fun isAutoReconnectEnabled(context: Context): Boolean =
            context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                .getBoolean(KEY_AUTO_RECONNECT, true)

        fun setAutoReconnectEnabled(context: Context, enabled: Boolean) {
            context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                .edit().putBoolean(KEY_AUTO_RECONNECT, enabled).apply()
        }

        /** 通知保活开关（默认开）。关闭后仍会统计流量，只是不常驻通知。 */
        fun isKeepAliveEnabled(context: Context): Boolean =
            context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                .getBoolean(KEY_KEEP_ALIVE, true)

        fun setKeepAliveEnabled(context: Context, enabled: Boolean) {
            context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                .edit().putBoolean(KEY_KEEP_ALIVE, enabled).apply()
        }

        fun formatSpeed(bytesPerSecond: Long): String = when {
            bytesPerSecond < 1024 -> "$bytesPerSecond B/s"
            bytesPerSecond < 1024 * 1024 -> String.format("%.1f KB/s", bytesPerSecond / 1024.0)
            bytesPerSecond < 1024L * 1024 * 1024 ->
                String.format("%.1f MB/s", bytesPerSecond / 1024.0 / 1024.0)
            else -> String.format("%.2f GB/s", bytesPerSecond / 1024.0 / 1024.0 / 1024.0)
        }

        fun formatBytes(bytes: Long): String = when {
            bytes < 1024 -> "$bytes B"
            bytes < 1024 * 1024 -> String.format("%.1f KB", bytes / 1024.0)
            bytes < 1024L * 1024 * 1024 -> String.format("%.1f MB", bytes / 1024.0 / 1024.0)
            else -> String.format("%.2f GB", bytes / 1024.0 / 1024.0 / 1024.0)
        }
    }

    private val handler = Handler(Looper.getMainLooper())
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var reconnectJob: Job? = null
    private var retryCount = 0

    // 隧道流量的上一次累计值（读 WireGuard 后端的真实计数，差值即速度）
    private val lastRx = AtomicLong(0L)
    private val lastTx = AtomicLong(0L)

    private val tunnelListener: (Tunnel.State) -> Unit = { state -> onTunnelState(state) }

    override fun onCreate() {
        super.onCreate()
        createNotificationChannel()
        WgManager.getInstance(this).initialize()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_DISCONNECT -> {
                cancelReconnect()
                scope.launch {
                    markUserDisconnect(this@WgService)
                    WgManager.getInstance(this@WgService).disconnect()
                    WgConfigCache.clear(this@WgService)
                }
            }
            else -> {
                WgManager.getInstance(this).addStateListener(tunnelListener)
                // 初始通知用隧道真实状态，不要一上来就显示"已连接"
                startForegroundCompat(buildNotification(WgManager.getInstance(this).state.value))
                handler.post(samplingTick)
                // 服务被系统重启（进程被杀）后，如果之前连着且有配置，直接恢复连接
                restoreIfNeeded()
            }
        }
        // START_STICKY：进程被杀后系统会重新拉起这个服务，自动重连逻辑随之恢复
        return START_STICKY
    }

    override fun onDestroy() {
        handler.removeCallbacks(samplingTick)
        WgManager.getInstanceOrNull()?.removeStateListener(tunnelListener)
        reconnectJob?.cancel()
        scope.cancel()
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    // ---------------- 流量采样 ----------------

    private val samplingTick = object : Runnable {
        override fun run() {
            sampleTraffic()
            handler.postDelayed(this, if (isForeground) INTERVAL_FOREGROUND else INTERVAL_BACKGROUND)
        }
    }

    private var isForeground = true

    private fun sampleTraffic() {
        val stats = WgManager.getInstance(this).statistics() ?: return
        val rx = stats.first
        val tx = stats.second
        val drx = rx - lastRx.get()
        val dtx = tx - lastTx.get()
        lastRx.set(rx)
        lastTx.set(tx)
        if (drx > 0) downSpeed = drx / SAMPLE_SECONDS
        if (dtx > 0) upSpeed = dtx / SAMPLE_SECONDS
        totalUsed = rx

        updateNotification()
    }

    // ---------------- 自动重连 ----------------

    private fun onTunnelState(state: Tunnel.State) {
        when (state) {
            Tunnel.State.UP -> {
                retryCount = 0
                cancelReconnect()
                userDisconnected = false
                Log.i(TAG, "隧道已连接，重置重连计数")
            }
            Tunnel.State.DOWN -> {
                if (userDisconnected) {
                    Log.i(TAG, "用户主动断开，不触发自动重连")
                    return
                }
                Log.i(TAG, "隧道意外断开，准备重连")
                scheduleReconnect()
            }
            else -> Unit
        }
        updateNotification()
    }

    private fun scheduleReconnect() {
        cancelReconnect()
        val delayMs = BACKOFF_DELAYS[retryCount.coerceAtMost(BACKOFF_DELAYS.lastIndex)]
        Log.i(TAG, "${delayMs / 1000} 秒后尝试第 ${retryCount + 1} 次重连")
        reconnectJob = scope.launch {
            delay(delayMs)
            doReconnect()
        }
    }

    private suspend fun doReconnect() {
        val wg = WgManager.getInstanceOrNull() ?: return
        // 内存缓存优先（最新），没有就用持久化的（进程被杀重启后仍可用）
        val config = wg.cachedConfig() ?: WgConfigCache.load(this)
        if (config.isNullOrEmpty()) {
            Log.w(TAG, "没有缓存的配置，无法重连")
            return
        }
        if (!isAutoReconnectEnabled(this)) return

        Log.i(TAG, "正在尝试第 ${retryCount + 1} 次重连…")
        val ok = wg.connect(config)
        if (ok) {
            retryCount = 0
        } else {
            retryCount++
            scheduleReconnect()
        }
    }

    private fun cancelReconnect() {
        reconnectJob?.cancel()
        reconnectJob = null
    }

    /**
     * 服务重启后恢复连接。
     *
     * 场景：App 进程被系统回收 → START_STICKY 把服务拉回来 → 隧道已经没了，
     * 但配置还在本地（WgConfigCache），直接重连即可。
     */
    private fun restoreIfNeeded() {
        scope.launch {
            val wg = WgManager.getInstance(this@WgService)
            if (wg.isConnected()) return@launch
            val config = WgConfigCache.load(this@WgService) ?: return@launch
            if (!isAutoReconnectEnabled(this@WgService)) return@launch
            Log.i(TAG, "服务重启，尝试恢复连接…")
            val ok = wg.connect(config)
            if (ok) Log.i(TAG, "连接已恢复")
        }
    }

    // ---------------- 通知 ----------------

    private fun createNotificationChannel() {
        val mgr = getSystemService(NotificationManager::class.java) ?: return
        if (mgr.getNotificationChannel(CHANNEL_ID) != null) return
        val channel = NotificationChannel(
            CHANNEL_ID,
            "WiFi 连接",
            NotificationManager.IMPORTANCE_LOW
        ).apply {
            description = "WireGuard 隧道状态与实时网速"
            setShowBadge(false)
        }
        mgr.createNotificationChannel(channel)
    }

    private fun buildNotification(state: Tunnel.State): Notification {
        val connected = state == Tunnel.State.UP
        val text = if (connected) {
            "已连接 · ↓${formatSpeed(downSpeed)} ↑${formatSpeed(upSpeed)}"
        } else {
            "未连接"
        }
        val open = PendingIntent.getActivity(
            this, 0,
            Intent(this, MainActivity::class.java).apply {
                flags = Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP
            },
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )
        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle("神秘WiFi")
            .setContentText(text)
            .setSmallIcon(R.drawable.wg_notification)
            .setOngoing(connected)
            .setOnlyAlertOnce(true)
            .setSilent(true)
            .setContentIntent(open)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .build()
    }

    private fun updateNotification() {
        val mgr = getSystemService(NotificationManager::class.java) ?: return
        if (!isKeepAliveEnabled(this)) return
        runCatching {
            mgr.notify(NOTIFICATION_ID, buildNotification(WgManager.getInstance(this).state.value))
        }
    }

    private fun startForegroundCompat(notification: Notification) {
        runCatching {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
                startForeground(
                    NOTIFICATION_ID, notification,
                    ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE
                )
            } else {
                startForeground(NOTIFICATION_ID, notification)
            }
        }.onFailure { Log.e(TAG, "startForeground 失败", it) }
    }
}
