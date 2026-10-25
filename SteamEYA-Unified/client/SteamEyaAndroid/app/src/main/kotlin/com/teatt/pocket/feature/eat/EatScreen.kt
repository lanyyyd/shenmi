package com.teatt.pocket.feature.eat

import androidx.activity.compose.BackHandler
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
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.ArrowBack
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Restaurant
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
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
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import coil3.compose.AsyncImage
import com.teatt.pocket.data.EatApi
import com.teatt.pocket.data.EatDish
import com.teatt.pocket.data.EatStore
import com.teatt.pocket.ui.BrandPink
import com.teatt.pocket.ui.BrandViolet
import com.teatt.pocket.ui.LocalBarInset
import kotlinx.coroutines.launch
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * 「今天吃点啥」原生版。
 *
 * 外层仍然是 App 的四段玻璃菜单栏，所以这里不再自建底部 bar，改用顶部胶囊分段：
 * 首页 / 转盘 / 评价 / 我的。数据统一提到这一层缓存，切分段不重复请求。
 */
@Composable
fun EatScreen(
    token: String,
    modifier: Modifier = Modifier
) {
    var tab by remember { mutableIntStateOf(0) }
    var stores by remember { mutableStateOf<List<EatStore>>(emptyList()) }
    var loading by remember { mutableStateOf(true) }
    var error by remember { mutableStateOf<String?>(null) }
    var detail by remember { mutableStateOf<EatStore?>(null) }
    val scope = rememberCoroutineScope()

    suspend fun loadStores() {
        loading = true
        error = null
        runCatching { stores = EatApi.stores(token) }
            .onFailure { error = it.message ?: "加载失败" }
        loading = false
    }

    LaunchedEffect(token) { loadStores() }

    BackHandler(enabled = detail != null || tab != 0) {
        if (detail != null) detail = null else tab = 0
    }

    Column(modifier.fillMaxSize()) {
        EatHeader(
            tab = tab,
            onSelect = { tab = it; detail = null },
            onRefresh = { scope.launch { loadStores() } }
        )

        Box(Modifier.weight(1f).fillMaxWidth()) {
            val store = detail
            when {
                store != null -> StoreDetailPage(
                    token = token,
                    store = store,
                    onBack = { detail = null },
                    onEatHere = {
                        scope.launch {
                            runCatching { EatApi.pick(token, store.id) }
                            detail = null
                            tab = 1
                        }
                    }
                )

                tab == 0 -> StoreListPage(
                    token = token,
                    stores = stores,
                    loading = loading,
                    error = error,
                    onOpen = { detail = it },
                    onReload = { scope.launch { loadStores() } }
                )

                tab == 1 -> WheelPage(token = token)

                tab == 2 -> FeedPage(token = token)

                else -> MinePage(token = token, stores = stores, onNeedReload = { scope.launch { loadStores() } })
            }
        }
    }
}

@Composable
private fun EatHeader(tab: Int, onSelect: (Int) -> Unit, onRefresh: () -> Unit) {
    Column(
        Modifier
            .fillMaxWidth()
            .statusBarsPadding()
            .padding(start = 20.dp, end = 12.dp, top = 14.dp, bottom = 6.dp)
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text("今天吃点啥", fontSize = 26.sp, fontWeight = FontWeight.Bold)
                Text(
                    "按时吃饭，快乐干饭",
                    fontSize = 12.sp,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
            IconButton(onClick = onRefresh) {
                Icon(
                    Icons.Filled.Refresh,
                    contentDescription = "刷新",
                    tint = BrandPink,
                    modifier = Modifier.size(20.dp)
                )
            }
        }

        Spacer(Modifier.height(12.dp))

        Row(
            Modifier
                .fillMaxWidth()
                .clip(RoundedCornerShape(14.dp))
                .background(MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.5f))
                .padding(4.dp),
            horizontalArrangement = Arrangement.spacedBy(4.dp)
        ) {
            listOf("首页", "转盘", "评价", "我的").forEachIndexed { index, label ->
                val selected = tab == index
                Box(
                    Modifier
                        .weight(1f)
                        .clip(RoundedCornerShape(11.dp))
                        .background(if (selected) BrandPink else Color.Transparent)
                        .clickable { onSelect(index) }
                        .padding(vertical = 9.dp),
                    contentAlignment = Alignment.Center
                ) {
                    Text(
                        label,
                        fontSize = 13.sp,
                        fontWeight = if (selected) FontWeight.SemiBold else FontWeight.Normal,
                        color = if (selected) Color.White else MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
            }
        }
    }
}

// ---------------------------------------------------------------- 首页：店铺列表

@Composable
private fun StoreListPage(
    token: String,
    stores: List<EatStore>,
    loading: Boolean,
    error: String?,
    onOpen: (EatStore) -> Unit,
    onReload: () -> Unit
) {
    var showAdd by remember { mutableStateOf(false) }
    val bottomInset = LocalBarInset.current + 20.dp

    Box(Modifier.fillMaxSize()) {
        when {
            loading && stores.isEmpty() -> CenterLoading()
            error != null && stores.isEmpty() -> CenterTip(error, onReload)
            stores.isEmpty() -> CenterTip("还没有店铺，点下面按钮加一家吧", onReload)
            else -> LazyColumn(
                Modifier.fillMaxSize(),
                contentPadding = PaddingValues(start = 20.dp, end = 20.dp, top = 6.dp, bottom = bottomInset),
                verticalArrangement = Arrangement.spacedBy(12.dp)
            ) {
                item {
                    Text(
                        "附近好店 · 就等你来",
                        fontSize = 12.sp,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.padding(vertical = 4.dp)
                    )
                }
                items(stores, key = { it.id }) { store ->
                    StoreCard(store) { onOpen(store) }
                }
                item {
                    OutlinedAddButton("找不到店铺？点我上传") { showAdd = true }
                }
            }
        }
    }

    if (showAdd) {
        AddStoreDialog(
            token = token,
            onDismiss = { showAdd = false },
            onDone = { showAdd = false; onReload() }
        )
    }
}

@Composable
private fun StoreCard(store: EatStore, onClick: () -> Unit) {
    Surface(
        shape = RoundedCornerShape(20.dp),
        color = MaterialTheme.colorScheme.surface,
        modifier = Modifier.fillMaxWidth().clickable(onClick = onClick)
    ) {
        Row(Modifier.padding(12.dp)) {
            EatCover(
                url = store.image,
                modifier = Modifier.size(92.dp).clip(RoundedCornerShape(14.dp))
            )
            Spacer(Modifier.width(12.dp))
            Column(Modifier.weight(1f)) {
                Text(
                    store.name,
                    fontSize = 16.sp,
                    fontWeight = FontWeight.SemiBold,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
                Spacer(Modifier.height(4.dp))
                Row(verticalAlignment = Alignment.CenterVertically) {
                    StarRow(rating = store.rating, count = store.ratingCount)
                }
                if (store.location.isNotBlank()) {
                    Spacer(Modifier.height(3.dp))
                    Text(
                        "📍 ${store.location}",
                        fontSize = 11.sp,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis
                    )
                }
                if (store.recommend.isNotEmpty()) {
                    Spacer(Modifier.height(6.dp))
                    Text(
                        store.recommend.joinToString(" · ") { it.name },
                        fontSize = 11.sp,
                        color = BrandPink,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis
                    )
                }
            }
        }
    }
}

// ---------------------------------------------------------------- 店铺详情

@Composable
private fun StoreDetailPage(
    token: String,
    store: EatStore,
    onBack: () -> Unit,
    onEatHere: () -> Unit
) {
    var dishes by remember { mutableStateOf<List<EatDish>>(emptyList()) }
    var loading by remember { mutableStateOf(true) }
    var picked by remember { mutableStateOf<EatDish?>(null) }
    var pending by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()
    val bottomInset = LocalBarInset.current + 20.dp

    LaunchedEffect(store.id) {
        loading = true
        runCatching { dishes = EatApi.storeDetail(token, store.id).dishes }
        loading = false
    }

    Box(Modifier.fillMaxSize()) {
        LazyColumn(
            Modifier.fillMaxSize(),
            contentPadding = PaddingValues(start = 20.dp, end = 20.dp, bottom = bottomInset)
        ) {
            item {
                Row(
                    Modifier.fillMaxWidth().padding(top = 4.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    IconButton(onClick = onBack) {
                        Icon(Icons.Filled.ArrowBack, contentDescription = "返回")
                    }
                    Text("返回店铺列表", fontSize = 13.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
            item {
                EatCover(
                    url = store.image,
                    modifier = Modifier.fillMaxWidth().aspectRatio(16f / 9f).clip(RoundedCornerShape(18.dp))
                )
            }
            item {
                Column(Modifier.padding(top = 12.dp)) {
                    Text(store.name, fontSize = 20.sp, fontWeight = FontWeight.Bold)
                    Spacer(Modifier.height(5.dp))
                    StarRow(rating = store.rating, count = store.ratingCount)
                    if (store.location.isNotBlank()) {
                        Spacer(Modifier.height(4.dp))
                        Text(
                            "📍 ${store.location}",
                            fontSize = 12.sp,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                }
            }
            item {
                PrimaryButton(
                    text = if (pending) "正在挑…" else "🍽️ 就吃这家了！",
                    enabled = !pending,
                    modifier = Modifier.padding(top = 14.dp)
                ) {
                    pending = true
                    scope.launch {
                        runCatching { EatApi.pick(token, store.id) }.onSuccess { onEatHere() }
                        pending = false
                    }
                }
            }
            item {
                Text(
                    "招牌菜品",
                    fontSize = 12.sp,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(top = 18.dp, bottom = 8.dp)
                )
            }
            if (loading) {
                item { Box(Modifier.fillMaxWidth().padding(24.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator(color = BrandPink, strokeWidth = 2.dp) } }
            } else if (dishes.isEmpty()) {
                item { Text("还没有菜品", fontSize = 13.sp, color = MaterialTheme.colorScheme.onSurfaceVariant) }
            } else {
                items(dishes.chunked(2)) { row ->
                    Row(horizontalArrangement = Arrangement.spacedBy(12.dp), modifier = Modifier.padding(bottom = 12.dp)) {
                        row.forEach { dish ->
                            DishCard(dish, Modifier.weight(1f)) { picked = dish }
                        }
                        if (row.size == 1) Spacer(Modifier.weight(1f))
                    }
                }
            }
        }
    }

    picked?.let { dish ->
        DishDetailDialog(
            dish = dish,
            onDismiss = { picked = null },
            onChoose = {
                scope.launch {
                    runCatching { EatApi.choose(token, dish.id) }
                    picked = null
                    onEatHere()
                }
            }
        )
    }
}

@Composable
private fun DishCard(dish: EatDish, modifier: Modifier = Modifier, onClick: () -> Unit) {
    Surface(
        modifier = modifier.clickable(onClick = onClick),
        shape = RoundedCornerShape(16.dp),
        color = MaterialTheme.colorScheme.surface
    ) {
        Column {
            EatCover(
                url = dish.image,
                modifier = Modifier.fillMaxWidth().aspectRatio(1f).clip(RoundedCornerShape(16.dp))
            )
            Column(Modifier.padding(10.dp)) {
                Text(dish.name, fontSize = 14.sp, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Spacer(Modifier.height(3.dp))
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(priceText(dish.price), fontSize = 12.sp, color = BrandPink, fontWeight = FontWeight.SemiBold)
                    if (dish.eatCount > 0) {
                        Spacer(Modifier.width(6.dp))
                        Text("吃过 ${dish.eatCount}", fontSize = 10.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
            }
        }
    }
}

// ---------------------------------------------------------------- 通用小组件

@Composable
fun EatCover(url: String?, modifier: Modifier = Modifier, fallback: String = "🍽️") {
    val real = EatApi.imageUrl(url)
    Box(modifier.background(Brush.linearGradient(listOf(BrandPink.copy(alpha = .22f), BrandViolet.copy(alpha = .18f))))) {
        if (!real.isNullOrBlank()) {
            AsyncImage(
                model = real,
                contentDescription = null,
                contentScale = ContentScale.Crop,
                modifier = Modifier.fillMaxSize()
            )
        } else {
            Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                Text(fallback, fontSize = 26.sp)
            }
        }
    }
}

@Composable
fun StarRow(rating: Double?, count: Int) {
    if (rating == null) {
        Text("暂无评分", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
        return
    }
    Text("★", fontSize = 12.sp, color = Color(0xFFF59E0B))
    Spacer(Modifier.width(3.dp))
    Text(
        String.format(Locale.US, "%.1f", rating),
        fontSize = 12.sp,
        fontWeight = FontWeight.SemiBold,
        color = Color(0xFFF59E0B)
    )
    if (count > 0) {
        Spacer(Modifier.width(4.dp))
        Text("($count)", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

@Composable
fun PrimaryButton(
    text: String,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    onClick: () -> Unit
) {
    Box(
        modifier
            .fillMaxWidth()
            .height(48.dp)
            .clip(RoundedCornerShape(15.dp))
            .background(if (enabled) BrandPink else BrandPink.copy(alpha = .5f))
            .clickable(enabled = enabled, onClick = onClick),
        contentAlignment = Alignment.Center
    ) {
        Text(text, color = Color.White, fontSize = 15.sp, fontWeight = FontWeight.SemiBold)
    }
}

@Composable
fun OutlinedAddButton(text: String, onClick: () -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(16.dp))
            .background(MaterialTheme.colorScheme.surfaceVariant.copy(alpha = .45f))
            .clickable(onClick = onClick)
            .padding(vertical = 14.dp),
        horizontalArrangement = Arrangement.Center,
        verticalAlignment = Alignment.CenterVertically
    ) {
        Icon(Icons.Filled.Add, null, Modifier.size(16.dp), tint = BrandPink)
        Spacer(Modifier.width(6.dp))
        Text(text, fontSize = 13.sp, color = BrandPink, fontWeight = FontWeight.Medium)
    }
}

@Composable
fun CenterLoading() {
    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        CircularProgressIndicator(color = BrandPink, strokeWidth = 2.dp)
    }
}

@Composable
fun CenterTip(text: String, onRetry: (() -> Unit)? = null) {
    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
            Icon(
                Icons.Filled.Restaurant,
                null,
                Modifier.size(34.dp),
                tint = MaterialTheme.colorScheme.onSurfaceVariant
            )
            Spacer(Modifier.height(10.dp))
            Text(text, fontSize = 13.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
            if (onRetry != null) {
                Spacer(Modifier.height(6.dp))
                TextButton(onClick = onRetry) { Text("重试") }
            }
        }
    }
}

fun priceText(price: Double): String =
    if (price <= 0) "价格待定" else "¥" + String.format(Locale.US, "%.0f", price)

fun eatTimeText(ts: Long): String {
    if (ts <= 0) return ""
    val diff = System.currentTimeMillis() - ts
    return when {
        diff < 60_000 -> "刚刚"
        diff < 3_600_000 -> "${diff / 60_000} 分钟前"
        diff < 86_400_000 -> "${diff / 3_600_000} 小时前"
        diff < 172_800_000 -> "昨天"
        diff < 604_800_000 -> "${diff / 86_400_000} 天前"
        else -> SimpleDateFormat("MM-dd", Locale.getDefault()).format(Date(ts))
    }
}

@Composable
fun Avatar(url: String?, name: String, size: Int = 34) {
    val real = EatApi.imageUrl(url)
    Box(
        Modifier
            .size(size.dp)
            .clip(CircleShape)
            .background(BrandPink.copy(alpha = .16f)),
        contentAlignment = Alignment.Center
    ) {
        if (!real.isNullOrBlank()) {
            AsyncImage(model = real, contentDescription = null, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize())
        } else {
            Text(
                name.take(1).ifBlank { "🍜" },
                fontSize = (size * 0.42f).sp,
                color = BrandPink,
                fontWeight = FontWeight.Bold
            )
        }
    }
}
