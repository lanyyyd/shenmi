package com.teatt.pocket.feature.wireguard

import com.wireguard.android.backend.Tunnel

/**
 * WireGuard 隧道实例。
 *
 * 移植自「神秘WiFi」WuweiTunnel.kt：实现 Tunnel 接口，封装隧道名与状态，
 * 底层状态变化时通过回调通知上层。
 */
class WgTunnel(
    private val name: String,
    private val onStateChanged: (Tunnel.State) -> Unit
) : Tunnel {

    var state: Tunnel.State = Tunnel.State.DOWN
        private set

    override fun getName(): String = name

    override fun onStateChange(newState: Tunnel.State) {
        state = newState
        onStateChanged.invoke(newState)
    }
}
