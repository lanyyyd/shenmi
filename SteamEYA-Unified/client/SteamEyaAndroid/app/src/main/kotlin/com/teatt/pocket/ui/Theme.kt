package com.teatt.pocket.ui

import android.app.Activity
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.SideEffect
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalView
import androidx.core.view.WindowCompat

/** 与 Windows 端一致的品牌粉（AuroraPrimaryColor = #EC4899）。 */
val BrandPink = Color(0xFFEC4899)
val BrandViolet = Color(0xFF8B5CF6)

private val LightScheme = lightColorScheme(
    primary = BrandPink,
    onPrimary = Color.White,
    primaryContainer = Color(0xFFFCE7F3),
    onPrimaryContainer = Color(0xFF831843),
    secondary = BrandViolet,
    onSecondary = Color.White,
    background = Color(0xFFFDF8FB),
    onBackground = Color(0xFF1C1B1F),
    surface = Color.White,
    onSurface = Color(0xFF1C1B1F),
    surfaceVariant = Color(0xFFF4EDF2),
    onSurfaceVariant = Color(0xFF6B5A64),
    outline = Color(0xFFE3D6DE)
)

private val DarkScheme = darkColorScheme(
    primary = Color(0xFFF472B6),
    onPrimary = Color(0xFF3B0A25),
    primaryContainer = Color(0xFF5B1237),
    onPrimaryContainer = Color(0xFFFFD9E8),
    secondary = Color(0xFFA78BFA),
    onSecondary = Color(0xFF221046),
    background = Color(0xFF121014),
    onBackground = Color(0xFFEDE7EA),
    surface = Color(0xFF1B171C),
    onSurface = Color(0xFFEDE7EA),
    surfaceVariant = Color(0xFF2A242B),
    onSurfaceVariant = Color(0xFFB9A9B3),
    outline = Color(0xFF40363D)
)

@Composable
fun PocketTheme(
    darkTheme: Boolean = isSystemInDarkTheme(),
    content: @Composable () -> Unit
) {
    val colors = if (darkTheme) DarkScheme else LightScheme
    val view = LocalView.current
    val context = LocalContext.current

    SideEffect {
        // 让内容延伸到状态栏/导航栏下面，底部玻璃栏才有"浮在内容上"的观感。
        val window = (context as? Activity)?.window ?: return@SideEffect
        window.statusBarColor = Color.Transparent.toArgb()
        window.navigationBarColor = Color.Transparent.toArgb()
        WindowCompat.setDecorFitsSystemWindows(window, false)
        WindowCompat.getInsetsController(window, view).apply {
            isAppearanceLightStatusBars = !darkTheme
            isAppearanceLightNavigationBars = !darkTheme
        }
    }

    MaterialTheme(colorScheme = colors, typography = Typography(), content = content)
}
