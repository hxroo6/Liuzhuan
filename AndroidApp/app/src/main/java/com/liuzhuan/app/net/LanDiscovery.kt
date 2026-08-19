package com.liuzhuan.app.net

import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.SocketTimeoutException

/**
 * UDP 自动发现 — 局域网内寻找电脑端流转
 * 协议：广播 LIUZHUAN_DISCOVER → 电脑端回 LIUZHUAN_OFFER|ip|port|设备名
 */
object LanDiscovery {

    const val DISCOVER_PORT = 8901
    private const val MAGIC = "LIUZHUAN_DISCOVER"
    private const val OFFER = "LIUZHUAN_OFFER"

    data class Server(val ip: String, val port: Int, val name: String)

    /** 广播探测，收集 3 秒内所有电脑端响应 */
    fun discover(timeoutMs: Long = 3000): List<Server> {
        val results = mutableListOf<Server>()
        val socket = DatagramSocket()
        try {
            socket.broadcast = true
            socket.soTimeout = 150
            val sendData = MAGIC.toByteArray(Charsets.UTF_8)
            val broadcastAddr = InetAddress.getByName("255.255.255.255")
            socket.send(DatagramPacket(sendData, sendData.size, broadcastAddr, DISCOVER_PORT))

            val end = System.currentTimeMillis() + timeoutMs
            val buf = ByteArray(1024)
            while (System.currentTimeMillis() < end) {
                try {
                    val pkt = DatagramPacket(buf, buf.size)
                    socket.receive(pkt)
                    val msg = String(pkt.data, 0, pkt.length, Charsets.UTF_8)
                    if (msg.startsWith(OFFER)) {
                        val parts = msg.split("|")
                        if (parts.size >= 4) {
                            val s = Server(parts[1], parts[2].toIntOrNull() ?: 8899, parts[3])
                            if (results.none { it.ip == s.ip }) results.add(s)
                        }
                    }
                } catch (e: SocketTimeoutException) {
                    // 单次超时继续收，直到总超时
                }
            }
        } finally {
            socket.close()
        }
        return results
    }
}
