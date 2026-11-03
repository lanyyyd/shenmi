package com.teatt.pocket.feature.cloud

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Cloud
import androidx.compose.material.icons.filled.ContentPaste
import androidx.compose.material.icons.filled.DeleteOutline
import androidx.compose.material.icons.filled.OpenInBrowser
import androidx.compose.material.icons.filled.QrCodeScanner
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Surface
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
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import com.teatt.pocket.core.ApiException
import com.teatt.pocket.core.CloudAccountDto
import com.teatt.pocket.core.CloudGameDto
import com.teatt.pocket.core.CookieDto
import com.teatt.pocket.core.GachaLinkDto
import com.teatt.pocket.core.GachaRoleDto
import com.teatt.pocket.core.StokenCreateDto
import com.teatt.pocket.data.CloudApi
import kotlinx.coroutines.delay
import com.teatt.pocket.ui.BrandPink
import com.teatt.pocket.ui.LocalBarInset
import com.teatt.pocket.ui.NetworkErrorPane
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
    val scope = rememberCoroutineScope()

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
        loading = false
    }

    LaunchedEffect(token) { reload() }

    fun upload(game: CloudGameDto, cookies: List<CookieDto>) {
        if (cookies.isEmpty()) {
            dialog = "没抓到 Cookie" to "可能还没登录完成，返回页面确认登录后再试一次。"
            return
        }
        busyKey = game.key
        scope.launch {
            try {
                val result = CloudApi.upload(token, game.key, cookies)
                val detail = buildString {
                    append("已保存 ")
                    append(cookies.size)
                    append(" 项 Cookie")
                    if (result.nickname.isNotBlank()) append("\n账号：${result.nickname}")
                    if (result.gameUid.isNotBlank()) append("\nUID：${result.gameUid}")
                    append("\n有效性：").append(if (result.valid == true) "通过" else "未通过（${result.msg}）")
                    when {
                        result.refreshOk == true -> append("\n自动续期：已开启（stoken 换票）")
                        result.refreshOk == false && result.needStoken ->
                            append("\nCookie 里没有 stoken：点卡片上的「扫码」补上后即可自动续期")
                        result.refreshOk == false && result.refreshMsg.isNotBlank() ->
                            append("\n自动续期失败：${result.refreshMsg}")
                    }
                }
                dialog = "抓取完成" to detail
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
                onHarvest = { cookies ->
                    captureTarget = null
                    upload(target, cookies)
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

            if (loading) {
                item {
                    Box(Modifier.fillMaxWidth().padding(top = 80.dp), contentAlignment = Alignment.Center) {
                        CircularProgressIndicator(color = BrandPink, strokeWidth = 2.dp)
                    }
                }
            } else if (accounts.isEmpty()) {
                item { EmptyCard() }
            } else {
                items(accounts, key = { it.game + it.gameUid }) { account ->
                    AccountCard(
                        account = account,
                        busy = busyKey == account.game,
                        onCheck = {
                            busyKey = account.game
                            scope.launch {
                                try {
                                    val result = CloudApi.check(token, account.game, account.gameUid)
                                    dialog = "检测完成" to buildString {
                                        append("有效性：").append(if (result.valid == true) "通过" else "未通过")
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
                            busyKey = account.game
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

    if (gachaOpen) {
        GachaLinkDialog(
            token = token,
            roles = gachaRoles,
            onDismiss = { gachaOpen = false }
        )
    }
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
            Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                Text(
                    "选择游戏账号",
                    fontSize = 12.sp,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
                roles.forEachIndexed { index, item ->
                    Row(
                        Modifier
                            .fillMaxWidth()
                            .clickable { selected = index },
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        RadioButton(
                            selected = selected == index,
                            onClick = { selected = index }
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
                        "抽卡链接",
                        fontSize = 12.sp,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                    Text(link, fontSize = 11.sp)
                    TextButton(onClick = {
                        clipboard.setText(AnnotatedString(link))
                        status = "已复制到剪贴板。"
                    }) { Text("复制链接") }
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
                    val item = role ?: return@TextButton
                    busy = true
                    status = "正在生成…"
                    scope.launch {
                        try {
                            val res: GachaLinkDto =
                                CloudApi.gachaLink(token, item.game, item.gameUid, item.region, item.mid)
                            link = res.url
                            status = if (res.url.isBlank()) {
                                res.detail.ifBlank { "生成失败，请稍后重试" }
                            } else {
                                "生成成功，链接约 24 小时有效。"
                            }
                        } catch (e: Exception) {
                            status = "生成失败：${e.message ?: "请稍后重试"}"
                        } finally {
                            busy = false
                        }
                    }
                }
            ) {
                Text(if (busy) "生成中…" else "生成链接")
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

            if (account.renewalText.isNotBlank()) {
                Spacer(Modifier.height(2.dp))
                Text(
                    account.renewalText,
                    fontSize = 11.sp,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }

            if (account.stokenHint.isNotBlank()) {
                Spacer(Modifier.height(2.dp))
                Text(
                    account.stokenHint,
                    fontSize = 11.sp,
                    color = Color(0xFFB45309)
                )
            }

            Spacer(Modifier.height(4.dp))

            Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
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
                    TextButton(onClick = onStoken, enabled = !busy) {
                        Icon(Icons.Filled.QrCodeScanner, null, Modifier.size(15.dp))
                        Spacer(Modifier.size(4.dp))
                        Text("扫码", fontSize = 13.sp)
                    }
                }
                Spacer(Modifier.weight(1f))
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
