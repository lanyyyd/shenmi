package com.teatt.pocket.feature.eat

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.CubicBezierEasing
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Favorite
import androidx.compose.material.icons.filled.FavoriteBorder
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.drawscope.rotate
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.drawText
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.rememberTextMeasurer
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import coil3.compose.AsyncImage
import com.teatt.pocket.core.Session
import com.teatt.pocket.data.EatApi
import com.teatt.pocket.data.EatDish
import com.teatt.pocket.data.EatImageRef
import com.teatt.pocket.data.EatReview
import com.teatt.pocket.data.EatStore
import com.teatt.pocket.data.EatSummary
import com.teatt.pocket.data.EatTaskState
import com.teatt.pocket.data.EatUser
import com.teatt.pocket.ui.BrandPink
import com.teatt.pocket.ui.LocalBarInset
import kotlinx.coroutines.launch
import java.util.Locale

// ---------------------------------------------------------------- 转盘

private val WHEEL_COLORS = listOf(
    Color(0xFFFFB020), Color(0xFFFF8A00), Color(0xFFFFC53D),
    Color(0xFFFFD97A), Color(0xFFFF9E2C), Color(0xFFFFB84D)
)

@Composable
fun WheelPage(token: String, topInset: Dp, refreshTick: Int) {
    var dishes by remember { mutableStateOf<List<EatDish>>(emptyList()) }
    var state by remember { mutableStateOf<EatTaskState?>(null) }
    var loading by remember { mutableStateOf(true) }
    var spinning by remember { mutableStateOf(false) }
    var message by remember { mutableStateOf<String?>(null) }
    val angle = remember { Animatable(0f) }
    val measurer = rememberTextMeasurer()
    val scope = rememberCoroutineScope()
    val bottomInset = LocalBarInset.current + 20.dp

    suspend fun load() {
        loading = true
        runCatching { dishes = EatApi.dishes(token) }
        runCatching { state = EatApi.task(token) }
        loading = false
    }

    LaunchedEffect(token, refreshTick) { load() }

    val task = state?.task

    suspend fun doSpin() {
        if (spinning) return
        val n = dishes.size
        if (n == 0) {
            message = "还没有菜品可以抽，先去「我的」加一道吧"
            return
        }
        spinning = true
        val result = runCatching { EatApi.spin(token) }.getOrNull()
        val picked = result?.task
        if (result == null || picked == null) {
            spinning = false
            message = "抽不出来，稍后再试"
            return
        }
        // 结果先扣在手里：等转盘停稳再落到 state，否则还没转完答案就蹦出来了
        var idx = dishes.indexOfFirst { it.id == picked.dishId }
        if (idx < 0) idx = 0
        val sweep = 360f / n
        val base = -(idx * sweep + sweep / 2f)
        val current = angle.value
        val currentMod = ((current % 360f) + 360f) % 360f
        val delta = ((base - currentMod) % 360f + 360f) % 360f + 360f * 5
        angle.animateTo(
            targetValue = current + delta,
            animationSpec = tween(3600, easing = CubicBezierEasing(0.16f, 0.9f, 0.18f, 1f))
        )
        state = result
        spinning = false
    }

    Column(
        Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(start = 20.dp, end = 20.dp, top = topInset + 6.dp, bottom = bottomInset)
    ) {
        Text(
            "全局大转盘",
            fontSize = 12.sp,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.padding(bottom = 8.dp)
        )

        if (loading && dishes.isEmpty()) {
            Box(Modifier.fillMaxWidth().height(240.dp), contentAlignment = Alignment.Center) {
                CircularProgressIndicator(color = BrandPink, strokeWidth = 2.dp)
            }
        } else if (dishes.isEmpty()) {
            Box(Modifier.fillMaxWidth().height(240.dp), contentAlignment = Alignment.Center) {
                Text("还没有菜品，先去「我的」页添加吧～", fontSize = 13.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        } else {
            BoxWithWheel(
                dishes = dishes,
                angle = angle.value,
                measurer = measurer,
                modifier = Modifier.fillMaxWidth().aspectRatio(1f)
            )
        }

        Spacer(Modifier.height(12.dp))

        // 当前结果
        Surface(shape = RoundedCornerShape(18.dp), color = MaterialTheme.colorScheme.surface, modifier = Modifier.fillMaxWidth()) {
            Column(Modifier.padding(14.dp)) {
                if (spinning) {
                    Text("转盘转动中…", fontSize = 13.sp, fontWeight = FontWeight.Medium)
                    Spacer(Modifier.height(4.dp))
                    Text(
                        "停稳后揭晓今天吃什么",
                        fontSize = 11.sp,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                } else if (task == null) {
                    Text("今天还没抽出结果，点下面「抽」试试", fontSize = 13.sp)
                    Spacer(Modifier.height(4.dp))
                    Text(
                        "抽出来的结果全站共享，大家看到的是同一道菜",
                        fontSize = 11.sp,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                } else {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        EatCover(
                            url = task.dishImage,
                            modifier = Modifier.size(56.dp).clip(RoundedCornerShape(12.dp))
                        )
                        Spacer(Modifier.width(10.dp))
                        Column(Modifier.weight(1f)) {
                            Text(task.dishName.ifBlank { "这份神秘料理" }, fontSize = 16.sp, fontWeight = FontWeight.Bold)
                            Spacer(Modifier.height(2.dp))
                            Text(
                                "${task.storeName} · ${priceText(task.price)}",
                                fontSize = 11.sp,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis
                            )
                        }
                        Text(
                            if (task.global) "🌍 共享" else "🙋 仅你",
                            fontSize = 10.sp,
                            color = BrandPink
                        )
                    }
                }
            }
        }

        Spacer(Modifier.height(12.dp))

        PrimaryButton(text = if (spinning) "转动中…" else "🎯 抽！", enabled = !spinning && dishes.isNotEmpty()) {
            scope.launch { doSpin() }
        }

        Spacer(Modifier.height(10.dp))

        Row(
            Modifier
                .fillMaxWidth()
                .height(46.dp)
                .clip(RoundedCornerShape(15.dp))
                .background(MaterialTheme.colorScheme.surfaceVariant.copy(alpha = .5f))
                .clickable(enabled = !spinning) {
                    scope.launch {
                        runCatching { EatApi.eatOther(token); load() }
                            .onFailure { message = it.message }
                    }
                },
            horizontalArrangement = Arrangement.Center,
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text("🍽️ 我要吃别的", fontSize = 14.sp, color = BrandPink, fontWeight = FontWeight.Medium)
        }

        Spacer(Modifier.height(10.dp))
        Text(
            "「我要吃别的」只会重置你自己的结果，之后抽取只对你生效，不影响别人看到的那道菜。",
            fontSize = 11.sp,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
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

/** 画布转盘：扇区用菜品名，指针在正上方，中心是「抽」。 */
@Composable
private fun BoxWithWheel(
    dishes: List<EatDish>,
    angle: Float,
    measurer: androidx.compose.ui.text.TextMeasurer,
    modifier: Modifier = Modifier
) {
    val labelStyle = TextStyle(fontSize = 10.sp, color = Color.White, fontWeight = FontWeight.SemiBold)
    val centerStyle = TextStyle(fontSize = 20.sp, color = Color(0xFFE06A00), fontWeight = FontWeight.Bold)

    Canvas(modifier) {
        val center = Offset(size.width / 2f, size.height / 2f)
        val radius = size.minDimension / 2f - 6.dp.toPx()
        val n = dishes.size
        val sweep = 360f / n

        rotate(angle, center) {
            dishes.forEachIndexed { i, dish ->
                drawArc(
                    color = WHEEL_COLORS[i % WHEEL_COLORS.size],
                    startAngle = i * sweep - 90f,
                    sweepAngle = sweep,
                    useCenter = true,
                    topLeft = Offset(center.x - radius, center.y - radius),
                    size = Size(radius * 2, radius * 2)
                )
                drawArc(
                    color = Color.White,
                    startAngle = i * sweep - 90f,
                    sweepAngle = sweep,
                    useCenter = true,
                    topLeft = Offset(center.x - radius, center.y - radius),
                    size = Size(radius * 2, radius * 2),
                    style = Stroke(width = 2.dp.toPx())
                )
            }

            dishes.forEachIndexed { i, dish ->
                val mid = i * sweep - 90f + sweep / 2f
                rotate(mid, center) {
                    val raw = if (dish.name.length > 6) dish.name.take(6) + "…" else dish.name
                    val layout = measurer.measure(raw, labelStyle)
                    drawText(
                        textLayoutResult = layout,
                        topLeft = Offset(
                            center.x + radius - 22.dp.toPx() - layout.size.width,
                            center.y - layout.size.height / 2f
                        )
                    )
                }
            }
        }

        // 中心按钮
        drawCircle(Color.White, radius = radius * 0.26f, center = center)
        drawCircle(Color(0xFFFF8A00), radius = radius * 0.26f, center = center, style = Stroke(width = 5.dp.toPx()))
        val label = measurer.measure("抽", centerStyle)
        drawText(
            textLayoutResult = label,
            topLeft = Offset(center.x - label.size.width / 2f, center.y - label.size.height / 2f)
        )

        // 顶部指针
        val pointer = Path().apply {
            moveTo(center.x - 11.dp.toPx(), center.y - radius - 2.dp.toPx())
            lineTo(center.x + 11.dp.toPx(), center.y - radius - 2.dp.toPx())
            lineTo(center.x, center.y - radius + 20.dp.toPx())
            close()
        }
        drawPath(pointer, Color(0xFFE53935))
    }
}

// ---------------------------------------------------------------- 评价流

@Composable
fun FeedPage(token: String, topInset: Dp, refreshTick: Int) {
    var reviews by remember { mutableStateOf<List<EatReview>>(emptyList()) }
    var loading by remember { mutableStateOf(true) }
    var hasMore by remember { mutableStateOf(false) }
    var offset by remember { mutableStateOf(0) }
    var showEditor by remember { mutableStateOf(false) }
    var sameTarget by remember { mutableStateOf<EatReview?>(null) }
    var viewer by remember { mutableStateOf<String?>(null) }
    var message by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()
    val bottomInset = LocalBarInset.current + 20.dp

    suspend fun load(reset: Boolean) {
        if (reset) {
            loading = true
            offset = 0
        }
        runCatching { EatApi.reviews(token, limit = 20, offset = offset) }
            .onSuccess { dto ->
                reviews = if (reset) dto.reviews else reviews + dto.reviews
                offset = offset + dto.reviews.size
                hasMore = dto.hasMore
            }
            .onFailure { message = it.message ?: "加载失败" }
        loading = false
    }

    LaunchedEffect(token, refreshTick) { load(true) }

    Box(Modifier.fillMaxSize()) {
        when {
            loading && reviews.isEmpty() -> CenterLoading()
            reviews.isEmpty() -> CenterTip("还没有人发评价，来当第一个吧") { showEditor = true }
            else -> LazyColumn(
                Modifier.fillMaxSize(),
                contentPadding = PaddingValues(start = 20.dp, end = 20.dp, top = topInset + 6.dp, bottom = bottomInset),
                verticalArrangement = Arrangement.spacedBy(12.dp)
            ) {
                item {
                    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                        Text("大家的评价", fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        Spacer(Modifier.weight(1f))
                        TextButton(onClick = { showEditor = true }) { Text("✍️ 发评价", fontSize = 13.sp) }
                    }
                }
                items(reviews, key = { it.id }) { review ->
                    ReviewCard(
                        review = review,
                        onLike = {
                            scope.launch {
                                runCatching { EatApi.like(token, review.id) }
                                    .onSuccess { liked ->
                                        reviews = reviews.map {
                                            if (it.id == review.id) {
                                                it.copy(liked = liked, likeCount = (it.likeCount + if (liked) 1 else -1).coerceAtLeast(0))
                                            } else it
                                        }
                                    }
                                    .onFailure { message = it.message }
                            }
                        },
                        onSame = { sameTarget = review },
                        onImage = { viewer = it }
                    )
                }
                if (hasMore) {
                    item {
                        TextButton(onClick = { scope.launch { load(false) } }, modifier = Modifier.fillMaxWidth()) {
                            Text("加载更多", fontSize = 13.sp)
                        }
                    }
                }
            }
        }
    }

    if (showEditor) {
        ReviewEditor(
            token = token,
            parent = null,
            onDismiss = { showEditor = false },
            onDone = { showEditor = false; scope.launch { load(true) } }
        )
    }

    sameTarget?.let { parent ->
        ReviewEditor(
            token = token,
            parent = parent,
            onDismiss = { sameTarget = null },
            onDone = { sameTarget = null; scope.launch { load(true) } }
        )
    }

    viewer?.let { url -> FullImageDialog(url) { viewer = null } }

    message?.let {
        AlertDialog(
            onDismissRequest = { message = null },
            title = { Text("提示") },
            text = { Text(it) },
            confirmButton = { TextButton(onClick = { message = null }) { Text("好") } }
        )
    }
}

@Composable
private fun ReviewCard(
    review: EatReview,
    onLike: () -> Unit,
    onSame: () -> Unit,
    onImage: (String) -> Unit
) {
    Surface(shape = RoundedCornerShape(20.dp), color = MaterialTheme.colorScheme.surface, modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(14.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Avatar(review.user.avatar, review.user.displayName)
                Spacer(Modifier.width(10.dp))
                Column(Modifier.weight(1f)) {
                    Text(review.user.displayName.ifBlank { "匿名" }, fontSize = 14.sp, fontWeight = FontWeight.SemiBold)
                    Text(eatTimeText(review.createdAt), fontSize = 10.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                val rating = review.rating
                if (rating != null) {
                    Text("★".repeat(rating), fontSize = 12.sp, color = Color(0xFFF59E0B))
                }
            }

            Spacer(Modifier.height(9.dp))

            Text(
                buildString {
                    append(review.storeName.ifBlank { "某家店" })
                    if (review.dishName.isNotBlank()) append(" · ").append(review.dishName)
                    if (review.price > 0) append("  ").append(priceText(review.price))
                },
                fontSize = 12.sp,
                color = BrandPink,
                fontWeight = FontWeight.Medium,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis
            )

            if (review.comment.isNotBlank()) {
                Spacer(Modifier.height(6.dp))
                Text(review.comment, fontSize = 14.sp, lineHeight = 20.sp)
            }

            if (review.images.isNotEmpty()) {
                Spacer(Modifier.height(10.dp))
                ImageGrid(review.images, onImage)
            }

            if (review.guests.isNotEmpty()) {
                Spacer(Modifier.height(8.dp))
                Text(
                    "🍽️ 一起吃：" + review.guests.joinToString("、") { it.displayName },
                    fontSize = 11.sp,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }

            Spacer(Modifier.height(6.dp))

            Row(verticalAlignment = Alignment.CenterVertically) {
                Row(
                    Modifier.clip(RoundedCornerShape(10.dp)).clickable(onClick = onLike).padding(6.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Icon(
                        if (review.liked) Icons.Filled.Favorite else Icons.Filled.FavoriteBorder,
                        contentDescription = "点赞",
                        tint = if (review.liked) BrandPink else MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.size(15.dp)
                    )
                    Spacer(Modifier.width(4.dp))
                    Text(
                        if (review.likeCount > 0) review.likeCount.toString() else "赞",
                        fontSize = 12.sp,
                        color = if (review.liked) BrandPink else MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }

                Spacer(Modifier.width(6.dp))

                Row(
                    Modifier.clip(RoundedCornerShape(10.dp)).clickable(onClick = onSame).padding(6.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Text("💬 同评", fontSize = 12.sp, color = BrandPink)
                    if (review.sameCount > 0) {
                        Spacer(Modifier.width(4.dp))
                        Text(review.sameCount.toString(), fontSize = 12.sp, color = BrandPink)
                    }
                }
            }

            if (review.sames.isNotEmpty()) {
                Spacer(Modifier.height(6.dp))
                Column(
                    Modifier
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(12.dp))
                        .background(MaterialTheme.colorScheme.surfaceVariant.copy(alpha = .35f))
                        .padding(10.dp)
                ) {
                    review.sames.take(3).forEach { same ->
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Text("${same.user.displayName.ifBlank { "匿名" }}：", fontSize = 12.sp, fontWeight = FontWeight.Medium)
                            Text(
                                same.comment.ifBlank { "也吃了这道" },
                                fontSize = 12.sp,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                        }
                    }
                    if (review.sameCount > 3) {
                        Text("还有 ${review.sameCount - 3} 条跟评", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
            }
        }
    }
}

@Composable
private fun ImageGrid(images: List<String>, onImage: (String) -> Unit) {
    val rows = images.chunked(3)
    Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
        rows.forEach { row ->
            Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                row.forEach { url ->
                    val real = EatApi.imageUrl(url)
                    Box(
                        Modifier
                            .weight(1f)
                            .aspectRatio(1f)
                            .clip(RoundedCornerShape(10.dp))
                            .background(MaterialTheme.colorScheme.surfaceVariant)
                            .clickable { real?.let(onImage) }
                    ) {
                        if (!real.isNullOrBlank()) {
                            AsyncImage(model = real, contentDescription = null, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize())
                        }
                    }
                }
                repeat(3 - row.size) { Spacer(Modifier.weight(1f)) }
            }
        }
    }
}

@Composable
private fun FullImageDialog(url: String, onDismiss: () -> Unit) {
    Dialog(
        onDismissRequest = onDismiss,
        properties = DialogProperties(usePlatformDefaultWidth = false, decorFitsSystemWindows = false)
    ) {
        Box(
            Modifier
                .fillMaxSize()
                .background(Color.Black.copy(alpha = .94f))
                .clickable(onClick = onDismiss),
            contentAlignment = Alignment.Center
        ) {
            AsyncImage(
                model = url,
                contentDescription = null,
                contentScale = ContentScale.Fit,
                modifier = Modifier.fillMaxWidth().padding(12.dp)
            )
            TextButton(onClick = onDismiss, modifier = Modifier.align(Alignment.TopEnd).padding(top = 40.dp)) {
                Icon(Icons.Filled.Close, contentDescription = "关闭", tint = Color.White)
            }
        }
    }
}

// ---------------------------------------------------------------- 我的

@Composable
fun MinePage(
    token: String,
    stores: List<EatStore>,
    topInset: Dp,
    refreshTick: Int,
    onNeedReload: () -> Unit
) {
    var period by remember { mutableStateOf("month") }
    var summary by remember { mutableStateOf<EatSummary?>(null) }
    var wall by remember { mutableStateOf<List<EatImageRef>>(emptyList()) }
    var loading by remember { mutableStateOf(true) }
    var viewer by remember { mutableStateOf<String?>(null) }
    var showAddStore by remember { mutableStateOf(false) }
    var showAddDish by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()
    val bottomInset = LocalBarInset.current + 20.dp

    LaunchedEffect(token, period, refreshTick) {
        loading = true
        runCatching { summary = EatApi.summary(token, period) }
        runCatching { wall = EatApi.wall(token) }
        loading = false
    }

    LazyColumn(
        Modifier.fillMaxSize(),
        contentPadding = PaddingValues(start = 20.dp, end = 20.dp, top = topInset + 6.dp, bottom = bottomInset),
        verticalArrangement = Arrangement.spacedBy(10.dp)
    ) {
        item {
            Row(
                Modifier
                    .clip(RoundedCornerShape(12.dp))
                    .background(MaterialTheme.colorScheme.surfaceVariant.copy(alpha = .5f))
                    .padding(3.dp)
            ) {
                listOf("month" to "本月", "year" to "今年").forEach { (key, label) ->
                    val on = period == key
                    Box(
                        Modifier
                            .clip(RoundedCornerShape(10.dp))
                            .background(if (on) BrandPink else Color.Transparent)
                            .clickable { period = key }
                            .padding(horizontal = 18.dp, vertical = 7.dp)
                    ) {
                        Text(label, fontSize = 12.sp, color = if (on) Color.White else MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
            }
        }

        item {
            Surface(shape = RoundedCornerShape(20.dp), color = MaterialTheme.colorScheme.surface) {
                Row(Modifier.fillMaxWidth().padding(18.dp), horizontalArrangement = Arrangement.SpaceEvenly) {
                    StatCell(summary?.count?.toString() ?: "0", "吃了（次）", Modifier.weight(1f))
                    StatCell("¥" + String.format(Locale.US, "%.0f", summary?.spend ?: 0.0), "花掉的银子", Modifier.weight(1f))
                    StatCell(summary?.topDish?.ifBlank { "—" } ?: "—", "最常吃的菜", Modifier.weight(1f))
                }
            }
        }

        item {
            Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                Box(Modifier.weight(1f)) { OutlinedAddButton("＋ 添加店铺") { showAddStore = true } }
                Box(Modifier.weight(1f)) { OutlinedAddButton("＋ 添加菜品") { showAddDish = true } }
            }
        }

        item {
            Text(
                "图片墙",
                fontSize = 12.sp,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(top = 6.dp)
            )
        }

        if (wall.isEmpty()) {
            item {
                Text(
                    if (loading) "加载中…" else "还没有图片，发评价时带上图片就会出现在这里",
                    fontSize = 12.sp,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
        } else {
            items(wall.chunked(3)) { row ->
                Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                    row.forEach { item ->
                        val real = EatApi.imageUrl(item.image)
                        Box(
                            Modifier
                                .weight(1f)
                                .aspectRatio(1f)
                                .clip(RoundedCornerShape(10.dp))
                                .background(MaterialTheme.colorScheme.surfaceVariant)
                                .clickable { real?.let { viewer = it } }
                        ) {
                            if (!real.isNullOrBlank()) {
                                AsyncImage(model = real, contentDescription = null, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize())
                            }
                        }
                    }
                    repeat(3 - row.size) { Spacer(Modifier.weight(1f)) }
                }
            }
        }
    }

    if (showAddStore) {
        AddStoreDialog(
            token = token,
            onDismiss = { showAddStore = false },
            onDone = { showAddStore = false; onNeedReload() }
        )
    }
    if (showAddDish) {
        AddDishDialog(
            token = token,
            stores = stores,
            onDismiss = { showAddDish = false },
            onDone = { showAddDish = false; onNeedReload() }
        )
    }
    viewer?.let { FullImageDialog(it) { viewer = null } }
}

@Composable
private fun StatCell(value: String, label: String, modifier: Modifier = Modifier) {
    Column(modifier, horizontalAlignment = Alignment.CenterHorizontally) {
        Text(value, fontSize = 17.sp, fontWeight = FontWeight.Bold, maxLines = 1, overflow = TextOverflow.Ellipsis)
        Spacer(Modifier.height(3.dp))
        Text(label, fontSize = 10.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

// ---------------------------------------------------------------- 弹窗

@Composable
fun DishDetailDialog(dish: EatDish, onDismiss: () -> Unit, onChoose: () -> Unit) {
    Dialog(onDismissRequest = onDismiss) {
        Surface(shape = RoundedCornerShape(22.dp), color = MaterialTheme.colorScheme.surface) {
            Column(Modifier.padding(16.dp)) {
                EatCover(
                    url = dish.image,
                    modifier = Modifier.fillMaxWidth().aspectRatio(4f / 3f).clip(RoundedCornerShape(16.dp))
                )
                Spacer(Modifier.height(12.dp))
                Text(dish.name, fontSize = 17.sp, fontWeight = FontWeight.Bold)
                Spacer(Modifier.height(4.dp))
                Text(
                    buildString {
                        if (dish.storeName.isNotBlank()) append(dish.storeName).append(" · ")
                        append(priceText(dish.price))
                        if (dish.eatCount > 0) append(" · 吃过 ").append(dish.eatCount)
                    },
                    fontSize = 12.sp,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
                Spacer(Modifier.height(14.dp))
                PrimaryButton("🍽️ 就吃这个了！", onClick = onChoose)
                Spacer(Modifier.height(8.dp))
                TextButton(onClick = onDismiss, modifier = Modifier.fillMaxWidth()) { Text("关闭") }
            }
        }
    }
}

@Composable
fun AddStoreDialog(token: String, onDismiss: () -> Unit, onDone: () -> Unit) {
    var name by remember { mutableStateOf("") }
    var location by remember { mutableStateOf("") }
    var image by remember { mutableStateOf("") }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("添加店铺") },
        text = {
            Column(Modifier.heightIn(max = 380.dp).verticalScroll(rememberScrollState())) {
                OutlinedTextField(
                    value = name,
                    onValueChange = { name = it },
                    label = { Text("店铺名称") },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth()
                )
                Spacer(Modifier.height(8.dp))
                OutlinedTextField(
                    value = location,
                    onValueChange = { location = it },
                    label = { Text("位置（可空）") },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth()
                )
                Spacer(Modifier.height(10.dp))
                ImagePickerRow(
                    token = token,
                    images = listOf(image).filter { it.isNotBlank() },
                    onPick = { image = it },
                    maxCount = 1
                )
                error?.let {
                    Spacer(Modifier.height(6.dp))
                    Text(it, fontSize = 12.sp, color = MaterialTheme.colorScheme.error)
                }
            }
        },
        confirmButton = {
            TextButton(
                enabled = name.isNotBlank() && !busy,
                onClick = {
                    busy = true
                    scope.launch {
                        runCatching { EatApi.addStore(token, name.trim(), location.trim(), image) }
                            .onSuccess { onDone() }
                            .onFailure { error = it.message ?: "保存失败" }
                        busy = false
                    }
                }
            ) { Text(if (busy) "保存中…" else "保存") }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("取消") } }
    )
}

@Composable
fun AddDishDialog(token: String, stores: List<EatStore>, onDismiss: () -> Unit, onDone: () -> Unit) {
    var storeId by remember { mutableStateOf(stores.firstOrNull()?.id ?: 0) }
    var name by remember { mutableStateOf("") }
    var price by remember { mutableStateOf("") }
    var image by remember { mutableStateOf("") }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    var picker by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()
    val current = stores.firstOrNull { it.id == storeId }

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("添加菜品") },
        text = {
            Column(Modifier.heightIn(max = 420.dp).verticalScroll(rememberScrollState())) {
                Row(
                    Modifier
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(12.dp))
                        .background(MaterialTheme.colorScheme.surfaceVariant.copy(alpha = .5f))
                        .clickable { picker = true }
                        .padding(12.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Text(
                        current?.name ?: "选择店铺",
                        fontSize = 13.sp,
                        modifier = Modifier.weight(1f),
                        color = if (current == null) MaterialTheme.colorScheme.onSurfaceVariant else MaterialTheme.colorScheme.onSurface
                    )
                    Text("▾", fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                Spacer(Modifier.height(8.dp))
                OutlinedTextField(
                    value = name,
                    onValueChange = { name = it },
                    label = { Text("菜品名称") },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth()
                )
                Spacer(Modifier.height(8.dp))
                OutlinedTextField(
                    value = price,
                    onValueChange = { price = it.filter { c -> c.isDigit() || c == '.' } },
                    label = { Text("价格（可空）") },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal),
                    modifier = Modifier.fillMaxWidth()
                )
                Spacer(Modifier.height(10.dp))
                ImagePickerRow(
                    token = token,
                    images = listOf(image).filter { it.isNotBlank() },
                    onPick = { image = it },
                    maxCount = 1
                )
                error?.let {
                    Spacer(Modifier.height(6.dp))
                    Text(it, fontSize = 12.sp, color = MaterialTheme.colorScheme.error)
                }
            }
        },
        confirmButton = {
            TextButton(
                enabled = name.isNotBlank() && storeId != 0 && !busy,
                onClick = {
                    busy = true
                    scope.launch {
                        runCatching {
                            EatApi.addDish(token, storeId, name.trim(), price.toDoubleOrNull() ?: 0.0, image)
                        }.onSuccess { onDone() }.onFailure { error = it.message ?: "保存失败" }
                        busy = false
                    }
                }
            ) { Text(if (busy) "保存中…" else "保存") }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("取消") } }
    )

    if (picker) {
        PickerDialog(
            title = "选择店铺",
            options = stores.map { it.id to it.name },
            onDismiss = { picker = false }
        ) { id -> storeId = id; picker = false }
    }
}

@Composable
private fun PickerDialog(
    title: String,
    options: List<Pair<Int, String>>,
    onDismiss: () -> Unit,
    onPick: (Int) -> Unit
) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(title) },
        text = {
            Column(Modifier.heightIn(max = 340.dp).verticalScroll(rememberScrollState())) {
                if (options.isEmpty()) {
                    Text("列表为空", fontSize = 13.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                options.forEach { (id, name) ->
                    Row(
                        Modifier
                            .fillMaxWidth()
                            .clip(RoundedCornerShape(10.dp))
                            .clickable { onPick(id) }
                            .padding(vertical = 11.dp, horizontal = 6.dp)
                    ) {
                        Text(name, fontSize = 14.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    }
                }
            }
        },
        confirmButton = { TextButton(onClick = onDismiss) { Text("取消") } }
    )
}

// ---------------------------------------------------------------- 发评价 / 同评

/**
 * 评价编辑器，与网页端一致：
 * - parent == null：主评，可以选店铺/菜品、打分、写评语、传图、勾「一起吃」的小伙伴
 * - parent != null：同评，目标锁定在父评价的店铺/菜品，隐藏「一起吃」
 */
@Composable
fun ReviewEditor(
    token: String,
    parent: EatReview?,
    onDismiss: () -> Unit,
    onDone: () -> Unit
) {
    val isSame = parent != null
    var stores by remember { mutableStateOf<List<EatStore>>(emptyList()) }
    var dishes by remember { mutableStateOf<List<EatDish>>(emptyList()) }
    var users by remember { mutableStateOf<List<EatUser>>(emptyList()) }
    var storeId by remember { mutableStateOf(parent?.storeId ?: 0) }
    var dishId by remember { mutableStateOf(parent?.dishId ?: 0) }
    var rating by remember { mutableStateOf(0) }
    var comment by remember { mutableStateOf("") }
    val guests = remember { mutableStateListOf<Int>() }
    val images = remember { mutableStateListOf<String>() }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    var pickStore by remember { mutableStateOf(false) }
    var pickDish by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()
    val myId = Session.state.value.userId

    LaunchedEffect(token) {
        if (!isSame) {
            runCatching { stores = EatApi.stores(token) }
            runCatching { dishes = EatApi.dishes(token) }
            runCatching { users = EatApi.users(token) }
        }
    }

    val storeName = stores.firstOrNull { it.id == storeId }?.name ?: parent?.storeName.orEmpty()
    val dishName = dishes.firstOrNull { it.id == dishId }?.name ?: parent?.dishName.orEmpty()

    Dialog(onDismissRequest = onDismiss) {
        Surface(shape = RoundedCornerShape(24.dp), color = MaterialTheme.colorScheme.surface) {
            Column(
                Modifier
                    .fillMaxWidth()
                    .padding(20.dp)
                    .heightIn(max = 580.dp)
                    .verticalScroll(rememberScrollState())
            ) {
                Text(if (isSame) "💬 同评" else "✍️ 发评价", fontSize = 18.sp, fontWeight = FontWeight.Bold)
                Spacer(Modifier.height(4.dp))
                Text(
                    if (isSame) "同意这道菜？说说你的看法～打分、评语、图片都可留空"
                    else "说说这一顿吃得怎么样～打分、评语、图片都可以留空",
                    fontSize = 12.sp,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
                Spacer(Modifier.height(14.dp))

                if (isSame) {
                    // 同评锁定目标，不给改
                    Column(
                        Modifier
                            .fillMaxWidth()
                            .clip(RoundedCornerShape(12.dp))
                            .background(MaterialTheme.colorScheme.surfaceVariant.copy(alpha = .45f))
                            .padding(12.dp)
                    ) {
                        Text(dishName.ifBlank { "这道菜" }, fontSize = 14.sp, fontWeight = FontWeight.SemiBold)
                        Spacer(Modifier.height(3.dp))
                        Text(
                            "🍽️ ${storeName.ifBlank { "—" }}" + if ((parent?.price ?: 0.0) > 0) " · ${priceText(parent!!.price)}" else "",
                            fontSize = 11.sp,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                } else {
                    PickerRow(if (storeName.isBlank()) "选择店铺" else storeName) { pickStore = true }
                    Spacer(Modifier.height(8.dp))
                    PickerRow(if (dishName.isBlank()) "选择菜品" else dishName) { pickDish = true }
                }

                Spacer(Modifier.height(14.dp))
                Text("打个分（可不填）", fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                Spacer(Modifier.height(6.dp))
                Row {
                    (1..5).forEach { i ->
                        Text(
                            "★",
                            fontSize = 26.sp,
                            color = if (i <= rating) Color(0xFFF59E0B) else MaterialTheme.colorScheme.outline,
                            modifier = Modifier
                                .clip(CircleShape)
                                .clickable { rating = if (rating == i) 0 else i }
                                .padding(horizontal = 3.dp)
                        )
                    }
                }

                Spacer(Modifier.height(12.dp))
                OutlinedTextField(
                    value = comment,
                    onValueChange = { comment = it.take(500) },
                    label = { Text("说点什么（可不填）") },
                    minLines = 3,
                    maxLines = 6,
                    modifier = Modifier.fillMaxWidth()
                )

                if (!isSame) {
                    Spacer(Modifier.height(14.dp))
                    Text(
                        "一起吃的小伙伴（可多选，计入 TA 的月统计）",
                        fontSize = 12.sp,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                    Spacer(Modifier.height(6.dp))
                    val candidates = users.filter { it.id != myId }
                    if (candidates.isEmpty()) {
                        Text("暂无其他用户", fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    } else {
                        Column {
                            candidates.chunked(3).forEach { row ->
                                Row(horizontalArrangement = Arrangement.spacedBy(6.dp), modifier = Modifier.padding(bottom = 6.dp)) {
                                    row.forEach { user ->
                                        val on = user.id in guests
                                        Box(
                                            Modifier
                                                .clip(RoundedCornerShape(10.dp))
                                                .background(if (on) BrandPink.copy(alpha = .16f) else MaterialTheme.colorScheme.surfaceVariant.copy(alpha = .5f))
                                                .clickable {
                                                    if (on) guests.remove(user.id) else guests.add(user.id)
                                                }
                                                .padding(horizontal = 10.dp, vertical = 6.dp)
                                        ) {
                                            Text(
                                                user.displayName,
                                                fontSize = 12.sp,
                                                color = if (on) BrandPink else MaterialTheme.colorScheme.onSurfaceVariant,
                                                maxLines = 1
                                            )
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                Spacer(Modifier.height(12.dp))
                Text("图片（最多 9 张，可不填）", fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                Spacer(Modifier.height(6.dp))
                ImagePickerRow(
                    token = token,
                    images = images.toList(),
                    onPick = { url -> if (url.isNotBlank() && images.size < 9) images.add(url) },
                    maxCount = 9
                )
                if (images.isNotEmpty()) {
                    Spacer(Modifier.height(6.dp))
                    Text("已上传 ${images.size} 张", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }

                error?.let {
                    Spacer(Modifier.height(8.dp))
                    Text(it, fontSize = 12.sp, color = MaterialTheme.colorScheme.error)
                }

                Spacer(Modifier.height(16.dp))
                PrimaryButton(
                    text = if (busy) "提交中…" else "提交",
                    enabled = dishId != 0 && !busy
                ) {
                    busy = true
                    scope.launch {
                        runCatching {
                            EatApi.addReview(
                                token = token,
                                storeId = storeId.takeIf { it != 0 },
                                dishId = dishId,
                                rating = rating.takeIf { it > 0 },
                                comment = comment.trim(),
                                images = images.toList(),
                                guestIds = guests.toList(),
                                parentId = parent?.id
                            )
                        }.onSuccess { onDone() }.onFailure { error = it.message ?: "提交失败" }
                        busy = false
                    }
                }
                Spacer(Modifier.height(6.dp))
                TextButton(onClick = onDismiss, modifier = Modifier.fillMaxWidth()) { Text("取消") }
            }
        }
    }

    if (pickStore) {
        PickerDialog("选择店铺", stores.map { it.id to it.name }, { pickStore = false }) {
            storeId = it
            dishId = 0
            pickStore = false
        }
    }
    if (pickDish) {
        val list = dishes.filter { storeId == 0 || it.storeId == storeId }
        PickerDialog(
            "选择菜品",
            list.map { it.id to "${it.name}（${priceText(it.price)}）" },
            { pickDish = false }
        ) { id ->
            dishId = id
            pickDish = false
            if (storeId == 0) storeId = dishes.firstOrNull { it.id == id }?.storeId ?: 0
        }
    }
}

@Composable
private fun PickerRow(text: String, onClick: () -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(12.dp))
            .background(MaterialTheme.colorScheme.surfaceVariant.copy(alpha = .5f))
            .clickable(onClick = onClick)
            .padding(12.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Text(text, fontSize = 13.sp, modifier = Modifier.weight(1f), maxLines = 1, overflow = TextOverflow.Ellipsis)
        Text("▾", fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

/**
 * 选图 + 上传到 /api/eat/upload，回调返回服务端相对地址。
 */
@Composable
private fun ImagePickerRow(
    token: String,
    images: List<String>,
    maxCount: Int,
    onPick: (String) -> Unit
) {
    var uploading by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    val context = LocalContext.current
    val scope = rememberCoroutineScope()

    val picker = rememberLauncherForActivityResult(
        ActivityResultContracts.PickMultipleVisualMedia(maxItems = maxCount.coerceAtLeast(2))
    ) { uris ->
        if (uris.isEmpty()) return@rememberLauncherForActivityResult
        uploading = true
        error = null
        scope.launch {
            try {
                uris.take(maxCount).forEach { uri ->
                    val resolver = context.contentResolver
                    val bytes = resolver.openInputStream(uri)?.use { it.readBytes() }
                        ?: throw IllegalStateException("读取图片失败")
                    if (bytes.size > 8 * 1024 * 1024) throw IllegalStateException("单张图片不能超过 8MB")
                    val mime = resolver.getType(uri) ?: "image/jpeg"
                    val ext = when {
                        mime.contains("png") -> "png"
                        mime.contains("webp") -> "webp"
                        mime.contains("gif") -> "gif"
                        else -> "jpg"
                    }
                    onPick(EatApi.upload(token, bytes, "eat.$ext", mime))
                }
            } catch (e: Exception) {
                error = e.message ?: "上传失败"
            } finally {
                uploading = false
            }
        }
    }

    Column {
        Row(horizontalArrangement = Arrangement.spacedBy(6.dp), verticalAlignment = Alignment.CenterVertically) {
            images.take(maxCount).forEach { url ->
                val real = EatApi.imageUrl(url)
                Box(
                    Modifier
                        .size(58.dp)
                        .clip(RoundedCornerShape(10.dp))
                        .background(MaterialTheme.colorScheme.surfaceVariant)
                ) {
                    if (!real.isNullOrBlank()) {
                        AsyncImage(model = real, contentDescription = null, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize())
                    }
                }
            }
            if (images.size < maxCount) {
                Box(
                    Modifier
                        .size(58.dp)
                        .clip(RoundedCornerShape(10.dp))
                        .background(MaterialTheme.colorScheme.surfaceVariant.copy(alpha = .6f))
                        .clickable(enabled = !uploading) {
                            picker.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly))
                        },
                    contentAlignment = Alignment.Center
                ) {
                    if (uploading) {
                        CircularProgressIndicator(strokeWidth = 2.dp, color = BrandPink, modifier = Modifier.size(18.dp))
                    } else {
                        Icon(Icons.Filled.Add, contentDescription = "添加图片", tint = BrandPink, modifier = Modifier.size(20.dp))
                    }
                }
            }
        }
        error?.let {
            Spacer(Modifier.height(4.dp))
            Text(it, fontSize = 11.sp, color = MaterialTheme.colorScheme.error)
        }
    }
}
