package com.teatt.pocket.feature.update

import android.app.DownloadManager
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Environment
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.core.content.FileProvider
import com.teatt.pocket.core.AppVersion
import com.teatt.pocket.data.UpdateApi
import com.teatt.pocket.data.UpdateInfoDto
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File

/** 全局更新弹窗状态：RootScreen 统一渲染，任何页面都能触发。 */
sealed interface UpdateDialogState {
    data class Available(val info: UpdateInfoDto) : UpdateDialogState
    data class Message(val title: String, val body: String) : UpdateDialogState
}

/** 下载进度：null 表示空闲；Running 时弹窗里显示进度条。 */
sealed interface UpdateDownloadState {
    data class Running(val downloaded: Long, val total: Long) : UpdateDownloadState
    data class Failed(val message: String) : UpdateDownloadState
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

    var downloadState by mutableStateOf<UpdateDownloadState?>(null)
        private set

    /** 下载与安装不能跟着弹窗的 Composition 一起被取消，所以挂在全局作用域上。 */
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)

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

    /**
     * 下载整包安装包：进度显示在弹窗里，下载完直接拉起系统安装器。
     *
     * 安卓不允许普通应用静默替换自己，所以最后一屏「安装」必须由用户点；
     * 但不再需要去通知栏找文件，点一次「立即更新」→ 进度条 → 安装界面。
     */
    fun startDownload(context: Context, info: UpdateInfoDto) {
        if (downloadState is UpdateDownloadState.Running) return
        val app = context.applicationContext
        scope.launch { runDownload(app, info) }
    }

    private suspend fun runDownload(context: Context, info: UpdateInfoDto) {
        val url = UpdateApi.downloadUrl(info)
        if (url.isBlank()) {
            downloadState = UpdateDownloadState.Failed("服务端没有返回下载地址，请稍后重试")
            return
        }
        val dir = context.getExternalFilesDir(Environment.DIRECTORY_DOWNLOADS)
        if (dir == null) {
            downloadState = UpdateDownloadState.Failed("存储不可用，无法下载")
            return
        }
        // 清掉上次的安装包：同名文件已存在时系统下载管理器会直接报错
        dir.listFiles()?.forEach { if (it.isFile && it.name.endsWith(".apk")) it.delete() }

        val name = "ShenMi-${info.version}.apk"
        val file = File(dir, name)
        val request = DownloadManager.Request(Uri.parse(url))
            .setTitle("神·魔法武器 ${info.version}")
            .setDescription("正在下载更新，完成后自动打开安装界面")
            .setNotificationVisibility(DownloadManager.Request.VISIBILITY_VISIBLE_NOTIFY_COMPLETED)
            .setMimeType("application/vnd.android.package-archive")
            .setDestinationInExternalFilesDir(context, Environment.DIRECTORY_DOWNLOADS, name)

        val dm = context.getSystemService(Context.DOWNLOAD_SERVICE) as DownloadManager
        val id = runCatching { dm.enqueue(request) }.getOrElse { e ->
            downloadState = UpdateDownloadState.Failed("无法开始下载：${e.message ?: "未知错误"}")
            return
        }

        downloadState = UpdateDownloadState.Running(0, info.fileSize)
        while (true) {
            delay(400)
            val p = withContext(Dispatchers.IO) { readProgress(dm, id) }
            if (p == null) {
                downloadState = UpdateDownloadState.Failed("下载任务已丢失，请重试")
                return
            }
            when (p.status) {
                DownloadManager.STATUS_SUCCESSFUL -> {
                    downloadState = null
                    dialog = null
                    install(context, file)
                    return
                }

                DownloadManager.STATUS_FAILED -> {
                    downloadState = UpdateDownloadState.Failed("下载失败，请检查网络后重试")
                    return
                }

                else -> downloadState = UpdateDownloadState.Running(
                    p.downloaded, if (p.total > 0) p.total else info.fileSize
                )
            }
        }
    }

    private class Progress(val status: Int, val downloaded: Long, val total: Long)

    private fun readProgress(dm: DownloadManager, id: Long): Progress? =
        dm.query(DownloadManager.Query().setFilterById(id))?.use { c ->
            if (!c.moveToFirst()) return null
            val si = c.getColumnIndex(DownloadManager.COLUMN_STATUS)
            val di = c.getColumnIndex(DownloadManager.COLUMN_BYTES_DOWNLOADED_SO_FAR)
            val ti = c.getColumnIndex(DownloadManager.COLUMN_TOTAL_SIZE_BYTES)
            Progress(
                status = if (si >= 0) c.getInt(si) else DownloadManager.STATUS_RUNNING,
                downloaded = if (di >= 0) c.getLong(di) else 0L,
                total = if (ti >= 0) c.getLong(ti) else -1L
            )
        }

    /** 拉起系统安装器；Android 8+ 首次会要求允许本应用「安装未知应用」。 */
    private fun install(context: Context, file: File) {
        if (!file.exists()) {
            dialog = UpdateDialogState.Message("安装包丢失", "下载已完成但文件不存在，请重新检查更新。")
            return
        }
        runCatching {
            val uri = FileProvider.getUriForFile(context, "${context.packageName}.fileprovider", file)
            val intent = Intent(Intent.ACTION_VIEW).apply {
                setDataAndType(uri, "application/vnd.android.package-archive")
                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
            }
            context.startActivity(intent)
        }.onFailure { e ->
            dialog = UpdateDialogState.Message(
                "无法打开安装界面",
                "安装包已下载完成，但系统没能打开安装器。请确认已允许本应用安装未知应用后，再点一次检查更新。\n\n${e.message ?: ""}"
            )
        }
    }
}
