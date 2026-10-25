package com.teatt.pocket.feature.wireguard

/**
 * WireGuard 配置辅助工具。
 *
 * 移植自「神秘WiFi」WireGuardConfigHelper.kt：解析配置文本、读取/改写 Endpoint。
 */
object WgConfigHelper {

    /**
     * 修改 [Peer] 段的 Endpoint。
     *
     * 服务端下发的配置里 Endpoint 指向服务器 IP:53（UDP 53 便于穿墙），
     * 万一以后换端口，可以在这里改写而不必让服务端重新签发。
     */
    fun modifyEndpoint(configText: String, serverIp: String, serverPort: String): String {
        val lines = configText.lines().toMutableList()
        var endpointFound = false

        for (i in lines.indices) {
            val trimmed = lines[i].trim()
            if (trimmed.startsWith("#") || trimmed.startsWith(";")) continue

            if (trimmed.startsWith("Endpoint", ignoreCase = true)) {
                val eq = trimmed.indexOf('=')
                if (eq >= 0) {
                    lines[i] = "Endpoint = $serverIp:$serverPort"
                    endpointFound = true
                    break
                }
            }
        }

        if (!endpointFound) {
            var peerIndex = -1
            for (i in lines.indices) {
                if (lines[i].trim().equals("[Peer]", ignoreCase = true)) {
                    peerIndex = i
                    break
                }
            }
            if (peerIndex >= 0) {
                lines.add(peerIndex + 1, "Endpoint = $serverIp:$serverPort")
            } else {
                lines.add("")
                lines.add("[Peer]")
                lines.add("Endpoint = $serverIp:$serverPort")
            }
        }

        return lines.joinToString("\n")
    }

    /** 取出当前 Endpoint（如 "1.2.3.4:53"），没有则返回 null。 */
    fun extractEndpoint(configText: String): String? {
        for (line in configText.lines()) {
            val trimmed = line.trim()
            if (trimmed.startsWith("Endpoint", ignoreCase = true)) {
                val eq = trimmed.indexOf('=')
                if (eq >= 0) return trimmed.substring(eq + 1).trim()
            }
        }
        return null
    }

    /** 取出 [Interface] 的 PrivateKey。 */
    fun extractPrivateKey(configText: String): String? {
        var inInterface = false
        for (line in configText.lines()) {
            val trimmed = line.trim()
            if (trimmed.equals("[Interface]", ignoreCase = true)) {
                inInterface = true
                continue
            }
            if (trimmed.startsWith("[") && !trimmed.equals("[Interface]", ignoreCase = true)) {
                inInterface = false
            }
            if (inInterface && trimmed.startsWith("PrivateKey", ignoreCase = true)) {
                val eq = trimmed.indexOf('=')
                if (eq >= 0) return trimmed.substring(eq + 1).trim()
            }
        }
        return null
    }

    /** 取出 [Peer] 的 PublicKey。 */
    fun extractPublicKey(configText: String): String? {
        var inPeer = false
        for (line in configText.lines()) {
            val trimmed = line.trim()
            if (trimmed.equals("[Peer]", ignoreCase = true)) {
                inPeer = true
                continue
            }
            if (trimmed.startsWith("[") && !trimmed.equals("[Peer]", ignoreCase = true)) {
                inPeer = false
            }
            if (inPeer && trimmed.startsWith("PublicKey", ignoreCase = true)) {
                val eq = trimmed.indexOf('=')
                if (eq >= 0) return trimmed.substring(eq + 1).trim()
            }
        }
        return null
    }
}
