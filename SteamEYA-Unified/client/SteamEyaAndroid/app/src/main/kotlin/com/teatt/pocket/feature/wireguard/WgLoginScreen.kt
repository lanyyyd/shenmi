package com.teatt.pocket.feature.wireguard

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.Person
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
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
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.teatt.pocket.R
import com.teatt.pocket.ui.BrandPink
import com.teatt.pocket.ui.fieldColors
import kotlinx.coroutines.launch

/**
 * 「小工具」里的 WiFi 登录界面。
 *
 * 视觉与 App 主登录页保持同一套语言（渐变背景 / 圆角 14dp 输入框 + 聚焦粉边 /
 * BrandPink 胶囊主按钮 / 13sp 红色错误 / 12sp 底部说明），区别只有三处：
 *  1. 顶部是横版横幅（原来的「茶π」文字标志）
 *  2. 去掉「登录 / 注册」分段控件 —— 这套账号只有登录，账号由管理员开通
 *  3. 多一个左上角返回箭头（这是内嵌在全屏 Dialog 里的页，不是 App 首页）
 */
@Composable
fun WgLoginScreen(
    onBack: () -> Unit,
    onLoggedIn: () -> Unit,
    modifier: Modifier = Modifier
) {
    var username by remember { mutableStateOf("") }
    var password by remember { mutableStateOf("") }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()

    fun submit() {
        if (busy) return
        val user = username.trim()
        if (user.isEmpty()) {
            error = "请输入账号"
            return
        }
        if (password.isEmpty()) {
            error = "请输入密码"
            return
        }
        if (password.length < 6) {
            error = "密码至少 6 位"
            return
        }
        busy = true
        error = null
        scope.launch {
            try {
                val r = WgApi.login(user, password)
                if (r.success && !r.token.isNullOrEmpty()) {
                    WgSession.save(r.token, r.username.ifBlank { user })
                    onLoggedIn()
                } else {
                    error = r.error ?: "账号或密码错误，请联系管理员开通"
                }
            } catch (e: Exception) {
                error = e.message ?: "连接服务器失败"
            } finally {
                busy = false
            }
        }
    }

    Box(
        modifier
            .fillMaxSize()
            .background(
                Brush.verticalGradient(
                    listOf(
                        MaterialTheme.colorScheme.background,
                        BrandPink.copy(alpha = 0.10f),
                        MaterialTheme.colorScheme.background
                    )
                )
            )
    ) {
        // 左上角返回（内嵌页必须能退回「我的」）
        IconButton(
            onClick = onBack,
            modifier = Modifier
                .statusBarsPadding()
                .padding(8.dp)
        ) {
            Icon(Icons.AutoMirrored.Filled.ArrowBack, "返回")
        }

        Column(
            Modifier
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .statusBarsPadding()
                .navigationBarsPadding()
                .imePadding()
                .padding(horizontal = 26.dp),
            horizontalAlignment = Alignment.CenterHorizontally
        ) {
            Spacer(Modifier.height(64.dp))

            // 横幅 logo：宽度与下方输入框一致，高度按原图比例自动
            Image(
                painter = painterResource(R.drawable.wg_banner),
                contentDescription = "神秘WiFi",
                contentScale = ContentScale.Fit,
                modifier = Modifier
                    .fillMaxWidth()
                    .aspectRatio(1790f / 853f)
            )

            Spacer(Modifier.height(34.dp))

            androidx.compose.material3.OutlinedTextField(
                value = username,
                onValueChange = { username = it.take(20) },
                label = { Text("账号") },
                singleLine = true,
                leadingIcon = { Icon(Icons.Filled.Person, null, Modifier.size(18.dp)) },
                shape = RoundedCornerShape(14.dp),
                colors = fieldColors(),
                modifier = Modifier.fillMaxWidth()
            )

            Spacer(Modifier.height(12.dp))

            androidx.compose.material3.OutlinedTextField(
                value = password,
                onValueChange = { password = it },
                label = { Text("密码") },
                singleLine = true,
                visualTransformation = PasswordVisualTransformation(),
                keyboardOptions = KeyboardOptions(imeAction = ImeAction.Done),
                leadingIcon = { Icon(Icons.Filled.Lock, null, Modifier.size(18.dp)) },
                shape = RoundedCornerShape(14.dp),
                colors = fieldColors(),
                modifier = Modifier.fillMaxWidth()
            )

            AnimatedVisibility(visible = error != null) {
                Text(
                    text = error.orEmpty(),
                    color = MaterialTheme.colorScheme.error,
                    fontSize = 13.sp,
                    textAlign = TextAlign.Center,
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(top = 14.dp)
                )
            }

            Spacer(Modifier.height(26.dp))

            // 主按钮：与 App 登录页一致的纯色胶囊
            Box(
                Modifier
                    .fillMaxWidth()
                    .height(52.dp)
                    .clip(RoundedCornerShape(16.dp))
                    .background(BrandPink)
                    .clickable(enabled = !busy) { submit() },
                contentAlignment = Alignment.Center
            ) {
                if (busy) {
                    CircularProgressIndicator(
                        color = Color.White,
                        strokeWidth = 2.dp,
                        modifier = Modifier.size(20.dp)
                    )
                } else {
                    Text(
                        text = "登 录",
                        color = Color.White,
                        fontSize = 16.sp,
                        fontWeight = FontWeight.SemiBold
                    )
                }
            }

            Spacer(Modifier.height(18.dp))

            Text(
                text = "WiFi 账号由管理员开通，登录后即可一键连接",
                fontSize = 12.sp,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                textAlign = TextAlign.Center,
                modifier = Modifier.fillMaxWidth()
            )

            Spacer(Modifier.height(40.dp))
        }
    }
}
