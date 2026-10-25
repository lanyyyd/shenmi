package com.teatt.pocket.core

import com.teatt.pocket.BuildConfig

/**
 * 版本号比较，规则与桌面端 UpdateService 保持一致：
 * 去掉 v 前缀与 -/+ 后缀，按 `.` 分段做数值比较，缺的段补 0。
 * 因此 1.0.13 > 1.0.9（字符串比较会判反）。
 */
object AppVersion {

    /** 构建时由 CI 注入的 versionName，如 1.0.13。 */
    val current: String get() = BuildConfig.VERSION_NAME

    fun isNewer(latest: String, current: String = this.current): Boolean {
        val a = parse(latest)
        val b = parse(current)
        if (a.isEmpty()) return false
        val n = maxOf(a.size, b.size)
        for (i in 0 until n) {
            val x = a.getOrElse(i) { 0 }
            val y = b.getOrElse(i) { 0 }
            if (x != y) return x > y
        }
        return false
    }

    private fun parse(value: String): List<Int> {
        var v = value.trim()
        if (v.startsWith("v") || v.startsWith("V")) v = v.substring(1)
        v = v.substringBefore('-').substringBefore('+')
        if (v.isBlank()) return emptyList()
        return v.split('.').mapNotNull { it.trim().toIntOrNull() }
    }
}
