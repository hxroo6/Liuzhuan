package com.liuzhuan.app.audio

import org.json.JSONObject

/** Backwards-compatible material QR with an optional short-lived audio invitation. Never starts capture. */
object AudioPairing {
    fun microphone(content: String): String? {
        require(content.length <= 8192)
        val root = JSONObject(content)
        val mic = if (root.optString("kind") == AudioProtocol.KIND) root else root.optJSONObject("mic") ?: return null
        require(mic.getString("kind") == AudioProtocol.KIND && mic.getInt("v") == AudioProtocol.VERSION)
        val host = mic.getString("ip")
        if (mic !== root) require(host == root.getString("ip"))
        require(isPrivateV4(host) && mic.getInt("port") in 1024..65535)
        require(mic.getString("token").matches(Regex("[A-F0-9]{48}")))
        return mic.toString()
    }
    fun isPrivateV4(host: String): Boolean {
        val bytes = host.split('.').map { it.toIntOrNull() ?: return false }
        return bytes.size == 4 && bytes.all { it in 0..255 } &&
            (bytes[0] == 10 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 172 && bytes[1] in 16..31)
    }
}
