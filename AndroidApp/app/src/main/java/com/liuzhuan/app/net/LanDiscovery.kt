package com.liuzhuan.app.net

import java.net.DatagramPacket
import java.net.DatagramSocket
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

    // 发现端口固定，OFFER 中的控制面端口允许电脑自定义。
    const val DISCOVER_PORT = 8901
    private const val MAGIC = "LIUZHUAN_DISCOVER"
    private const val OFFER = "LIUZHUAN_OFFER"

    data class Server(val ip: String, val port: Int, val name: String,val deviceId:String="",val nonce:String="",val proof:String="",val encodedName:String="") {
        fun verified(authHash:String):Boolean = deviceId.isNotBlank() && IdentityProof.verify(authHash,nonce,deviceId,port,encodedName,proof)
    }

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
            val nonce=java.util.UUID.randomUUID().toString().replace("-","")
            val v2="LIUZHUAN_DISCOVER_V2|$nonce".toByteArray(Charsets.UTF_8)
            socket.send(DatagramPacket(v2,v2.size,broadcastAddr))

            // 单调时钟避免系统时间校准让搜索提前结束或持续不退。
            val started = System.nanoTime()
            val buf = ByteArray(1024)
            while (true) {
                currentCoroutineContext().ensureActive()
                val remainingMs = timeoutMs - (System.nanoTime() - started) / 1_000_000
                if (remainingMs <= 0) break
                socket.soTimeout = remainingMs.coerceIn(1, 150).toInt()
                try {
                    val pkt = DatagramPacket(buf, buf.size)
                    socket.receive(pkt)
                    val msg = String(pkt.data, 0, pkt.length, Charsets.UTF_8)
                    if(msg.startsWith("LIUZHUAN_OFFER_V2|")) {
                        val p=msg.split('|');val port=p.getOrNull(2)?.toIntOrNull()
                        if(!pkt.address.isAnyLocalAddress && !pkt.address.isMulticastAddress && p.size==6 && p[4]==nonce && port!=null && port in 1..65534 && runCatching {java.util.UUID.fromString(p[1]).toString()==p[1]}.getOrDefault(false)) {
                            val name=runCatching {String(java.util.Base64.getDecoder().decode(p[3]),Charsets.UTF_8)}.getOrNull()
                            if(name!=null && name.length in 1..64 && !name.any {it.isISOControl()} && results.size<64) {
                                val server=Server(pkt.address.hostAddress?:continue,port,name,p[1],nonce,p[5],p[3])
                                results.removeAll {it.ip==server.ip && it.port==server.port};results.add(server)
                            }
                        }
                        continue
                    }
                    if (msg.startsWith("$OFFER|")) {
                        val parts = msg.split("|", limit = 4)
                        val port = parts.getOrNull(2)?.toIntOrNull()
                        if (parts.size == 4 && port != null && port in 1..65534 && !pkt.address.isAnyLocalAddress && !pkt.address.isMulticastAddress) {
                            // 回包来源是实际可达的电脑地址；兼容旧服务端把虚拟网卡 IP 写入 OFFER 的情况。
                            val s = Server(pkt.address.hostAddress ?: continue, port, parts[3].take(120))
                            if (results.size<64 && results.none { it.ip == s.ip && it.port == s.port }) results.add(s)
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
