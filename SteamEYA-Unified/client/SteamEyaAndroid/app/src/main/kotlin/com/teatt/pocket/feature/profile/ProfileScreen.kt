package com.teatt.pocket.feature.profile

import android.net.Uri
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
import androidx.compose.material.icons.filled.ChevronRight
import androidx.compose.material.icons.filled.Logout
import androidx.compose.material.icons.filled.Person
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
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
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import coil3.compose.AsyncImage
import com.teatt.pocket.core.ApiException
import com.teatt.pocket.core.Session
import com.teatt.pocket.core.SessionState
import com.teatt.pocket.data.ProfileApi
import com.teatt.pocket.ui.BrandPink
import com.teatt.pocket.ui.BrandViolet
import kotlinx.coroutines.launch

@Composable
fun ProfileScreen(
    state: SessionState,
    onLoggedOut: () -> Unit,
    modifier: Modifier = Modifier
) {
    var dialog by remember { mutableStateOf<ProfileDialog?>(null) }
    var message by remember { mutableStateOf<String?>(null) }
    var busy by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()
    val context = LocalContext.current

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
        contentPadding = PaddingValues(start = 20.dp, end = 20.dp, top = 0.dp, bottom = 110.dp),
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
