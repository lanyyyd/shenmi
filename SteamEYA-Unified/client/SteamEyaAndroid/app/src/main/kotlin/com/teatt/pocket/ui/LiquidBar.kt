package com.teatt.pocket.ui

import androidx.compose.animation.core.animateDpAsState
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.spring
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.scale
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.kyant.backdrop.Backdrop
import com.kyant.backdrop.drawBackdrop
import com.kyant.backdrop.effects.blur
import com.kyant.backdrop.effects.lens
import com.kyant.backdrop.effects.vibrancy
import com.kyant.shapes.Capsule

data class TabSpec(
    val icon: ImageVector,
    val label: String
)

/**
 * 液态玻璃底部功能栏。
 *
 * 玻璃本体来自 Kyant0/AndroidLiquidGlass（io.github.kyant0:backdrop），
 * 这里只负责排版与选中态：背景模糊 + 色彩提纯（vibrancy）+ 边缘折射（lens），
 * 折射与着色都由库的 drawBackdrop 完成，没有自己实现玻璃算法。
 */
@Composable
fun LiquidBottomBar(
    backdrop: Backdrop,
    selectedIndex: Int,
    tabs: List<TabSpec>,
    onSelect: (Int) -> Unit,
    modifier: Modifier = Modifier
) {
    val dark = MaterialTheme.colorScheme.background.luminanceIsDark()
    val tint = if (dark) Color.Black.copy(alpha = 0.42f) else Color.White.copy(alpha = 0.46f)
    val borderColor = if (dark) Color.White.copy(alpha = 0.12f) else Color.White.copy(alpha = 0.55f)
    val activeColor = MaterialTheme.colorScheme.primary
    val idleColor = if (dark) Color(0xFFB9A9B3) else Color(0xFF7A6773)

    Box(
        modifier
            .padding(horizontal = 18.dp)
            .navigationBarsPadding()
            .padding(bottom = BarBottomGap)
            .fillMaxWidth()
            .height(BarCoreHeight)
            .drawBackdrop(
                backdrop = backdrop,
                shape = { Capsule() },
                effects = {
                    vibrancy()
                    blur(12.dp.toPx())
                    lens(22.dp.toPx(), 26.dp.toPx())
                },
                onDrawSurface = { drawRect(tint) }
            )
            .border(1.dp, borderColor, Capsule())
    ) {
        BoxWithConstraints(Modifier.fillMaxSize()) {
            val tabWidth = maxWidth / tabs.size
            val indicator by animateDpAsState(
                targetValue = tabWidth * selectedIndex,
                animationSpec = spring(dampingRatio = 0.72f, stiffness = 380f),
                label = "indicator"
            )

            // 选中胶囊：半透明高光 + 细边，叠在玻璃之上形成"凹槽"观感
            Box(
                Modifier
                    .offset(x = indicator)
                    .width(tabWidth)
                    .fillMaxHeight()
                    .padding(7.dp)
            ) {
                Spacer(
                    Modifier
                        .fillMaxSize()
                        .background(
                            if (dark) Color.White.copy(alpha = 0.10f) else Color.White.copy(alpha = 0.62f),
                            Capsule()
                        )
                        .border(
                            1.dp,
                            if (dark) Color.White.copy(alpha = 0.10f) else Color.White.copy(alpha = 0.9f),
                            Capsule()
                        )
                )
            }

            Row(Modifier.fillMaxSize(), verticalAlignment = Alignment.CenterVertically) {
                tabs.forEachIndexed { index, tab ->
                    val active = index == selectedIndex
                    val iconScale by animateFloatAsState(
                        targetValue = if (active) 1f else 0.94f,
                        animationSpec = spring(dampingRatio = 0.6f, stiffness = 700f),
                        label = "iconScale"
                    )
                    val labelAlpha by animateFloatAsState(
                        targetValue = if (active) 1f else 0.72f,
                        label = "labelAlpha"
                    )

                    Column(
                        Modifier
                            .width(tabWidth)
                            .fillMaxHeight()
                            .clickable(
                                interactionSource = remember { MutableInteractionSource() },
                                indication = null
                            ) { onSelect(index) },
                        horizontalAlignment = Alignment.CenterHorizontally,
                        verticalArrangement = Arrangement.Center
                    ) {
                        Icon(
                            imageVector = tab.icon,
                            contentDescription = tab.label,
                            tint = if (active) activeColor else idleColor,
                            modifier = Modifier
                                .height(23.dp)
                                .width(23.dp)
                                .scale(iconScale)
                        )
                        Spacer(Modifier.height(3.dp))
                        Text(
                            text = tab.label,
                            fontSize = 10.sp,
                            maxLines = 1,
                            modifier = Modifier.alpha(labelAlpha),
                            color = if (active) activeColor else idleColor,
                            fontWeight = if (active) FontWeight.SemiBold else FontWeight.Normal
                        )
                    }
                }
            }
        }
    }
}

/** 极简亮度判断，避免为了一个条件引入 ColorUtils。 */
internal fun Color.luminanceIsDark(): Boolean {
    val r = red
    val g = green
    val b = blue
    return (0.299f * r + 0.587f * g + 0.114f * b) < 0.5f
}

/** 未选中态的小圆点，暂时留作后续未读数角标用。 */
@Composable
internal fun Dot(color: Color) {
    Spacer(
        Modifier
            .height(6.dp)
            .width(6.dp)
            .background(color, CircleShape)
            .alpha(0.9f)
    )
}
