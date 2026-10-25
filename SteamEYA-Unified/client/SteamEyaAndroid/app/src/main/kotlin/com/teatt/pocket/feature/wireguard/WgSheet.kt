package com.teatt.pocket.feature.wireguard

import android.Manifest
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.provider.Settings
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import com.teatt.pocket.ui.BrandPink
import com.wireguard.android.backend.Tunnel
import kotlinx.coroutines.launch

/**
 * 「神秘WiFi」连接面板（全屏 Dialog 的内容）。
 *
 * 面板只是**观察者**：隧道由 [WgService] 持有，
 * 所以关掉这个面板（返回键 / 左上角箭头）不会断开连接。
 */
@Composable
fun WgSheet(
    onBack: () -> Unit,
    modifier: Modifier = Modifier
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()

    // 记住 manager 实例，别让 context 变化导致订阅重建
    val wgManager = remember { WgManager.getInstance(context) }
    val tunnelState by wgManager.state.collectAsState()

    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    var statusText by remember { mutableStateOf("") }
    var menuOpen by remember { mutableStateOf(false) }
    var showConfig by remember { mutableStateOf(false) }
    var confirmLogout by remember { mutableStateOf(false) }

    var autoReconnect by remember {
        mutableStateOf(WgService.isAutoReconnectEnabled(context))
    }
    var keepAlive by remember {
        mutableStateOf(WgService.isKeepAliveEnabled(context))
    }
    // 通知权限被拒后的引导弹窗
    var notifHint by remember { mutableStateOf(false) }

    /** 去系统通知设置里打开本 App 的通知权限。 */
    fun openNotificationSettings() {
        runCatching {
            context.startActivity(
                Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS)
                    .putExtra(Settings.EXTRA_APP_PACKAGE, context.packageName)
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            )
        }
    }

    /** 保存开关并让运行中的服务立即生效。 */
    fun persistKeepAlive(enabled: Boolean) {
        keepAlive = enabled
        WgService.setKeepAliveEnabled(context, enabled)
        WgService.refreshKeepAlive()
    }

    // Android 13+ 通知是运行时权限：首次勾选「通知保活」时向系统申请
    val notifPermissionLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { granted ->
        if (granted) {
            persistKeepAlive(true)
        } else {
            persistKeepAlive(false)
            notifHint = true
        }
    }

    var profile by remember { mutableStateOf<WgApi.UserProfile?>(null) }
    var configText by remember { mutableStateOf("") }

    // VPN 授权：先 prepare，用户同意后系统返回 RESULT_OK，再真正建隧道。
    // 顺序不能反——先连再授权会连不上（源项目也是这个顺序）。
    var pendingConfig by remember { mutableStateOf<String?>(null) }
    val vpnAuthLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.StartActivityForResult()
    ) { result ->
        val cfg = pendingConfig
        pendingConfig = null
        if (result.resultCode == android.app.Activity.RESULT_OK && cfg != null) {
            busy = true
            error = null
            scope.launch {
                val ok = wgManager.connect(cfg)
                if (ok) {
                    statusText = "已连接"
                    WgService.start(context)
                } else {
                    error = "连接失败，请稍后重试"
                }
                busy = false
            }
        } else {
            error = "未授权 VPN，无法建立连接"
        }
    }

    val connected = tunnelState == Tunnel.State.UP

    /**
     * 加载配置：**缓存优先 + 后台刷新**（对齐原「神秘WiFi」项目的 loadConfigWithCache）。
     *
     * 首次联网时服务端下发配置 → 存到本地（WgConfigCache）。
     * 之后无论有没有网，打开面板先用本地缓存，连接直接用它；
     * 同时后台悄悄向服务端拉一次最新配置，拿到就更新缓存（失败不影响连接）。
     *
     * 所以：第一次之后，**完全断网也能打开小工具并连上**。
     */
    fun loadConfigCacheFirst() {
        // 1) 先用缓存（同步返回，UI 立刻可用）
        val cached = WgConfigCache.load(context)
        if (!cached.isNullOrEmpty()) {
            configText = cached
            statusText = "就绪"
            // 离线时也把配额显示出来（用上次成功的快照）
            val (used, limit) = WgConfigCache.loadTraffic(context)
            if (limit > 0) {
                profile = WgApi.UserProfile(
                    trafficUsed = used,
                    trafficLimit = limit
                )
            }
        } else {
            statusText = "正在获取配置…"
        }

        // 2) 后台刷新（拿不到就算了，缓存还在）
        scope.launch {
            val token = WgSession.token()
            if (token.isBlank()) return@launch
            try {
                // 配额信息
                val p = WgApi.profile(token)
                if (p.success) {
                    profile = p
                    WgConfigCache.saveTraffic(context, p.trafficUsed, p.trafficLimit)
                }
                // 最新配置
                val cfg = WgApi.vpnConfig(token)
                if (cfg.success && !cfg.config.isNullOrEmpty()) {
                    configText = cfg.config
                    WgConfigCache.save(context, cfg.config)
                    if (statusText == "正在获取配置…") statusText = "就绪"
                } else if (cached.isNullOrEmpty()) {
                    // 首次使用且没网 —— 明确告诉用户
                    statusText = "无法获取配置"
                    error = cfg.error ?: "请确保网络可用后重试"
                }
            } catch (e: Exception) {
                // 无网络等异常：缓存存在就静默忽略，不打扰用户
                if (cached.isNullOrEmpty()) {
                    statusText = "无法获取配置"
                    error = "网络不可用，且本地没有缓存配置"
                }
            }
        }
    }

    LaunchedEffect(Unit) {
        loadConfigCacheFirst()
    }

    fun connect() {
        if (busy) return
        busy = true
        error = null
        statusText = "正在连接…"
        // 用户重新点连接 → 清除「主动断开」标记，允许后续自动重连
        WgService.clearUserDisconnect(context)
        scope.launch {
            try {
                // ★ 优先用本地配置（离线也能连）。
                // 只有本地完全没有配置时，才需要联网去取。
                var cfg = configText.ifBlank { null } ?: WgConfigCache.load(context)

                if (cfg.isNullOrEmpty()) {
                    val token = WgSession.token()
                    if (token.isBlank()) {
                        error = "请先登录"
                        busy = false
                        return@launch
                    }
                    val fetched = WgApi.vpnConfig(token)
                    if (!fetched.success || fetched.config.isNullOrEmpty()) {
                        error = fetched.error ?: "获取配置失败"
                        busy = false
                        return@launch
                    }
                    cfg = fetched.config
                    configText = fetched.config
                    WgConfigCache.save(context, fetched.config)
                }

                // VPN 授权：必须先 prepare，用户同意后再连
                val prepare = com.wireguard.android.backend.GoBackend.VpnService.prepare(context)
                if (prepare != null) {
                    pendingConfig = cfg
                    busy = false
                    vpnAuthLauncher.launch(prepare)
                    return@launch
                }
                val ok = WgManager.getInstance(context).connect(cfg)
                if (ok) {
                    statusText = "已连接"
                    WgService.start(context)
                } else {
                    error = "连接失败，请稍后重试"
                }
            } catch (e: Exception) {
                error = e.message ?: "连接失败"
            } finally {
                busy = false
            }
        }
    }

    fun disconnect() {
        busy = true
        scope.launch {
            WgService.markUserDisconnect(context)
            wgManager.disconnect()
            // 注意：断开连接不清配置缓存 ——
            // 配置留在本地，下次打开（哪怕没网）还能直接连。
            WgService.stop(context)
            busy = false
            statusText = "已断开"
        }
    }

    Surface(
        modifier = modifier
            .fillMaxSize()
            .background(MaterialTheme.colorScheme.background),
        color = MaterialTheme.colorScheme.background
    ) {
        Column(
            Modifier
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .statusBarsPadding()
                .navigationBarsPadding()
                .padding(horizontal = 22.dp)
        ) {
            Spacer(Modifier.height(8.dp))

            // 顶栏：← 返回（只关面板，保持连接） + 标题 + 菜单
            Row(
                Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically
            ) {
                IconButton(onClick = onBack) {
                    Icon(Icons.AutoMirrored.Filled.ArrowBack, "返回")
                }
                Text(
                    "神秘WiFi",
                    fontSize = 17.sp,
                    fontWeight = FontWeight.SemiBold,
                    modifier = Modifier.weight(1f)
                )
                Box {
                    IconButton(onClick = { menuOpen = true }) {
                        Icon(Icons.Filled.MoreVert, "更多")
                    }
                    DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
                        DropdownMenuItem(
                            text = { Text("查看配置") },
                            onClick = {
                                menuOpen = false
                                showConfig = true
                            }
                        )
                        DropdownMenuItem(
                            text = { Text("复制配置") },
                            onClick = {
                                menuOpen = false
                                copyConfig(context, configText)
                            }
                        )
                        DropdownMenuItem(
                            text = { Text("退出 WiFi 账号") },
                            onClick = {
                                menuOpen = false
                                confirmLogout = true
                            }
                        )
                    }
                }
            }

            Spacer(Modifier.height(18.dp))

            // 状态区
            Column(
                Modifier.fillMaxWidth(),
                horizontalAlignment = Alignment.CenterHorizontally
            ) {
                StatusDot(connected)
                Spacer(Modifier.height(12.dp))
                Text(
                    when {
                        connected -> "已连接"
                        busy -> statusText.ifBlank { "连接中…" }
                        // 有本地配置 = 可用（哪怕当前没网）
                        configText.isNotBlank() -> "就绪"
                        statusText.isNotBlank() -> statusText
                        else -> "未连接"
                    },
                    fontSize = 20.sp,
                    fontWeight = FontWeight.Bold
                )
                Spacer(Modifier.height(6.dp))
                Text(
                    WgConfigHelper.extractEndpoint(configText) ?: "47.104.210.199:53",
                    fontSize = 12.sp,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }

            Spacer(Modifier.height(22.dp))

            // 实时速度
            Row(
                Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.Center
            ) {
                SpeedCell("↓", WgService.formatSpeed(WgService.downSpeedValue()))
                Spacer(Modifier.width(28.dp))
                SpeedCell("↑", WgService.formatSpeed(WgService.upSpeedValue()))
            }

            Spacer(Modifier.height(24.dp))

            // 流量配额
            val used = profile?.trafficUsed ?: 0L
            val limit = profile?.trafficLimit ?: 0L
            Column(Modifier.fillMaxWidth()) {
                Row(Modifier.fillMaxWidth()) {
                    Text(
                        "已用 ${WgService.formatBytes(used)}" +
                            if (limit > 0) " / ${WgService.formatBytes(limit)}" else "",
                        fontSize = 13.sp,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.weight(1f)
                    )
                    if (limit > 0) {
                        Text(
                            "${((used * 100) / limit).coerceAtMost(100)}%",
                            fontSize = 13.sp,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                }
                Spacer(Modifier.height(8.dp))
                if (limit > 0) {
                    LinearProgressIndicator(
                        progress = { ((used.toFloat() / limit)).coerceIn(0f, 1f) },
                        modifier = Modifier
                            .fillMaxWidth()
                            .height(6.dp)
                            .clip(RoundedCornerShape(3.dp)),
                        color = BrandPink,
                        trackColor = MaterialTheme.colorScheme.surfaceVariant
                    )
                } else {
                    LinearProgressIndicator(
                        modifier = Modifier
                            .fillMaxWidth()
                            .height(6.dp)
                            .clip(RoundedCornerShape(3.dp)),
                        color = MaterialTheme.colorScheme.surfaceVariant
                    )
                }
            }

            Spacer(Modifier.height(26.dp))

            // 连接 / 断开
            Box(
                Modifier
                    .fillMaxWidth()
                    .height(50.dp)
                    .clip(RoundedCornerShape(16.dp))
                    .background(
                        if (connected) MaterialTheme.colorScheme.surfaceVariant
                        else BrandPink
                    )
                    .clickable(enabled = !busy) {
                        if (connected) disconnect() else connect()
                    },
                contentAlignment = Alignment.Center
            ) {
                if (busy) {
                    CircularProgressIndicator(
                        color = if (connected) MaterialTheme.colorScheme.onSurfaceVariant else Color.White,
                        strokeWidth = 2.dp,
                        modifier = Modifier.size(20.dp)
                    )
                } else {
                    Text(
                        if (connected) "断 开" else "连 接",
                        color = if (connected) MaterialTheme.colorScheme.onSurface else Color.White,
                        fontSize = 16.sp,
                        fontWeight = FontWeight.SemiBold
                    )
                }
            }

            AnimatedVisibility(visible = error != null) {
                Text(
                    error.orEmpty(),
                    color = MaterialTheme.colorScheme.error,
                    fontSize = 13.sp,
                    textAlign = TextAlign.Center,
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(top = 12.dp)
                )
            }

            Spacer(Modifier.height(22.dp))

            // 开关
            ToggleRow(
                title = "自动重连",
                desc = "断线后按 10 秒起自动重试",
                checked = autoReconnect
            ) {
                autoReconnect = it
                WgService.setAutoReconnectEnabled(context, it)
            }

            ToggleRow(
                title = "通知保活",
                desc = "常驻通知显示连接状态与网速，降低被系统回收的概率",
                checked = keepAlive
            ) { on ->
                if (!on) {
                    persistKeepAlive(false)
                    return@ToggleRow
                }
                val permissionGranted = Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU ||
                    ContextCompat.checkSelfPermission(
                        context,
                        Manifest.permission.POST_NOTIFICATIONS
                    ) == PackageManager.PERMISSION_GRANTED
                when {
                    // 首次勾选：先跳系统通知权限
                    !permissionGranted ->
                        notifPermissionLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
                    // 权限给了但用户在系统里关掉了通知：引导去设置
                    !NotificationManagerCompat.from(context).areNotificationsEnabled() -> {
                        persistKeepAlive(false)
                        notifHint = true
                    }
                    else -> persistKeepAlive(true)
                }
            }

            Spacer(Modifier.height(30.dp))
        }
    }

    if (showConfig) {
        AlertDialog(
            onDismissRequest = { showConfig = false },
            title = { Text("WireGuard 配置") },
            text = {
                Text(
                    configText.ifBlank { "尚未获取配置" },
                    fontSize = 11.sp,
                    modifier = Modifier.height(320.dp).verticalScroll(rememberScrollState())
                )
            },
            confirmButton = {
                TextButton(onClick = { showConfig = false }) { Text("关闭") }
            }
        )
    }

    if (notifHint) {
        AlertDialog(
            onDismissRequest = { notifHint = false },
            title = { Text("需要通知权限") },
            text = { Text("通知保活依赖常驻通知来降低被系统回收的概率，请允许本应用发送通知。") },
            confirmButton = {
                TextButton(onClick = {
                    notifHint = false
                    openNotificationSettings()
                }) { Text("去设置") }
            },
            dismissButton = {
                TextButton(onClick = { notifHint = false }) { Text("取消") }
            }
        )
    }

    if (confirmLogout) {
        AlertDialog(
            onDismissRequest = { confirmLogout = false },
            title = { Text("退出 WiFi 账号") },
            text = { Text("退出后需要重新登录才能连接，隧道会被断开。") },
            confirmButton = {
                TextButton(onClick = {
                    confirmLogout = false
                    scope.launch {
                        WgService.markUserDisconnect(context)
                        wgManager.disconnect()
                        WgConfigCache.clear(context)
                        WgService.stop(context)
                        WgSession.clear()
                        onBack()
                    }
                }) { Text("退出") }
            },
            dismissButton = {
                TextButton(onClick = { confirmLogout = false }) { Text("取消") }
            }
        )
    }
}

@Composable
private fun StatusDot(connected: Boolean) {
    Box(
        Modifier
            .size(14.dp)
            .clip(RoundedCornerShape(7.dp))
            .background(
                when {
                    connected -> Color(0xFF4CAF50)
                    else -> MaterialTheme.colorScheme.outline
                }
            )
    )
}

@Composable
private fun SpeedCell(prefix: String, value: String) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Text(prefix, fontSize = 15.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Spacer(Modifier.width(6.dp))
        Text(value, fontSize = 15.sp, fontWeight = FontWeight.Medium)
    }
}

@Composable
private fun ToggleRow(
    title: String,
    desc: String,
    checked: Boolean,
    onChange: (Boolean) -> Unit
) {
    Row(
        Modifier
            .fillMaxWidth()
            .padding(vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Column(Modifier.weight(1f)) {
            Text(title, fontSize = 15.sp)
            Spacer(Modifier.height(2.dp))
            Text(
                desc,
                fontSize = 11.sp,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }
        Switch(
            checked = checked,
            onCheckedChange = onChange,
            colors = SwitchDefaults.colors(checkedThumbColor = Color.White)
        )
    }
}

private fun copyConfig(context: Context, config: String) {
    if (config.isBlank()) return
    val cm = context.getSystemService(Context.CLIPBOARD_SERVICE) as? ClipboardManager ?: return
    cm.setPrimaryClip(ClipData.newPlainText("WireGuard 配置", config))
}
