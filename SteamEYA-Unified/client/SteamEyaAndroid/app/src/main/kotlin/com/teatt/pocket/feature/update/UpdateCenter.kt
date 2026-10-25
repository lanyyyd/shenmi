package com.teatt.pocket.feature.update

import android.app.DownloadManager
import android.content.Context
import android.net.Uri
import android.os.Environment
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import com.teatt.pocket.core.AppVersion
import com.teatt.pocket.data.UpdateApi
import com.teatt.pocket.data.UpdateInfoDto

/** 全局更新弹窗状态：RootScreen 统一渲染，任何页面都能触发。 */
sealed interface UpdateDialogState {
    data class Available(val info: UpdateInfoDto) : UpdateDialogState
    data class Message(val title: String, val body: String) : UpdateDialogState
}

/**
 * 更新检查中心。
 *
 * 与桌面端共用同一个更新源（网关 `/api/update/latest?platform=android`）：
 * 管理员后台「更新管理 → 手机端」上传 APK 并设为当前版本，这里就能查到。
 */
object UpdateCenter {

    var dialog by mutableStateOf<UpdateDialogState?>(null)
        private set

    var checking by mutableStateOf(false)
        private set

    /**
     * @param manual true = 用户主动点「检查更新」，无论结果都给反馈；
     *               false = 进主界面自动检查，只有真的存在新版本才弹窗。
     */
    suspend fun check(token: String, manual: Boolean) {
        if (checking || token.isBlank()) return
        checking = true
        try {
            val dto = UpdateApi.latest(token)
            if (dto.available && AppVersion.isNewer(dto.version, AppVersion.current)) {
                dialog = UpdateDialogState.Available(dto)
            } else if (manual) {
                dialog = UpdateDialogState.Message(
                    "已是最新版本",
                    "当前版本 ${AppVersion.current}" + if (dto.version.isBlank()) "" else "，服务端最新 ${dto.version}"
                )
            }
        } catch (e: Exception) {
            if (manual) {
                dialog = UpdateDialogState.Message("检查更新失败", e.message ?: "请检查网络后重试")
            }
        } finally {
            checking = false
        }
    }

    fun dismiss() {
        dialog = null
    }

    /** 交给系统下载管理器：通知栏点一下就能装，不用申请存储/安装权限。 */
    fun download(context: Context, info: UpdateInfoDto): Boolean {
        val url = UpdateApi.downloadUrl(info)
        if (url.isBlank()) return false
        return runCatching {
            val name = info.fileName.ifBlank { "ShenMi-${info.version}.apk" }
            val request = DownloadManager.Request(Uri.parse(url))
                .setTitle("神·魔法武器 ${info.version}")
                .setDescription("正在下载更新，完成后点通知安装")
                .setNotificationVisibility(DownloadManager.Request.VISIBILITY_VISIBLE_NOTIFY_COMPLETED)
                .setMimeType("application/vnd.android.package-archive")
                .setDestinationInExternalPublicDir(Environment.DIRECTORY_DOWNLOADS, name)
            val dm = context.getSystemService(Context.DOWNLOAD_SERVICE) as DownloadManager
            dm.enqueue(request)
            true
        }.getOrDefault(false)
    }
}
