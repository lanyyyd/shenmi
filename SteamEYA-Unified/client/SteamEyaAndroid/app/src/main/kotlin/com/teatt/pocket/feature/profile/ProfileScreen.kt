package com.teatt.pocket.feature.profile

import android.net.Uri
import android.webkit.CookieManager
import android.webkit.WebStorage
import android.widget.Toast
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
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
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Build
import androidx.compose.material.icons.filled.ChevronRight
import androidx.compose.material.icons.filled.DeleteOutline
import androidx.compose.material.icons.filled.Logout
import androidx.compose.material.icons.filled.Person
import androidx.compose.material.icons.filled.SystemUpdate
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import coil3.compose.AsyncImage
import com.teatt.pocket.core.ApiException
import com.teatt.pocket.core.AppVersion
import com.teatt.pocket.core.Session
import com.teatt.pocket.core.SessionState
import com.teatt.pocket.data.ProfileApi
import com.teatt.pocket.feature.cloud.CloudGachaHistory
import com.teatt.pocket.feature.cloud.GachaCapture
import com.teatt.pocket.feature.update.UpdateCenter
import com.teatt.pocket.feature.wireguard.WgSession
import com.teatt.pocket.feature.wireguard.WgToolsHost
import com.teatt.pocket.ui.BrandPink
import com.teatt.pocket.ui.BrandViolet
import com.teatt.pocket.ui.LocalBarInset
import kotlinx.coroutines.launch
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

@Composable
fun ProfileScreen(
    state: SessionState,
    onLoggedOut: () -> Unit,
    modifier: Modifier = Modifier
) {
    var dialog by remember { mutableStateOf<ProfileDialog?>(null) }
    var message by remember { mutableStateOf<String?>(null) }
    var busy by remember { mutableStateOf(false) }
    var showTools by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()
    val context = LocalContext.current
    var gachaEnabled by remember { mutableStateOf(CloudGachaHistory.isEnabled(context)) }
    var gachaHistory by remember { mutableStateOf(CloudGachaHistory.list(context)) }
    var confirmClearCookies by remember { mutableStateOf(false) }

    // 上号是独立 Activity，回到「我的」时重新读一次本地历史
    val lifecycleOwner = LocalLifecycleOwner.current
    DisposableEffect(lifecycleOwner, context) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_RESUME) {
                gachaEnabled = CloudGachaHistory.isEnabled(context)
                gachaHistory = CloudGachaHistory.list(context)
            }
        }
        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose { lifecycleOwner.lifecycle.removeObserver(observer) }
    }

    // 头像上传：系统相册选择器（Android 13+ 的原生 Photo Picker，低版本自动走兼容实现）
    val pickImage = rememberLauncherForActivityResult(
        ActivityResultContracts.PickVisualMedia()
    ) { uri: Uri? ->
        if (uri == null) return@rememberLauncherForActivityResult
        busy = true
        scope.launch {
            try {
                val resolver = context.contentResolver
                val bytes = resolver.openInputStream(uri)?.use { it.readBytes() }
                    ?: throw IllegalStateException("读取图片失败")
                if (bytes.size > 2 * 1024 * 1024) {
                    message = "头像不能超过 2MB"
                    return@launch
                }
                val mime = resolver.getType(uri) ?: "image/jpeg"
                val ext = when {
                    mime.contains("png") -> "png"
                    mime.contains("webp") -> "webp"
                    mime.contains("gif") -> "gif"
                    else -> "jpg"
                }
                val path = ProfileApi.uploadAvatar(Session.token(), bytes, "avatar.$ext", mime)
                Session.updateAvatar(path)
                message = "头像已更新"
            } catch (e: Exception) {
                message = e.message ?: "头像上传失败"
            } finally {
                busy = false
            }
        }
    }

    LazyColumn(
        modifier.fillMaxSize(),
        contentPadding = PaddingValues(
            start = 20.dp,
            end = 20.dp,
            top = 0.dp,
            bottom = LocalBarInset.current + 24.dp
        ),
        verticalArrangement = Arrangement.spacedBy(14.dp)
    ) {
        item {
            Text(
                "我的",
                fontSize = 26.sp,
                fontWeight = FontWeight.Bold,
                modifier = Modifier
                    .statusBarsPadding()
                    .padding(top = 18.dp, bottom = 10.dp)
            )
        }

        item {
            Surface(shape = RoundedCornerShape(24.dp), color = Color.Transparent) {
                Row(
                    Modifier
                        .fillMaxWidth()
                        .background(
                            Brush.linearGradient(
                                listOf(BrandPink.copy(alpha = 0.16f), BrandViolet.copy(alpha = 0.14f))
                            )
                        )
                        .padding(18.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Box(
                        Modifier
                            .size(64.dp)
                            .clip(CircleShape)
                            .background(Color.White.copy(alpha = 0.65f))
                            .clickable(enabled = !busy) {
                                pickImage.launch(
                                    PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly)
                                )
                            },
                        contentAlignment = Alignment.Center
                    ) {
                        if (state.avatarUrl != null) {
                            AsyncImage(
                                model = state.avatarUrl,
                                contentDescription = "头像",
                                contentScale = ContentScale.Crop,
                                modifier = Modifier.fillMaxSize()
                            )
                        } else {
                            Icon(
                                Icons.Filled.Person,
                                contentDescription = "头像",
                                tint = BrandPink,
                                modifier = Modifier.size(30.dp)
                            )
                        }
                        if (busy) {
                            CircularProgressIndicator(
                                strokeWidth = 2.dp,
                                color = BrandPink,
                                modifier = Modifier.size(22.dp)
                            )
                        }
                    }

                    Spacer(Modifier.size(16.dp))

                    Column(Modifier.weight(1f)) {
                        Text(
                            state.nickname.ifBlank { state.username },
                            fontSize = 19.sp,
                            fontWeight = FontWeight.Bold
                        )
                        Spacer(Modifier.height(4.dp))
                        Text(
                            "Lv.${state.level} · @${state.username}",
                            fontSize = 12.sp,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                }
            }
        }

        item {
            Surface(shape = RoundedCornerShape(20.dp), color = MaterialTheme.colorScheme.surface) {
                Column {
                    SettingRow(Icons.Filled.Person, "修改昵称", state.nickname.ifBlank { state.username }) {
                        dialog = ProfileDialog.Nickname(state.nickname.ifBlank { state.username })
                    }
                    SettingRow(Icons.Filled.Person, "修改密码", "至少 6 位") {
                        dialog = ProfileDialog.Password
                    }
                    SettingRow(
                        Icons.Filled.Build,
                        "小工具",
                        if (WgSession.loggedIn) "WiFi" else "未登录"
                    ) {
                        showTools = true
                    }
                    SettingRow(
                        Icons.Filled.SystemUpdate,
                        "检查更新",
                        if (UpdateCenter.checking) "检查中…" else "当前 ${AppVersion.current}"
                    ) {
                        if (!UpdateCenter.checking) {
                            scope.launch { UpdateCenter.check(Session.token(), manual = true) }
                        }
                    }
                    SettingRow(
                        Icons.Filled.Logout,
                        "退出登录",
                        "",
                        danger = true
                    ) {
                        scope.launch {
                            Session.clear()
                            onLoggedOut()
                        }
                    }
                }
            }
        }

        // 清除 WebView 登录态：云账号抓取、内置浏览器都用同一个 Cookie 容器
        item {
            Surface(shape = RoundedCornerShape(20.dp), color = MaterialTheme.colorScheme.surface) {
                Row(
                    Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 16.dp, vertical = 14.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Column(Modifier.weight(1f)) {
                        Text("清除 WebView 的 Cookies", fontSize = 15.sp, fontWeight = FontWeight.SemiBold)
                        Spacer(Modifier.height(4.dp))
                        Text(
                            "清除所有 WebView 的 Cookies，这将使你从绝大多数网页退出登录。用户 Cookies 的导出不受此影响。",
                            fontSize = 12.sp,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                    IconButton(onClick = { confirmClearCookies = true }) {
                        Icon(
                            Icons.Filled.DeleteOutline,
                            contentDescription = "清除 WebView 的 Cookies",
                            tint = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                }
            }
        }

        // 云游戏抽卡链接：开关放右侧，下方展示本地抓取历史
        item {
            Surface(shape = RoundedCornerShape(20.dp), color = MaterialTheme.colorScheme.surface) {
                Column(Modifier.padding(horizontal = 16.dp, vertical = 14.dp)) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Column(Modifier.weight(1f)) {
                            Text("云游戏抽卡链接", fontSize = 15.sp, fontWeight = FontWeight.SemiBold)
                            Spacer(Modifier.height(4.dp))
                            Text(
                                "开启后，点击上号进入云原神 / 云崩铁时自动抓取抽卡链接并复制",
                                fontSize = 12.sp,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                        }
                        Switch(
                            checked = gachaEnabled,
                            onCheckedChange = {
                                gachaEnabled = it
                                CloudGachaHistory.setEnabled(context, it)
                            }
                        )
                    }

                    if (gachaHistory.isNotEmpty()) {
                        Spacer(Modifier.height(12.dp))
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Text(
                                "抓取历史",
                                fontSize = 13.sp,
                                fontWeight = FontWeight.SemiBold,
                                modifier = Modifier.weight(1f)
                            )
                            Text(
                                "点击任意一条复制链接",
                                fontSize = 11.sp,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                        }
                        Spacer(Modifier.height(4.dp))
                        gachaHistory.forEach { item ->
                            GachaHistoryRow(
                                item = item,
                                onCopy = {
                                    CloudGachaHistory.copy(context, item.link)
                                    Toast.makeText(context, "已复制抽卡链接", Toast.LENGTH_SHORT).show()
                                }
                            )
                        }
                    }
                }
            }
        }
    }

    if (showTools) {
        WgToolsHost(onDismiss = { showTools = false })
    }

    if (confirmClearCookies) {
        AlertDialog(
            onDismissRequest = { confirmClearCookies = false },
            title = { Text("清除 WebView 的 Cookies") },
            text = {
                Text("清除所有 WebView 的 Cookies，这将使你从绝大多数网页退出登录。用户 Cookies 的导出不受此影响。")
            },
            confirmButton = {
                TextButton(onClick = {
                    confirmClearCookies = false
                    runCatching {
                        CookieManager.getInstance().removeAllCookies(null)
                        CookieManager.getInstance().flush()
                        WebStorage.getInstance().deleteAllData()
                    }
                    message = "WebView 的 Cookies 已清除"
                }) { Text("清除") }
            },
            dismissButton = {
                TextButton(onClick = { confirmClearCookies = false }) { Text("取消") }
            }
        )
    }

    when (val current = dialog) {
        is ProfileDialog.Nickname -> NicknameDialog(
            initial = current.value,
            onDismiss = { dialog = null },
            onConfirm = { nick ->
                dialog = null
                scope.launch {
                    try {
                        val saved = ProfileApi.updateNickname(Session.token(), nick)
                        Session.updateNickname(saved)
                        message = "昵称已更新"
                    } catch (e: ApiException) {
                        message = e.message
                    }
                }
            }
        )

        ProfileDialog.Password -> PasswordDialog(
            onDismiss = { dialog = null },
            onConfirm = { old, new ->
                dialog = null
                scope.launch {
                    try {
                        ProfileApi.changePassword(Session.token(), old, new)
                        message = "密码已修改。聊天页会需要重新加载一次登录态。"
                    } catch (e: ApiException) {
                        message = e.message
                    }
                }
            }
        )

        null -> Unit
    }

    message?.let {
        AlertDialog(
            onDismissRequest = { message = null },
            title = { Text("提示") },
            text = { Text(it) },
            confirmButton = { TextButton(onClick = { message = null }) { Text("好") } }
        )
    }
}

private sealed interface ProfileDialog {
    data class Nickname(val value: String) : ProfileDialog
    data object Password : ProfileDialog
}

@Composable
private fun SettingRow(
    icon: ImageVector,
    title: String,
    hint: String,
    danger: Boolean = false,
    onClick: () -> Unit
) {
    Row(
        Modifier
            .fillMaxWidth()
            .clickable(onClick = onClick)
            .padding(horizontal = 16.dp, vertical = 15.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Icon(
            icon,
            contentDescription = null,
            modifier = Modifier.size(18.dp),
            tint = if (danger) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant
        )
        Spacer(Modifier.size(12.dp))
        Text(
            title,
            fontSize = 15.sp,
            color = if (danger) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurface
        )
        Spacer(Modifier.weight(1f))
        if (hint.isNotBlank()) {
            Text(hint, fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Spacer(Modifier.size(4.dp))
        }
        Icon(
            Icons.Filled.ChevronRight,
            contentDescription = null,
            modifier = Modifier.size(18.dp),
            tint = MaterialTheme.colorScheme.outline
        )
    }
}

@Composable
private fun GachaHistoryRow(item: GachaCapture, onCopy: () -> Unit) {
    Column(
        Modifier
            .fillMaxWidth()
            .clickable(onClick = onCopy)
            .padding(vertical = 8.dp)
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(
                item.gameName.ifBlank { "云游戏" },
                fontSize = 13.sp,
                fontWeight = FontWeight.SemiBold
            )
            if (item.gameUid.isNotBlank()) {
                Spacer(Modifier.size(6.dp))
                Text(
                    "UID ${item.gameUid}",
                    fontSize = 11.sp,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
            if (item.nickname.isNotBlank()) {
                Spacer(Modifier.size(6.dp))
                Text(
                    item.nickname,
                    fontSize = 11.sp,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
            Spacer(Modifier.weight(1f))
            Text(
                SimpleDateFormat("MM-dd HH:mm", Locale.getDefault()).format(Date(item.time)),
                fontSize = 11.sp,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }
        Spacer(Modifier.height(3.dp))
        Text(
            item.link,
            fontSize = 11.sp,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            maxLines = 2,
            overflow = TextOverflow.Ellipsis
        )
    }
}

@Composable
private fun NicknameDialog(
    initial: String,
    onDismiss: () -> Unit,
    onConfirm: (String) -> Unit
) {
    var value by remember { mutableStateOf(initial) }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("修改昵称") },
        text = {
            OutlinedTextField(
                value = value,
                onValueChange = { value = it.take(20) },
                singleLine = true,
                shape = RoundedCornerShape(12.dp),
                modifier = Modifier.fillMaxWidth()
            )
        },
        confirmButton = {
            TextButton(onClick = { onConfirm(value.trim()) }, enabled = value.trim().isNotEmpty()) {
                Text("保存")
            }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("取消") } }
    )
}

@Composable
private fun PasswordDialog(
    onDismiss: () -> Unit,
    onConfirm: (String, String) -> Unit
) {
    var old by remember { mutableStateOf("") }
    var fresh by remember { mutableStateOf("") }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("修改密码") },
        text = {
            Column {
                OutlinedTextField(
                    value = old,
                    onValueChange = { old = it },
                    label = { Text("原密码") },
                    singleLine = true,
                    visualTransformation = PasswordVisualTransformation(),
                    shape = RoundedCornerShape(12.dp),
                    modifier = Modifier.fillMaxWidth()
                )
                Spacer(Modifier.height(10.dp))
                OutlinedTextField(
                    value = fresh,
                    onValueChange = { fresh = it },
                    label = { Text("新密码（至少 6 位）") },
                    singleLine = true,
                    visualTransformation = PasswordVisualTransformation(),
                    shape = RoundedCornerShape(12.dp),
                    modifier = Modifier.fillMaxWidth()
                )
            }
        },
        confirmButton = {
            TextButton(
                onClick = { onConfirm(old, fresh) },
                enabled = old.isNotEmpty() && fresh.length >= 6
            ) { Text("确认修改") }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("取消") } }
    )
}
