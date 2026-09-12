import com.liuzhuan.app.net.LanDiscovery
import kotlinx.coroutines.*
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.util.concurrent.Executors
import kotlin.concurrent.thread

fun main() {
    val ui=Executors.newSingleThreadExecutor { Thread(it,"test-ui") }.asCoroutineDispatcher()
    ui.use {
        runBlocking(ui) {
            DatagramSocket(0,InetAddress.getLoopbackAddress()).use { server ->
                server.soTimeout=3000
                val responder=thread {
                    val packet=DatagramPacket(ByteArray(1024),1024);server.receive(packet)
                    for(message in listOf("LIUZHUAN_OFFER|bad|oops|invalid", "LIUZHUAN_OFFER|172.30.201.230|8899|Test PC", "LIUZHUAN_OFFER|172.30.201.230|8899|Test PC")) {
                        val bytes=message.toByteArray();server.send(DatagramPacket(bytes,bytes.size,packet.socketAddress))
                    }
                }
                var ticks=0
                val ticker=launch { repeat(10) { delay(10);ticks++ } }
                val found=LanDiscovery.discoverAt(400,"127.0.0.1",server.localPort)
                check(ticks>2) { "Discovery blocked UI event loop" }
                check(found.size==1 && found[0].ip=="127.0.0.1" && found[0].port==8899)
                ticker.join();responder.join()
                println("PASS UI remains responsive; malformed and duplicate offers ignored; reply source beats stale advertised IP")
            }
            DatagramSocket(0,InetAddress.getLoopbackAddress()).use { silent ->
                val started=System.nanoTime()
                try { withTimeout(100) { LanDiscovery.discoverAt(5000,"127.0.0.1",silent.localPort) };error("Cancellation missing") }
                catch(_:TimeoutCancellationException) { }
                check((System.nanoTime()-started)/1_000_000<1500)
                silent.soTimeout=1000
                val packet=DatagramPacket(ByteArray(1024),1024);silent.receive(packet)
                DatagramSocket(packet.port).close()
                println("PASS cancellation is bounded and releases discovery socket")
            }
            var caught=false
            try { LanDiscovery.discoverAt(100,"127.0.0.1",-1) } catch(_:IllegalArgumentException) { caught=true }
            check(caught)
            println("PASS discovery failures propagate to the caller for UI error feedback")
        }
    }
}
