package com.teatt.pocket.data

import com.teatt.pocket.core.Api
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject

/** 吃什么模块的传输模型，字段与服务端 `server/app/routers/eat.py` 一一对应。 */

@Serializable
data class EatUser(
    val id: Int = 0,
    val username: String = "",
    val nickname: String = "",
    val avatar: String = "",
    val level: Int = 0
) {
    val displayName: String get() = nickname.ifBlank { username }
}

@Serializable
data class EatDish(
    val id: Int = 0,
    val storeId: Int = 0,
    val name: String = "",
    val image: String = "",
    val price: Double = 0.0,
    val eatCount: Int = 0,
    val ownerId: Int = 0,
    val createdAt: Long = 0,
    val storeName: String = "",
    val location: String = "",
    val imageStatus: String = "",
    val imageError: String = ""
)

@Serializable
data class EatStore(
    val id: Int = 0,
    val name: String = "",
    val image: String = "",
    val location: String = "",
    val rating: Double? = null,
    val ratingCount: Int = 0,
    val ownerId: Int = 0,
    val createdAt: Long = 0,
    val recommend: List<EatDish> = emptyList(),
    val imageStatus: String = "",
    val imageError: String = ""
)

@Serializable
data class EatReview(
    val id: Int = 0,
    val user: EatUser = EatUser(),
    val storeId: Int? = null,
    val storeName: String = "",
    val dishId: Int? = null,
    val dishName: String = "",
    val price: Double = 0.0,
    val dishImage: String = "",
    val parentId: Int? = null,
    val rating: Int? = null,
    val comment: String = "",
    val image: String = "",
    val images: List<String> = emptyList(),
    val createdAt: Long = 0,
    val guests: List<EatUser> = emptyList(),
    val likeCount: Int = 0,
    val liked: Boolean = false,
    val sameCount: Int = 0,
    val sames: List<EatReview> = emptyList()
)

@Serializable
data class EatTask(
    val id: Int = 0,
    val ownerId: Int? = null,
    val global: Boolean = false,
    val dishId: Int = 0,
    val storeId: Int = 0,
    val dishName: String = "",
    val dishImage: String = "",
    val price: Double = 0.0,
    val storeName: String = "",
    val location: String = "",
    val rating: Double? = null,
    val createdAt: Long = 0
)

@Serializable
data class EatTaskState(
    val ok: Boolean = false,
    val overridden: Boolean = false,
    val global: Boolean = false,
    val task: EatTask? = null
)

@Serializable
data class EatCount(val name: String = "", val count: Int = 0)

@Serializable
data class EatImageRef(
    val id: Int = 0,
    val image: String = "",
    val comment: String = "",
    val dishName: String = "",
    val storeName: String = "",
    val user: EatUser = EatUser(),
    val createdAt: Long = 0
)

@Serializable
data class EatSummary(
    val ok: Boolean = false,
    val period: String = "month",
    val count: Int = 0,
    val spend: Double = 0.0,
    val topStore: String = "",
    val topDish: String = "",
    val stores: List<EatCount> = emptyList(),
    val dishes: List<EatCount> = emptyList(),
    val images: List<EatImageRef> = emptyList()
)

@Serializable
data class EatStoresDto(val ok: Boolean = false, val stores: List<EatStore> = emptyList())

@Serializable
data class EatStoreDto(val ok: Boolean = false, val store: EatStore = EatStore())

@Serializable
data class EatStoreDishesDto(
    val ok: Boolean = false,
    val store: EatStore = EatStore(),
    val dishes: List<EatDish> = emptyList()
)

@Serializable
data class EatDishesDto(val ok: Boolean = false, val dishes: List<EatDish> = emptyList())

@Serializable
data class EatReviewsDto(
    val ok: Boolean = false,
    val reviews: List<EatReview> = emptyList(),
    val total: Int = 0,
    val hasMore: Boolean = false
)

@Serializable
data class EatWallDto(val ok: Boolean = false, val images: List<EatImageRef> = emptyList())

@Serializable
data class EatUsersDto(val ok: Boolean = false, val users: List<EatUser> = emptyList())

@Serializable
data class EatUploadDto(val ok: Boolean = false, val url: String = "")

@Serializable
data class EatLikeDto(val ok: Boolean = false, val liked: Boolean = false)

@Serializable
data class EatImageStatusDto(
    val ok: Boolean = false,
    val image: String = "",
    val status: String = "",
    val error: String = ""
)

object EatApi {

    private const val BASE = "/api/eat"
    private const val JSON_TYPE = "application/json; charset=utf-8"

    suspend fun stores(token: String): List<EatStore> =
        Api.decode<EatStoresDto>(Api.get("$BASE/stores", token)).stores

    suspend fun storeDetail(token: String, storeId: Int): EatStoreDishesDto =
        Api.decode(Api.get("$BASE/stores/$storeId/dishes", token))

    suspend fun dishes(token: String): List<EatDish> =
        Api.decode<EatDishesDto>(Api.get("$BASE/dishes", token)).dishes

    suspend fun reviews(token: String, limit: Int = 30, offset: Int = 0): EatReviewsDto =
        Api.decode(
            Api.get("$BASE/reviews", token, mapOf("limit" to limit.toString(), "offset" to offset.toString()))
        )

    suspend fun wall(token: String, limit: Int = 120): List<EatImageRef> =
        Api.decode<EatWallDto>(Api.get("$BASE/wall", token, mapOf("limit" to limit.toString()))).images

    suspend fun summary(token: String, period: String): EatSummary =
        Api.decode(Api.get("$BASE/summary", token, mapOf("period" to period)))

    suspend fun users(token: String): List<EatUser> =
        Api.decode<EatUsersDto>(Api.get("$BASE/users", token)).users

    suspend fun task(token: String): EatTaskState = Api.decode(Api.get("$BASE/task", token))

    suspend fun spin(token: String): EatTaskState =
        Api.decode(Api.postJson("$BASE/spin", "{}", token))

    suspend fun pick(token: String, storeId: Int): EatTaskState =
        Api.decode(
            Api.postJson("$BASE/pick", buildJsonObject { put("storeId", JsonPrimitive(storeId)) }.toString(), token)
        )

    suspend fun choose(token: String, dishId: Int): EatTaskState =
        Api.decode(
            Api.postJson("$BASE/choose", buildJsonObject { put("dishId", JsonPrimitive(dishId)) }.toString(), token)
        )

    suspend fun eatOther(token: String) {
        Api.postJson("$BASE/task/other", "{}", token)
    }

    suspend fun like(token: String, reviewId: Int): Boolean =
        Api.decode<EatLikeDto>(Api.postJson("$BASE/reviews/$reviewId/like", "{}", token)).liked

    suspend fun addReview(
        token: String,
        storeId: Int?,
        dishId: Int,
        rating: Int?,
        comment: String,
        images: List<String>,
        guestIds: List<Int> = emptyList(),
        parentId: Int? = null
    ): EatReview {
        val body = buildJsonObject {
            put("dishId", JsonPrimitive(dishId))
            if (storeId != null) put("storeId", JsonPrimitive(storeId))
            if (parentId != null) put("parentId", JsonPrimitive(parentId))
            if (rating != null) put("rating", JsonPrimitive(rating))
            put("comment", JsonPrimitive(comment))
            put("images", buildJsonArray { images.forEach { add(JsonPrimitive(it)) } })
            put("guestIds", buildJsonArray { guestIds.forEach { add(JsonPrimitive(it)) } })
        }.toString()
        val raw = Api.postJson("$BASE/reviews", body, token)
        return Api.decode<ReviewCreateDto>(raw).review
    }

    /** 编辑自己的评价：店铺/菜品不可改，只能改评分、评语、图片和「一起吃」。 */
    suspend fun updateReview(
        token: String,
        reviewId: Int,
        rating: Int?,
        comment: String,
        images: List<String>,
        guestIds: List<Int>
    ): EatReview {
        val body = buildJsonObject {
            put("rating", if (rating != null) JsonPrimitive(rating) else JsonNull)
            put("comment", JsonPrimitive(comment))
            put("images", buildJsonArray { images.forEach { add(JsonPrimitive(it)) } })
            put("guestIds", buildJsonArray { guestIds.forEach { add(JsonPrimitive(it)) } })
        }.toString()
        val raw = Api.putJson("$BASE/reviews/$reviewId", body, token)
        return Api.decode<ReviewCreateDto>(raw).review
    }

    suspend fun deleteReview(token: String, reviewId: Int) {
        Api.delete("$BASE/reviews/$reviewId", token = token)
    }

    /** AI 重新生图：kind 取 store / dish，补充提示词会追加到原有提示词后面。 */
    suspend fun regenImage(token: String, kind: String, id: Int, extraPrompt: String) {
        val path = if (kind == "store") "$BASE/stores/$id/regen-image" else "$BASE/dishes/$id/regen-image"
        val body = buildJsonObject { put("extraPrompt", JsonPrimitive(extraPrompt)) }.toString()
        Api.postJson(path, body, token)
    }

    suspend fun imageStatus(token: String, kind: String, id: Int): EatImageStatusDto =
        Api.decode(
            Api.get("$BASE/image-status", token, mapOf("kind" to kind, "id" to id.toString()))
        )

    suspend fun addStore(token: String, name: String, location: String, image: String) {
        val body = buildJsonObject {
            put("name", JsonPrimitive(name))
            put("location", JsonPrimitive(location))
            put("image", JsonPrimitive(image))
        }.toString()
        Api.postJson("$BASE/stores", body, token)
    }

    suspend fun addDish(token: String, storeId: Int, name: String, price: Double, image: String) {
        val body = buildJsonObject {
            put("storeId", JsonPrimitive(storeId))
            put("name", JsonPrimitive(name))
            put("price", JsonPrimitive(price))
            put("image", JsonPrimitive(image))
        }.toString()
        Api.postJson("$BASE/dishes", body, token)
    }

    /** 评价配图上传，返回 /uploads/eat/xxx.jpg 这样的相对地址。 */
    suspend fun upload(token: String, bytes: ByteArray, fileName: String, mime: String): String =
        Api.decode<EatUploadDto>(
            Api.postFile("$BASE/upload", "file", fileName, mime, bytes, token = token)
        ).url

    fun imageUrl(path: String?): String? = Api.absolute(path)
}

@Serializable
data class ReviewCreateDto(val ok: Boolean = false, val review: EatReview = EatReview())
