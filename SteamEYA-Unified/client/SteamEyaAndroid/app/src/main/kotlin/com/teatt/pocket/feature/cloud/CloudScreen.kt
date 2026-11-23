package com.teatt.pocket.feature.cloud

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Cloud
import androidx.compose.material.icons.filled.ContentPaste
import androidx.compose.material.icons.filled.DeleteOutline
import androidx.compose.material.icons.filled.Notifications
import androidx.compose.material.icons.filled.OpenInBrowser
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.QrCodeScanner
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Share
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Checkbox
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import com.teatt.pocket.core.ApiException
import com.teatt.pocket.core.CloudAccountDto
import com.teatt.pocket.core.CloudCredential
import com.teatt.pocket.core.CloudCredentialStore
import com.teatt.pocket.core.CloudGameDto
import com.teatt.pocket.core.CookieDto
import com.teatt.pocket.core.CloudUserDto
import com.teatt.pocket.core.ExpiryPushStatusDto
import com.teatt.pocket.core.GachaLinkDto
import com.teatt.pocket.core.GachaRoleDto
import com.teatt.pocket.core.StokenCreateDto
import com.teatt.pocket.data.CloudApi
import kotlinx.coroutines.delay
import com.teatt.pocket.ui.BrandPink
import com.teatt.pocket.ui.LocalBarInset
import com.teatt.pocket.ui.NetworkErrorPane
import coil3.compose.AsyncImage
import kotlinx.coroutines.launch

@Composable
fun CloudScreen(
    token: String,
    modifier: Modifier = Modifier
) {
    var accounts by remember { mutableStateOf<List<CloudAccountDto>>(emptyList()) }
    var games by remember { mutableStateOf<List<CloudGameDto>>(emptyList()) }
    var loading by remember { mutableStateOf(true) }
    // 断网 / 接口失败且本地还没有数据时，整页显示「重试」；已有列表则保留内容只弹窗提示
    var failed by remember { mutableStateOf(false) }
    var busyKey by remember { mutableStateOf<String?>(null) }
    var captureTarget by remember { mutableStateOf<CloudGameDto?>(null) }
    var pasteTarget by remember { mutableStateOf<CloudGameDto?>(null) }
    var scanTarget by remember { mutableStateOf<CloudAccountDto?>(null) }
    var menuOpen by remember { mutableStateOf(false) }
    var dialog by remember { mutableStateOf<Pair<String, String>?>(null) }
    var gachaRoles by remember { mutableStateOf<List<GachaRoleDto>>(emptyList()) }
    var gachaOpen by remember { mutableStateOf(false) }
    var gachaLoading by remember { mutableStateOf(false) }
    var credTarget by remember { mutableStateOf<CloudAccountDto?>(null) }
    var creds by remember { mutableStateOf<Map<String, CloudCredential>>(emptyMap()) }
    var pushStatus by remember { mutableStateOf<ExpiryPushStatusDto?>(null) }
    var pushQrOpen by remember { mutableStateOf(false) }
    var pushBusy by remember { mutableStateOf(false) }
    /** 正在设置分享的账号；null 表示分享弹窗关闭。 */
    var shareTarget by remember { mutableStateOf<CloudAccountDto?>(null) }
    val scope = rememberCoroutineScope()
    val context = LocalContext.current

    /** 上号地址：优先用服务端下发的游戏地址，内置游戏兜底。 */
    fun playUrl(account: CloudAccountDto): String =
        games.firstOrNull { it.key == account.game }?.url?.trim().orEmpty().ifBlank {
            when (account.game) {
                "genshin" -> "https://ys.mihoyo.com/cloud/#/"
                "starrail" -> "https://sr.mihoyo.com/cloud/#/"
                "yihuan" -> "https://yh.wanmei.com/cloud/index.html"
                "bilibili" -> "https://www.bilibili.com/"
                else -> ""
            }
        }

    suspend fun reload() {
        failed = false
        runCatching {
            accounts = CloudApi.my(token)
            games = CloudApi.games(token)
        }.onFailure {
            if (accounts.isEmpty()) {
                failed = true
            } else {
                dialog = "加载失败" to (it.message ?: "请稍后重试")
            }
        }
        creds = runCatching { CloudCredentialStore.list() }
            .getOrNull().orEmpty().associateBy { it.key }
        // 过期提醒状态单独容错：接口没部署/断网时静默保留旧值，不影响账号列表。
        pushStatus = runCatching { CloudApi.expiryPushStatus(token) }.getOrNull() ?: pushStatus
        loading = false
    }

    fun refreshPushStatus() {
        scope.launch {
            pushStatus = runCatching { CloudApi.expiryPushStatus(token) }.getOrNull() ?: pushStatus
        }
    }

    fun setPushEnabled(enabled: Boolean) {
        if (pushStatus?.bound != true) {
            pushQrOpen = true
            return
        }
        pushBusy = true
        scope.launch {
            try {
                pushStatus = CloudApi.setExpiryPushEnabled(token, enabled)
                dialog = "过期提醒" to if (enabled) "已开启，检测到 Cookie 失效会推送微信提醒" else "已关闭微信提醒"
            } catch (e: Exception) {
                dialog = "保存失败" to (e.message ?: "请稍后重试")
                refreshPushStatus()
            } finally {
                pushBusy = false
            }
        }
    }

    fun testPush() {
        pushBusy = true
        scope.launch {
            try {
                CloudApi.testExpiryPush(token)
                dialog = "测试推送" to "已发送，请查看微信是否收到"
            } catch (e: Exception) {
                dialog = "测试推送失败" to (e.message ?: "请确认已绑定且服务端配置正确")
            } finally {
                pushBusy = false
            }
        }
    }

    fun unbindPush() {
        pushBusy = true
        scope.launch {
            try {
                pushStatus = CloudApi.unbindExpiryPush(token)
                dialog = "过期提醒" to "已解除微信绑定"
            } catch (e: Exception) {
                dialog = "解绑失败" to (e.message ?: "请稍后重试")
            } finally {
                pushBusy = false
            }
        }
    }

    LaunchedEffect(token, AutoRefreshCenter.revision) { reload() }

    fun upload(game: CloudGameDto, cookies: List<CookieDto>, comboToken: String = "") {
        if (cookies.isEmpty()) {
            dialog = "没抓到 Cookie" to "可能还没登录完成，返回页面确认登录后再试一次。"
            return
        }
        busyKey = game.key
        scope.launch {
            try {
                val result = CloudApi.upload(token, game.key, cookies, comboToken)
                val detail = buildString {
                    append("已保存 ")
                    append(cookies.size)
                    append(" 项 Cookie")
                    if (result.nickname.isNotBlank()) append("\n账号：${result.nickname}")
                    if (result.gameUid.isNotBlank()) append("\nUID：${result.gameUid}")
                    // msg 是接口文案（上传接口固定「上传成功」），检测结论在 detail 里
                    append("\n有效性：").append(
                        if (result.valid == true) "通过（${result.detail.ifBlank { "已登录" }}）"
                        else "未通过（${result.detail.ifBlank { "服务端没返回原因" }}）"
                    )
                    when {
                        result.refreshOk == true -> append("\n自动续期：已开启（stoken 换票）")
                        result.refreshOk == false && result.refreshMsg.isNotBlank() ->
                            append("\n自动续期失败：${result.refreshMsg}")
                    }
                }
                dialog = (if (result.valid == true) "抓取完成" else "上传完成，检测未通过") to detail
                reload()
            } catch (e: Exception) {
                dialog = "上传失败" to (e.message ?: "请稍后重试")
            } finally {
                busyKey = null
            }
        }
    }

    // 抓取页用全屏 Dialog（独立 Window）承载：放在 Compose 页面里、哪怕提到 RootScreen 的
    // overlay 层，AndroidView 里的 WebView 仍可能被底部玻璃栏压住。Dialog 是另一个 Window，
    // 一定在 Activity 窗口之上，底部按钮不会再被菜单栏挡住。
    captureTarget?.let { target ->
        Dialog(
            onDismissRequest = { captureTarget = null },
            properties = DialogProperties(
                usePlatformDefaultWidth = false,
                decorFitsSystemWindows = false,
                dismissOnClickOutside = false
            )
        ) {
            CaptureView(
                game = target,
                onCancel = { captureTarget = null },
                onHarvest = { cookies, combo ->
                    captureTarget = null
                    upload(target, cookies, combo)
                }
            )
        }
    }

    val bottomInset = LocalBarInset.current + 24.dp

    Box(modifier.fillMaxSize()) {
        LazyColumn(
            Modifier.fillMaxSize(),
            contentPadding = PaddingValues(start = 20.dp, end = 20.dp, top = 0.dp, bottom = bottomInset),
            verticalArrangement = Arrangement.spacedBy(12.dp)
        ) {
            item {
                Row(
                    Modifier
                        .fillMaxWidth()
                        .statusBarsPadding()
                        .padding(top = 18.dp, bottom = 6.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Column(Modifier.weight(1f)) {
                        Text("云账号", fontSize = 26.sp, fontWeight = FontWeight.Bold)
                        Text(
                            "管理云游戏登录态，抓取后可在任意设备使用",
                            fontSize = 12.sp,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                    TextButton(
                        enabled = !gachaLoading,
                        onClick = {
                            gachaLoading = true
                            scope.launch {
                                try {
                                    val res = CloudApi.gachaRoles(token)
                                    gachaRoles = res.roles
                                    if (res.roles.isEmpty()) {
                                        dialog = "没有可用的游戏账号" to (res.detail.ifBlank {
                                            res.errors.firstOrNull()
                                                ?: "请先用米游社 App 扫码补 stoken，并确认米游社已绑定原神/星铁角色。"
                                        })
                                    } else {
                                        gachaOpen = true
                                    }
                                } catch (e: Exception) {
                                    dialog = "获取账号失败" to (e.message ?: "请稍后重试")
                                } finally {
                                    gachaLoading = false
                                }
                            }
                        }
                    ) {
                        if (gachaLoading) {
                            CircularProgressIndicator(
                                Modifier.size(14.dp),
                                color = BrandPink,
                                strokeWidth = 2.dp
                            )
                        } else {
                            Text("抽卡链接", color = BrandPink)
                        }
                    }
                    Box {
                        IconButton(onClick = { menuOpen = true }) {
                            Icon(Icons.Filled.Add, contentDescription = "添加", tint = BrandPink)
                        }
                        DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
                            if (games.isEmpty()) {
                                DropdownMenuItem(
                                    text = { Text("先刷新列表") },
                                    onClick = { menuOpen = false },
                                    enabled = false
                                )
                            }
                            games.forEach { game ->
                                DropdownMenuItem(
                                    text = { Text(game.name) },
                                    onClick = {
                                        menuOpen = false
                                        captureTarget = game
                                    }
                                )
                            }
                        }
                    }
                }
            }

            item {
                ExpiryPushCard(
                    status = pushStatus,
                    busy = pushBusy,
                    onToggle = { setPushEnabled(it) },
                    onBind = { pushQrOpen = true },
                    onTest = { testPush() },
                    onUnbind = { unbindPush() }
                )
            }

            if (loading) {
                item {
                    Box(Modifier.fillMaxWidth().padding(top = 80.dp), contentAlignment = Alignment.Center) {
                        CircularProgressIndicator(color = BrandPink, strokeWidth = 2.dp)
                    }
                }
            } else if (accounts.isEmpty()) {
                item { EmptyCard() }
            } else {
                // 同一游戏可能存了多个账号（比如两个云原神），按游戏分组，多账号时显示数量。
                accounts.groupBy { it.game }.forEach { (_, list) ->
                    if (list.size > 1) {
                        item(key = "hdr_" + list.first().game) {
                            Text(
                                "${list.first().displayName} · ${list.size} 个账号",
                                fontSize = 13.sp,
                                fontWeight = FontWeight.SemiBold,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                modifier = Modifier.padding(top = 4.dp)
                            )
                        }
                    }
                    items(list, key = { it.key }) { account ->
                        AccountCard(
                            account = account,
                            busy = busyKey == account.game || busyKey == account.key,
                            credential = creds[account.credentialKey],
                            canPlay = playUrl(account).isNotBlank(),
                            onCredential = { credTarget = account },
                            onPlay = {
                                val url = playUrl(account)
                                if (url.isBlank()) {
                                    dialog = "无法上号" to "该游戏还没有配置打开地址"
                                } else {
                                    context.startActivity(
                                        CloudPlayActivity.intent(
                                            context,
                                            account.game,
                                            account.displayName,
                                            account.gameUid,
                                            account.nickname,
                                            url,
                                            account.ownerId
                                        )
                                    )
                                }
                            },
                            onShare = { shareTarget = account },
                            onCheck = {
                                busyKey = account.key
                                scope.launch {
                                    try {
                                        val result = CloudApi.check(token, account.game, account.gameUid)
                                        dialog = "检测完成" to buildString {
                                            append("账号：").append(account.displayName)
                                            if (account.gameUid.isNotBlank()) append(" · UID ${account.gameUid}")
                                            append("\n有效性：").append(if (result.valid == true) "通过" else "未通过")
                                            if (result.nickname.isNotBlank()) append("\n昵称：${result.nickname}")
                                            result.freeDuration?.let { append("\n剩余：$it 分钟") }
                                            if (result.msg.isNotBlank()) append("\n").append(result.msg)
                                            if (result.refreshMsg.isNotBlank()) append("\n").append(result.refreshMsg)
                                        }
                                        reload()
                                    } catch (e: Exception) {
                                        dialog = "检测失败" to (e.message ?: "请稍后重试")
                                    } finally {
                                        busyKey = null
                                    }
                                }
                            },
                            onRecapture = { captureTarget = games.firstOrNull { it.key == account.game } },
                            onPaste = {
                                games.firstOrNull { it.key == account.game }?.let { pasteTarget = it }
                            },
                            onStoken = { scanTarget = account },
                            onDelete = {
                                busyKey = account.key
                                scope.launch {
                                    try {
                                        CloudApi.delete(token, account.game, account.gameUid)
                                        reload()
                                    } catch (e: Exception) {
                                        dialog = "删除失败" to (e.message ?: "请稍后重试")
                                    } finally {
                                        busyKey = null
                                    }
                                }
                            }
                        )
                    }
                }
            }
        }

        // 首次拉取就失败（典型场景：断网打开 App）：整页提示 + 重试，和聊天页保持一致
        if (failed) {
            NetworkErrorPane(
                onRetry = {
                    failed = false
                    loading = true
                    scope.launch { reload() }
                }
            )
        }
    }

    dialog?.let { (title, body) ->
        AlertDialog(
            onDismissRequest = { dialog = null },
            title = { Text(title) },
            text = { Text(body) },
            confirmButton = {
                TextButton(onClick = { dialog = null }) { Text("好") }
            }
        )
    }

    pasteTarget?.let { target ->
        PasteDialog(
            game = target,
            onDismiss = { pasteTarget = null },
            onConfirm = { raw ->
                pasteTarget = null
                upload(target, CookieHarvester.parse(raw, target.url))
            }
        )
    }

    scanTarget?.let { account ->
        StokenScanDialog(
            token = token,
            onDismiss = { scanTarget = null },
            onDone = { msg ->
                scanTarget = null
                dialog = "扫码成功" to msg
                scope.launch { reload() }
            }
        )
    }

    shareTarget?.let { account ->
        ShareDialog(
            token = token,
            account = account,
            onDismiss = { shareTarget = null },
            onSaved = { count ->
                shareTarget = null
                dialog = "分享设置" to
                    if (count > 0) "已分享给 $count 位用户，他们可以直接上号" else "已取消该账号的全部分享"
            }
        )
    }

    if (pushQrOpen) {
        ExpiryPushQrDialog(
            token = token,
            onDismiss = { pushQrOpen = false },
            onBound = {
                pushQrOpen = false
                dialog = "微信提醒" to "绑定成功，检测到 Cookie 失效会推送通知"
                refreshPushStatus()
            }
        )
    }

    if (gachaOpen) {
        GachaLinkDialog(
            token = token,
            roles = gachaRoles,
            onDismiss = { gachaOpen = false }
        )
    }

    credTarget?.let { account ->
        CredentialDialog(
            account = account,
            existing = creds[account.credentialKey],
            onDismiss = { credTarget = null },
            onSaved = {
                credTarget = null
                scope.launch { reload() }
            },
            onRefreshNow = {
                credTarget = null
                AutoRefreshCenter.refreshNow(token, account)
            }
        )
    }
}

/** 设置本机保存的账号密码：只用于 App 冷启动时的静默续期。 */
@Composable
private fun CredentialDialog(
    account: CloudAccountDto,
    existing: CloudCredential?,
    onDismiss: () -> Unit,
    onSaved: () -> Unit,
    onRefreshNow: () -> Unit
) {
    var user by remember { mutableStateOf(existing?.account.orEmpty()) }
    var password by remember { mutableStateOf(existing?.password.orEmpty()) }
    var enabled by remember { mutableStateOf(existing?.enabled ?: true) }
    var hint by remember { mutableStateOf("") }
    val scope = rememberCoroutineScope()

    fun persist(next: () -> Unit) {
        if (user.isBlank() || password.isBlank()) {
            hint = "请填写完整的账号和密码"
            return
        }
        scope.launch {
            val saved = CloudCredentialStore.save(
                CloudCredential(
                    game = account.game,
                    gameUid = account.gameUid,
                    account = user.trim(),
                    password = password,
                    enabled = enabled
                )
            )
            if (!saved) {
                hint = "保存失败（本机加密不可用），请重试"
                return@launch
            }
            next()
        }
    }

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("静默续期 · ${account.displayName}") },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState())) {
                Text(
                    "App 冷启动时如果检测到该账号登录态失效，会用这里的账号密码在后台重新登录并更新 Cookie。" +
                        "密码用系统 Keystore 加密后只保存在本机，不会上传服务器。",
                    fontSize = 12.sp,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
                Spacer(Modifier.height(10.dp))
                OutlinedTextField(
                    value = user,
                    onValueChange = { user = it },
                    label = { Text("米哈游账号（手机号 / 邮箱）") },
                    singleLine = true,
                    shape = RoundedCornerShape(12.dp),
                    modifier = Modifier.fillMaxWidth()
                )
                Spacer(Modifier.height(8.dp))
                OutlinedTextField(
                    value = password,
                    onValueChange = { password = it },
                    label = { Text("密码") },
                    singleLine = true,
                    visualTransformation = PasswordVisualTransformation(),
                    shape = RoundedCornerShape(12.dp),
                    modifier = Modifier.fillMaxWidth()
                )
                Spacer(Modifier.height(8.dp))
                Row(
                    Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Text("启用自动静默续期", fontSize = 13.sp)
                    Switch(checked = enabled, onCheckedChange = { enabled = it })
                }
                Text(
                    "登录页的「同意用户协议」会自动勾选；如果仍然遇到验证码，会在后台弹出页面让你手动完成一次。",
                    fontSize = 11.sp,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
                if (hint.isNotBlank()) {
                    Spacer(Modifier.height(6.dp))
                    Text(hint, fontSize = 12.sp, color = MaterialTheme.colorScheme.error)
                }
                if (existing?.lastError?.isNotBlank() == true) {
                    Spacer(Modifier.height(6.dp))
                    Text(
                        "上次失败：${existing.lastError}",
                        fontSize = 11.sp,
                        color = Color(0xFFB45309)
                    )
                }
            }
        },
        confirmButton = {
            TextButton(onClick = { persist(onRefreshNow) }) { Text("保存并更新") }
        },
        dismissButton = {
            Row {
                if (existing != null) {
                    TextButton(onClick = {
                        scope.launch {
                            CloudCredentialStore.delete(account.game, account.gameUid)
                            onSaved()
                        }
                    }) {
                        Text("删除凭据", color = MaterialTheme.colorScheme.error)
                    }
                }
                TextButton(onClick = onDismiss) { Text("取消") }
            }
        }
    )
}

/** 用扫码 stoken 生成抽卡链接：选角色 -> 生成 -> 复制。 */
@Composable
private fun GachaLinkDialog(
    token: String,
    roles: List<GachaRoleDto>,
    onDismiss: () -> Unit
) {
    var selected by remember { mutableStateOf(0) }
    var link by remember { mutableStateOf("") }
    var busy by remember { mutableStateOf(false) }
    var status by remember { mutableStateOf("链接约 24 小时有效，只用来读取抽卡记录，不要发给陌生人。") }
    val scope = rememberCoroutineScope()
    val clipboard = LocalClipboardManager.current
    val role = roles.getOrNull(selected)

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("获取抽卡链接") },
        text = {
            Column(
                Modifier.verticalScroll(rememberScrollState()),
                verticalArrangement = Arrangement.spacedBy(6.dp)
            ) {
                Text(
                    "选择游戏账号",
                    fontSize = 12.sp,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
                roles.forEachIndexed { index, item ->
                    Row(
                        Modifier
                            .fillMaxWidth()
                            .clickable {
                                selected = index
                                link = ""
                                status = "链接约 24 小时有效，只用来读取抽卡记录，不要发给陌生人。"
                            },
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        RadioButton(
                            selected = selected == index,
                            onClick = {
                                selected = index
                                link = ""
                                status = "链接约 24 小时有效，只用来读取抽卡记录，不要发给陌生人。"
                            }
                        )
                        Column(Modifier.weight(1f)) {
                            Text(
                                "${item.gameName} · ${item.nickname.ifBlank { "未命名" }}",
                                fontSize = 14.sp
                            )
                            Text(
                                "UID ${item.gameUid} · " +
                                    item.regionName.ifBlank { if (item.isOfficial) "官服" else "渠道服" },
                                fontSize = 11.sp,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                        }
                    }
                }
                if (link.isNotBlank()) {
                    Spacer(Modifier.height(4.dp))
                    Text(
                        "抽卡链接（已自动复制）",
                        fontSize = 12.sp,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                    Text(
                        if (link.length > 64) link.take(64) + "…" else link,
                        fontSize = 11.sp,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis
                    )
                }
                Text(
                    status,
                    fontSize = 11.sp,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
        },
        confirmButton = {
            TextButton(
                enabled = !busy && role != null,
                onClick = {
                    // 已经生成过就只做复制，避免重复请求换票
                    if (link.isNotBlank()) {
                        clipboard.setText(AnnotatedString(link))
                        status = "已复制到剪贴板。"
                        return@TextButton
                    }
                    val item = role ?: return@TextButton
                    busy = true
                    status = "正在生成…"
                    scope.launch {
                        try {
                            val res: GachaLinkDto =
                                CloudApi.gachaLink(token, item.game, item.gameUid, item.region, item.mid)
                            if (res.url.isBlank()) {
                                status = res.detail.ifBlank { "生成失败，请稍后重试" }
                            } else {
                                link = res.url
                                clipboard.setText(AnnotatedString(res.url))
                                status = "生成成功，已复制到剪贴板（链接约 24 小时有效）。"
                            }
                        } catch (e: Exception) {
                            status = "生成失败：${e.message ?: "请稍后重试"}"
                        } finally {
                            busy = false
                        }
                    }
                }
            ) {
                Text(
                    when {
                        busy -> "生成中…"
                        link.isNotBlank() -> "复制链接"
                        else -> "生成链接"
                    }
                )
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) { Text("关闭") }
        }
    )
}

/** 米游社扫码补 stoken：服务端返回二维码 0/1 矩阵，这里直接画格子，不引二维码库。 */
@Composable
private fun StokenScanDialog(
    token: String,
    onDismiss: () -> Unit,
    onDone: (String) -> Unit
) {
    var created by remember { mutableStateOf<StokenCreateDto?>(null) }
    var status by remember { mutableStateOf("正在向米游社申请二维码…") }
    var terminal by remember { mutableStateOf(false) }

    LaunchedEffect(Unit) {
        try {
            val c = CloudApi.stokenCreate(token)
            if (!c.ok || c.rows.isEmpty()) {
                status = c.msg.ifBlank { "生成二维码失败" }
                terminal = true
                return@LaunchedEffect
            }
            created = c
            status = "请用米游社 App 扫描二维码"
            while (true) {
                delay(2000)
                val r = CloudApi.stokenPoll(token, c.session)
                if (r.msg.isNotBlank()) status = r.msg
                when (r.state) {
                    "confirmed" -> {
                        onDone(r.msg.ifBlank { "扫码成功，stoken 已保存" })
                        return@LaunchedEffect
                    }
                    "expired", "canceled", "error" -> {
                        terminal = true
                        return@LaunchedEffect
                    }
                }
            }
        } catch (e: Exception) {
            status = e.message ?: "网络请求失败"
            terminal = true
        }
    }

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("扫码补 stoken（米游社）") },
        text = {
            Column(horizontalAlignment = Alignment.CenterHorizontally) {
                val qr = created
                if (qr == null) {
                    CircularProgressIndicator(color = BrandPink, strokeWidth = 2.dp, modifier = Modifier.size(26.dp))
                } else {
                    Canvas(
                        Modifier
                            .size(240.dp)
                            .background(Color.White)
                    ) {
                        val rows = qr.rows
                        val cellX = size.width / (rows.firstOrNull()?.length ?: 1)
                        val cellY = size.height / rows.size.coerceAtLeast(1)
                        rows.forEachIndexed { y, row ->
                            row.forEachIndexed { x, ch ->
                                if (ch == '1') {
                                    drawRect(
                                        Color.Black,
                                        topLeft = Offset(x * cellX, y * cellY),
                                        size = Size(cellX + 0.5f, cellY + 0.5f)
                                    )
                                }
                            }
                        }
                    }
                }
                Spacer(Modifier.height(10.dp))
                Text(
                    status,
                    fontSize = 13.sp,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
                Spacer(Modifier.height(6.dp))
                Text(
                    "扫码只用于获取长期 stoken，不会上传账号密码；combo_token 仍需按原方式抓取。",
                    fontSize = 11.sp,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
        },
        confirmButton = {
            TextButton(onClick = onDismiss) { Text(if (terminal) "关闭" else "取消") }
        }
    )
}

/** 云游戏 Cookie 过期微信提醒：微信扫码绑定后，每日检测到失效时推送。 */
@Composable
private fun ExpiryPushCard(
    status: ExpiryPushStatusDto?,
    busy: Boolean,
    onToggle: (Boolean) -> Unit,
    onBind: () -> Unit,
    onTest: () -> Unit,
    onUnbind: () -> Unit
) {
    val bound = status?.bound == true
    val configured = status?.configured == true
    Surface(
        shape = RoundedCornerShape(20.dp),
        color = MaterialTheme.colorScheme.surface,
        modifier = Modifier.fillMaxWidth()
    ) {
        Column(Modifier.padding(16.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(Icons.Filled.Notifications, null, Modifier.size(18.dp), tint = BrandPink)
                Spacer(Modifier.size(9.dp))
                Column(Modifier.weight(1f)) {
                    Text("Cookie 过期微信提醒", fontWeight = FontWeight.SemiBold, fontSize = 15.sp)
                    Spacer(Modifier.height(2.dp))
                    Text(
                        when {
                            status == null -> "正在读取绑定状态…"
                            !configured -> "管理员尚未配置 PushPlus，暂时无法绑定"
                            bound -> {
                                val nick = status?.nickname.orEmpty()
                                "已绑定${if (nick.isBlank()) "" else "（$nick）"}：检测到失效会推送微信提醒"
                            }
                            else -> "微信扫码绑定后，每日检测到 Cookie 失效会推送提醒"
                        },
                        fontSize = 11.5.sp,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
                Switch(
                    checked = bound && status?.enabled == true,
                    enabled = !busy && configured,
                    onCheckedChange = onToggle
                )
            }
            if (configured) {
                Row(
                    Modifier
                        .fillMaxWidth()
                        .padding(top = 4.dp),
                    horizontalArrangement = Arrangement.spacedBy(2.dp)
                ) {
                    TextButton(onClick = onBind, enabled = !busy) {
                        Icon(Icons.Filled.QrCodeScanner, null, Modifier.size(15.dp))
                        Spacer(Modifier.size(4.dp))
                        Text(if (bound) "重新绑定" else "扫码绑定", fontSize = 13.sp)
                    }
                    if (bound) {
                        TextButton(onClick = onTest, enabled = !busy) { Text("测试推送", fontSize = 13.sp) }
                        TextButton(onClick = onUnbind, enabled = !busy) { Text("解绑", fontSize = 13.sp) }
                    }
                }
            }
        }
    }
}

/** 微信扫码绑定：展示 pushplus 个人二维码，2 秒轮询一次绑定状态。 */
@Composable
private fun ExpiryPushQrDialog(
    token: String,
    onDismiss: () -> Unit,
    onBound: () -> Unit
) {
    var qrUrl by remember { mutableStateOf("") }
    var message by remember { mutableStateOf("正在生成二维码…") }

    LaunchedEffect(Unit) {
        try {
            val qr = CloudApi.expiryPushQr(token)
            if (!qr.ok || qr.url.isBlank()) {
                message = qr.detail.ifBlank { "生成二维码失败：请确认管理员已配置 PushPlus" }
                return@LaunchedEffect
            }
            qrUrl = qr.url
            message = "请用微信扫码，扫码成功后自动绑定"
            while (true) {
                delay(2000)
                val st = runCatching { CloudApi.expiryPushStatus(token) }.getOrNull() ?: continue
                if (st.bound) {
                    onBound()
                    return@LaunchedEffect
                }
            }
        } catch (e: Exception) {
            message = e.message ?: "生成二维码失败"
        }
    }

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("微信扫码绑定过期提醒") },
        text = {
            Column(horizontalAlignment = Alignment.CenterHorizontally) {
                if (qrUrl.isNotBlank()) {
                    Surface(
                        shape = RoundedCornerShape(12.dp),
                        color = Color.White,
                        modifier = Modifier.padding(6.dp)
                    ) {
                        AsyncImage(
                            model = qrUrl,
                            contentDescription = "绑定二维码",
                            contentScale = ContentScale.Fit,
                            modifier = Modifier
                                .size(240.dp)
                                .padding(8.dp)
                        )
                    }
                } else {
                    CircularProgressIndicator(color = BrandPink, strokeWidth = 2.dp)
                }
                Spacer(Modifier.height(12.dp))
                Text(
                    message,
                    fontSize = 12.sp,
                    textAlign = TextAlign.Center,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
                Spacer(Modifier.height(4.dp))
                Text(
                    "二维码 10 分钟内有效，仅能扫描一次",
                    fontSize = 11.sp,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
        },
        confirmButton = {
            TextButton(onClick = onDismiss) { Text("关闭") }
        }
    )
}

@Composable
private fun EmptyCard() {
    Surface(
        shape = RoundedCornerShape(20.dp),
        color = MaterialTheme.colorScheme.surface,
        modifier = Modifier.fillMaxWidth()
    ) {
        Column(
            Modifier.padding(24.dp),
            horizontalAlignment = Alignment.CenterHorizontally
        ) {
            Icon(
                Icons.Filled.Cloud,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.size(36.dp)
            )
            Spacer(Modifier.height(12.dp))
            Text("还没有云账号", fontWeight = FontWeight.SemiBold)
            Spacer(Modifier.height(6.dp))
            Text(
                "点右上角 + 选一个游戏，在弹出的页面里登录，登录完成后点「我已登录」即可抓取。",
                fontSize = 12.sp,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }
    }
}

@Composable
private fun AccountCard(
    account: CloudAccountDto,
    busy: Boolean,
    credential: CloudCredential?,
    canPlay: Boolean,
    onCredential: () -> Unit,
    onPlay: () -> Unit,
    onShare: () -> Unit,
    onCheck: () -> Unit,
    onRecapture: () -> Unit,
    onPaste: () -> Unit,
    onStoken: () -> Unit,
    onDelete: () -> Unit
) {
    Surface(
        shape = RoundedCornerShape(20.dp),
        color = MaterialTheme.colorScheme.surface,
        modifier = Modifier.fillMaxWidth()
    ) {
        Column(Modifier.padding(16.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(
                    Modifier
                        .size(9.dp)
                        .background(
                            when (account.valid) {
                                1 -> Color(0xFF22C55E)
                                0 -> Color(0xFFEF4444)
                                else -> MaterialTheme.colorScheme.outline
                            },
                            CircleShape
                        )
                )
                Spacer(Modifier.size(9.dp))
                Text(account.displayName, fontWeight = FontWeight.SemiBold, fontSize = 16.sp)
                Spacer(Modifier.weight(1f))
                Text(account.statusText, fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }

            Spacer(Modifier.height(8.dp))

            Text(
                buildString {
                    append(account.nickname.ifBlank { "未获取昵称" })
                    if (account.gameUid.isNotBlank()) append(" · UID ${account.gameUid}")
                    account.freeDuration?.let { append(" · 剩余 $it 分钟") }
                },
                fontSize = 12.sp,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )

            if (account.shared) {
                Spacer(Modifier.height(2.dp))
                Text(
                    "来自 ${account.ownerName.ifBlank { account.ownerUsername.ifBlank { "好友" } }} 的分享",
                    fontSize = 11.sp,
                    color = BrandPink
                )
            }

            if (!account.shared && account.renewalText.isNotBlank()) {
                Spacer(Modifier.height(2.dp))
                Text(
                    account.renewalText,
                    fontSize = 11.sp,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }

            if (!account.shared && account.game in setOf("genshin", "starrail")) {
                Spacer(Modifier.height(2.dp))
                Text(
                    when {
                        credential == null -> "静默续期：未设置"
                        credential.enabled -> "静默续期：已开启"
                        else -> "静默续期：已暂停"
                    },
                    fontSize = 11.sp,
                    color = if (credential?.enabled == true) Color(0xFF15803D)
                    else MaterialTheme.colorScheme.onSurfaceVariant
                )
            }

            Spacer(Modifier.height(4.dp))

            Row(verticalAlignment = Alignment.CenterVertically) {
                // 按钮多，窄屏放不下：横向滚动，删除键固定在右侧
                Row(
                    Modifier
                        .weight(1f)
                        .horizontalScroll(rememberScrollState()),
                    horizontalArrangement = Arrangement.spacedBy(2.dp)
                ) {
                    if (canPlay) {
                        TextButton(onClick = onPlay, enabled = !busy) {
                            Icon(Icons.Filled.PlayArrow, null, Modifier.size(15.dp))
                            Spacer(Modifier.size(4.dp))
                            Text("上号", fontSize = 13.sp)
                        }
                    }
                    if (!account.shared) {
                        TextButton(onClick = onCheck, enabled = !busy) {
                            if (busy) {
                                CircularProgressIndicator(strokeWidth = 2.dp, modifier = Modifier.size(14.dp))
                            } else {
                                Icon(Icons.Filled.Refresh, null, Modifier.size(15.dp))
                                Spacer(Modifier.size(4.dp))
                                Text("检测", fontSize = 13.sp)
                            }
                        }
                        TextButton(onClick = onRecapture, enabled = !busy) {
                            Icon(Icons.Filled.OpenInBrowser, null, Modifier.size(15.dp))
                            Spacer(Modifier.size(4.dp))
                            Text("重抓", fontSize = 13.sp)
                        }
                        TextButton(onClick = onPaste, enabled = !busy) {
                            Icon(Icons.Filled.ContentPaste, null, Modifier.size(15.dp))
                            Spacer(Modifier.size(4.dp))
                            Text("粘贴", fontSize = 13.sp)
                        }
                        if (account.game in setOf("genshin", "starrail")) {
                            TextButton(onClick = onCredential, enabled = !busy) {
                                Text("续期", fontSize = 13.sp)
                            }
                            TextButton(onClick = onStoken, enabled = !busy) {
                                Icon(Icons.Filled.QrCodeScanner, null, Modifier.size(15.dp))
                                Spacer(Modifier.size(4.dp))
                                Text("扫码", fontSize = 13.sp)
                            }
                        }
                        TextButton(onClick = onShare, enabled = !busy) {
                            Icon(Icons.Filled.Share, null, Modifier.size(15.dp))
                            Spacer(Modifier.size(4.dp))
                            Text("分享", fontSize = 13.sp)
                        }
                    }
                }
                if (!account.shared) {
                    IconButton(onClick = onDelete, enabled = !busy) {
                        Icon(
                            Icons.Filled.DeleteOutline,
                            contentDescription = "删除",
                            tint = MaterialTheme.colorScheme.onSurfaceVariant,
                            modifier = Modifier.size(18.dp)
                        )
                    }
                }
            }
        }
    }
}

@Composable
private fun PasteDialog(
    game: CloudGameDto,
    onDismiss: () -> Unit,
    onConfirm: (String) -> Unit
) {
    val clipboard = LocalClipboardManager.current
    var text by remember { mutableStateOf("") }

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("粘贴 ${game.name} 的 Cookie") },
        text = {
            Column {
                Text(
                    "形如 uid=xxx; ltoken=xxx; cookie_token=xxx，从电脑浏览器或桌面端复制过来即可。",
                    fontSize = 12.sp,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
                Spacer(Modifier.height(10.dp))
                OutlinedTextField(
                    value = text,
                    onValueChange = { text = it },
                    minLines = 4,
                    maxLines = 8,
                    shape = RoundedCornerShape(12.dp),
                    modifier = Modifier.fillMaxWidth()
                )
                TextButton(onClick = {
                    clipboard.getText()?.text?.let { text = it }
                }) {
                    Text("从剪贴板粘贴", fontSize = 13.sp)
                }
            }
        },
        confirmButton = {
            TextButton(onClick = { onConfirm(text) }, enabled = text.isNotBlank()) { Text("上传") }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) { Text("取消") }
        }
    )
}

/**
 * 分享账号：所有用户单选/多选，保存时整组替换。
 * 被分享的人会在自己的云游戏列表里看到这张卡（只读、可直接上号）。
 */
@Composable
private fun ShareDialog(
    token: String,
    account: CloudAccountDto,
    onDismiss: () -> Unit,
    onSaved: (Int) -> Unit
) {
    val scope = rememberCoroutineScope()
    var users by remember { mutableStateOf<List<CloudUserDto>>(emptyList()) }
    var selected by remember { mutableStateOf<Set<Int>>(emptySet()) }
    var loading by remember { mutableStateOf(true) }
    var saving by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf("") }
    var keyword by remember { mutableStateOf("") }

    LaunchedEffect(account.key) {
        runCatching {
            users = CloudApi.shareUsers(token)
            selected = CloudApi.shareTargets(token, account.game, account.gameUid)
                .map { it.id }.toSet()
        }.onFailure {
            error = it.message ?: "加载用户列表失败"
        }
        loading = false
    }

    val filtered = remember(users, keyword) {
        val kw = keyword.trim()
        if (kw.isEmpty()) {
            users
        } else {
            users.filter {
                it.username.contains(kw, ignoreCase = true) ||
                    it.nickname.contains(kw, ignoreCase = true)
            }
        }
    }

    fun toggle(id: Int) {
        selected = if (id in selected) selected - id else selected + id
    }

    AlertDialog(
        onDismissRequest = { if (!saving) onDismiss() },
        title = { Text("分享账号") },
        text = {
            Column {
                Text(
                    buildString {
                        append(account.nickname.ifBlank { account.displayName })
                        if (account.gameUid.isNotBlank()) append(" · UID ${account.gameUid}")
                    },
                    fontSize = 12.sp,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
                Spacer(Modifier.height(10.dp))
                when {
                    loading -> Box(
                        Modifier.fillMaxWidth().height(120.dp),
                        contentAlignment = Alignment.Center
                    ) { CircularProgressIndicator(color = BrandPink, strokeWidth = 2.dp) }

                    error.isNotBlank() -> Text(
                        error,
                        fontSize = 13.sp,
                        color = MaterialTheme.colorScheme.error
                    )

                    else -> {
                        OutlinedTextField(
                            value = keyword,
                            onValueChange = { keyword = it },
                            placeholder = { Text("搜索用户", fontSize = 13.sp) },
                            singleLine = true,
                            shape = RoundedCornerShape(12.dp),
                            modifier = Modifier.fillMaxWidth()
                        )
                        Spacer(Modifier.height(6.dp))
                        Text(
                            "已选 ${selected.size} 人",
                            fontSize = 12.sp,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                        if (users.isEmpty()) {
                            Text(
                                "还没有其他用户可以分享",
                                fontSize = 13.sp,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                modifier = Modifier.padding(top = 20.dp)
                            )
                        } else {
                            LazyColumn(
                                Modifier.fillMaxWidth().heightIn(max = 300.dp)
                            ) {
                                items(filtered, key = { it.id }) { user ->
                                    val checked = user.id in selected
                                    Row(
                                        Modifier
                                            .fillMaxWidth()
                                            .clickable { toggle(user.id) }
                                            .padding(vertical = 2.dp),
                                        verticalAlignment = Alignment.CenterVertically
                                    ) {
                                        Checkbox(
                                            checked = checked,
                                            onCheckedChange = { toggle(user.id) }
                                        )
                                        Column {
                                            Text(user.displayName, fontSize = 14.sp)
                                            if (user.nickname.isNotBlank() && user.username.isNotBlank()) {
                                                Text(
                                                    user.username,
                                                    fontSize = 11.sp,
                                                    color = MaterialTheme.colorScheme.onSurfaceVariant
                                                )
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        },
        confirmButton = {
            TextButton(
                enabled = !loading && !saving && error.isBlank(),
                onClick = {
                    saving = true
                    scope.launch {
                        try {
                            val result = CloudApi.saveShares(
                                token, account.game, account.gameUid, selected.toList()
                            )
                            onSaved(result.count)
                        } catch (e: Exception) {
                            error = e.message ?: "保存失败"
                        } finally {
                            saving = false
                        }
                    }
                }
            ) { Text(if (saving) "保存中…" else "保存") }
        },
        dismissButton = {
            TextButton(onClick = onDismiss, enabled = !saving) { Text("取消") }
        }
    )
}
