package com.teatt.pocket.feature.eat

import android.content.Context
import android.content.SharedPreferences

/**
 * 记录已经提示过的任务 id。
 *
 * 启动时的「你有新任务」弹窗只对没看过的任务弹一次；自己选/抽出来的任务
 * 也会立刻记为已看过，避免下次启动时重复打扰。
 */
object EatTaskSeen {
    private const val PREFS = "pocket_eat"
    private const val KEY_TASK_ID = "seen_task_id"

    @Volatile
    private var prefs: SharedPreferences? = null

    private fun sp(context: Context): SharedPreferences =
        prefs ?: context.applicationContext
            .getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .also { prefs = it }

    fun isSeen(context: Context, taskId: Int): Boolean =
        taskId != 0 && sp(context).getInt(KEY_TASK_ID, 0) == taskId

    fun markSeen(context: Context, taskId: Int) {
        if (taskId != 0) sp(context).edit().putInt(KEY_TASK_ID, taskId).apply()
    }
}
