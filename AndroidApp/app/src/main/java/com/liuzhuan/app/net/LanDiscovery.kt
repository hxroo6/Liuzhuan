package com.liuzhuan.app.net

import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.SocketTimeoutException
import java.net.InetSocketAddress
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive

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
    suspend fun discover(timeoutMs: Long = 3000): List<Server> = discoverAt(timeoutMs, "255.255.255.255", DISCOVER_PORT)

    internal suspend fun discoverAt(timeoutMs: Long, destination: String, discoveryPort: Int): List<Server> = withContext(Dispatchers.IO) {
        val results = mutableListOf<Server>()
        require(timeoutMs > 0)
        DatagramSocket().use { socket ->
            socket.broadcast = true
            socket.soTimeout = 150
            val sendData = MAGIC.toByteArray(Charsets.UTF_8)
            currentCoroutineContext().ensureActive()
            val broadcastAddr = InetSocketAddress(destination, discoveryPort)
            socket.send(DatagramPacket(sendData, sendData.size, broadcastAddr))

            val end = System.currentTimeMillis() + timeoutMs
            val buf = ByteArray(1024)
            while (System.currentTimeMillis() < end) {
                currentCoroutineContext().ensureActive()
                try {
                    val pkt = DatagramPacket(buf, buf.size)
                    socket.receive(pkt)
                    val msg = String(pkt.data, 0, pkt.length, Charsets.UTF_8)
                    if (msg.startsWith("$OFFER|")) {
                        val parts = msg.split("|", limit = 4)
                        val port = parts.getOrNull(2)?.toIntOrNull()
                        if (parts.size == 4 && port != null && port in 1..65534 && !pkt.address.isAnyLocalAddress && !pkt.address.isMulticastAddress) {
                            // 回包来源是实际可达的电脑地址；兼容旧服务端把虚拟网卡 IP 写入 OFFER 的情况。
                            val s = Server(pkt.address.hostAddress ?: continue, port, parts[3].take(120))
                            if (results.none { it.ip == s.ip && it.port == s.port }) results.add(s)
                        }
                    }
                } catch (e: SocketTimeoutException) {
                    // 单次超时继续收，直到总超时
                }
            }
        }
        results
    }
}
