package com.teatt.pocket.feature.cloud

import androidx.compose.foundation.background
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
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
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
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import com.teatt.pocket.core.ApiException
import com.teatt.pocket.core.CloudAccountDto
import com.teatt.pocket.core.CloudGameDto
import com.teatt.pocket.core.CookieDto
import com.teatt.pocket.data.CloudApi
import com.teatt.pocket.ui.BrandPink
import com.teatt.pocket.ui.LocalBarInset
import kotlinx.coroutines.launch

@Composable
fun CloudScreen(
    token: String,
    modifier: Modifier = Modifier
) {
    var accounts by remember { mutableStateOf<List<CloudAccountDto>>(emptyList()) }
    var games by remember { mutableStateOf<List<CloudGameDto>>(emptyList()) }
    var loading by remember { mutableStateOf(true) }
    var busyKey by remember { mutableStateOf<String?>(null) }
    var captureTarget by remember { mutableStateOf<CloudGameDto?>(null) }
    var pasteTarget by remember { mutableStateOf<CloudGameDto?>(null) }
    var menuOpen by remember { mutableStateOf(false) }
    var dialog by remember { mutableStateOf<Pair<String, String>?>(null) }
    val scope = rememberCoroutineScope()

    suspend fun reload() {
        runCatching {
            accounts = CloudApi.my(token)
            games = CloudApi.games(token)
        }.onFailure { dialog = "加载失败" to (it.message ?: "请稍后重试") }
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
