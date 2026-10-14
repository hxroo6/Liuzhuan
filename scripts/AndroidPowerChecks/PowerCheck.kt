package com.liuzhuan.checks

import com.liuzhuan.app.TransferTasks
import com.liuzhuan.app.TransferWork
import com.liuzhuan.app.core.*
import com.liuzhuan.app.net.HttpClients
import com.liuzhuan.app.net.RetryBackoff
import kotlinx.coroutines.*
import okhttp3.Request
import java.io.InputStream
import java.net.InetSocketAddress
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import com.sun.net.httpserver.HttpServer

private var checks = 0
private fun verify(value: Boolean, description: String) { check(value) { description }; checks++; println("PASS $description") }
private suspend fun eventually(block: () -> Boolean) { withTimeout(5_000) { while (!block()) delay(10) } }

fun main() = runBlocking {
    val availability = mutableListOf<Boolean>()
    val routes = LanRouteState<String> { availability.add(it) }
    routes.publish()
    verify(availability == listOf(false), "no LAN or hotspot does not attempt mobile-data reconnection")
    routes.updateNetwork("cellular",false)
    verify(availability == listOf(false), "cellular-only updates do not enable LAN or emit duplicates")
    routes.updateWifiHotspot(true)
    verify(availability.last(), "phone hosting a Wi-Fi hotspot can connect despite cellular-only upstream")
    routes.updateNetwork("wifi",true)
    routes.removeNetwork("wifi")
    verify(availability == listOf(false,true), "Wi-Fi client teardown cannot disconnect an active hotspot")
    routes.updateWifiHotspot(false)
    verify(availability == listOf(false,true,false), "stopping hotspot with only cellular remaining suspends reconnect")
    routes.updateNetwork("wifi",true)
    routes.updateWifiHotspot(true)
    routes.updateWifiHotspot(false)
    verify(availability.last() && availability.size==4, "stopping hotspot does not interrupt an existing Wi-Fi client route")
    routes.updateNetwork("wifi",false)
    verify(!availability.last(), "transport capability loss removes LAN availability")
    routes.updateNetwork("ethernet",true)
    routes.updateNetwork("vpn",true)
    routes.removeNetwork("ethernet")
    verify(availability.last(), "removing one LAN preserves another supported route")
    routes.removeNetwork("vpn")
    routes.removeNetwork("unknown")
    routes.updateWifiHotspot(false)
    verify(!availability.last() && availability.takeLast(2)==listOf(true,false), "last route loss emits once; duplicate teardown stays quiet")

    val backoff = RetryBackoff()
    verify(List(8) { backoff.nextDelayMs() } == listOf(1000L,2000L,4000L,8000L,16000L,30000L,30000L,30000L), "consecutive failures retain exponential backoff across scheduled jobs")
    verify((1..100_000).all { backoff.nextDelayMs() == 30_000L }, "long offline period cannot wrap the retry shift or create negative delays")
    backoff.reset(); verify(backoff.nextDelayMs() == 1000L, "successful connection or explicit new target can reset retry budget")

    verify(KeepAlivePolicy.decide(false, true, 0, 0, 100).hold, "instant mode retains background delivery baseline")
    verify(!KeepAlivePolicy.decide(true, true, 0, 0, 100).hold, "balanced idle connection does not require an always-on lock")
    verify(KeepAlivePolicy.decide(true, true, 0, 130, 100).releaseAfterMs == 30L, "balanced work window releases at its deadline")
    verify(!KeepAlivePolicy.decide(true, true, 0, 130, 130).hold, "deadline equality releases an expired lease")
    verify(KeepAlivePolicy.decide(true, true, 1, 0, 100).hold, "long-running transfer remains protected after its initial work window")
    verify(!KeepAlivePolicy.decide(false, false, 2, 9999, 100).hold, "paused connection does not retain locks even with stale work signals")

    var consumed = 0
    val huge = object : InputStream() {
        override fun read(): Int { consumed++; return 1 }
        override fun read(buffer: ByteArray, off: Int, len: Int): Int { consumed += len; buffer.fill(1,off,off+len); return len }
    }
    verify(huge.readWithinLimit(512*1024) == null && consumed == 512*1024+1, "huge source is rejected after limit+1 bytes, without reading the rest")
    verify(byteArrayOf(1,2,3).inputStream().readWithinLimit(3)?.contentEquals(byteArrayOf(1,2,3)) == true, "file exactly at the limit is retained intact")
    verify(byteArrayOf().inputStream().readWithinLimit(0)?.isEmpty() == true, "zero-length stream is handled without a spin loop")
    val zeroRead = object : InputStream() {
        var left=3
        override fun read(buffer:ByteArray,off:Int,len:Int)=0
        override fun read():Int=if(left-- > 0) 7 else -1
    }
    verify(zeroRead.readWithinLimit(3)?.size == 3, "provider returning zero-length reads cannot spin forever")
    verify(byteArrayOf(0,15,16,127,-128,-1).hexString() == "000f107f80ff", "allocation-light hex conversion preserves protocol digest bytes")

    var now=100L
    val recent=RecentFileEvents { now }
    verify(recent.accept("app|revision|file") && !recent.accept("app|revision|file"), "repeated file broadcast within short window is suppressed")
    verify(recent.accept("other-app|revision|file"), "different source retains its own copy semantics")
    now+=2_000
    verify(recent.accept("app|revision|file"), "intentional later file copy is accepted after bounded dedup window")

    TransferTasks.start("progress", "upload", "progress-test")
    TransferTasks.progress("progress-test",1,1_000_000)
    val unchanged=TransferTasks.tasks.value
    repeat(100) { TransferTasks.progress("progress-test",it+2L,1_000_000) }
    verify(TransferTasks.tasks.value === unchanged, "throttled chunks do not replace or allocate the published task list")
    TransferTasks.progress("progress-test",1_000_000,1_000_000)
    verify(TransferTasks.tasks.value.first().done == 1_000_000L, "final progress is not throttled")
    var discarded=0
    TransferTasks.start("cached", "upload", "cache-test", discard={discarded++})
    TransferTasks.finish("cache-test",false,"offline")
    verify(discarded==0, "failed cached payload remains available for retry")
    TransferTasks.finish("cache-test",true,"stored")
    verify(discarded==1, "successful cached payload is released exactly once")

    val gates=CompletableDeferred<Unit>()
    val entered=AtomicInteger()
    repeat(2) { index ->
        val id="slot-$index"
        TransferTasks.start(id,"upload",id,cancel={TransferWork.cancel(id)})
        TransferWork.launch(id) { entered.incrementAndGet(); gates.await(); TransferTasks.finish(id,true,"stored") }
    }
    eventually { entered.get()==2 }
    TransferTasks.start("slot-2","upload","slot-2",cancel={TransferWork.cancel("slot-2")})
    TransferWork.launch("slot-2") { entered.incrementAndGet(); gates.await(); TransferTasks.finish("slot-2",true,"stored") }
    delay(100)
    verify(entered.get()==2 && TransferWork.active.value==2, "file work is limited to two active transfers")
    TransferWork.cancel("slot-2")
    eventually { !TransferTasks.running("slot-2") }
    verify(entered.get()==2, "canceling queued transfer does not start its I/O")
    gates.complete(Unit)
    eventually { TransferWork.active.value==0 }
    verify(!TransferTasks.running("slot-0") && !TransferTasks.running("slot-1"), "completed transfers release both slots")
    val oldCleanup=CompletableDeferred<Unit>()
    val terminalVisible=CompletableDeferred<Unit>()
    val retryEntered=AtomicInteger()
    TransferTasks.start("retry race", "upload", "retry-cleanup")
    TransferWork.launch("retry-cleanup") {
        TransferTasks.finish("retry-cleanup",false,"offline")
        terminalVisible.complete(Unit)
        oldCleanup.await()
    }
    terminalVisible.await()
    TransferTasks.start("retry race", "upload", "retry-cleanup")
    TransferWork.launch("retry-cleanup") { retryEntered.incrementAndGet(); TransferTasks.finish("retry-cleanup",true,"stored") }
    delay(50)
    verify(retryEntered.get()==0, "explicit retry waits for previous I/O cleanup instead of overlapping it")
    oldCleanup.complete(Unit)
    eventually { retryEntered.get()==1 && !TransferTasks.running("retry-cleanup") }
    verify(!TransferTasks.tasks.value.first { it.id=="retry-cleanup" }.failed, "retry submitted before old completion is preserved and finishes successfully")

    val streamEntered=CountDownLatch(1)
    val streamClosed=CountDownLatch(1)
    val source=object: InputStream() {
        override fun read():Int { streamEntered.countDown(); streamClosed.await(5,TimeUnit.SECONDS); throw java.io.IOException("closed source") }
        override fun close() { streamClosed.countDown() }
    }
    TransferTasks.start("source URI", "upload", "cancel-source")
    TransferWork.launch("cancel-source") { TransferWork.useResource("cancel-source",source) { it.read() } }
    verify(streamEntered.await(3,TimeUnit.SECONDS), "source stream is open before cancellation")
    TransferWork.cancel("cancel-source")
    eventually { !TransferTasks.running("cancel-source") && TransferWork.active.value==0 }
    verify(streamClosed.count==0L, "cancel closes a source stream even before an HTTP call exists")

    val server=HttpServer.create(InetSocketAddress("127.0.0.1",0),0)
    val requestEntered=CountDownLatch(1)
    val releaseServer=CountDownLatch(1)
    server.executor=java.util.concurrent.Executors.newCachedThreadPool { action -> Thread(action).apply { isDaemon=true } }
    server.createContext("/stall") { exchange ->
        try {
            exchange.sendResponseHeaders(200,1)
            requestEntered.countDown()
            releaseServer.await(5,TimeUnit.SECONDS)
            exchange.responseBody.write(byteArrayOf(1))
        } catch(_:Exception) {} finally { exchange.close() }
    }
    server.start()
    try {
        var cleaned=false
        val id="cancel-http"
        TransferTasks.start("blocked response", "download", id,cancel={TransferWork.cancel(id)})
        TransferWork.launch(id) {
            try {
                val call=HttpClients.transfer.newCall(Request.Builder().url("http://127.0.0.1:${server.address.port}/stall").build())
                TransferWork.withCall(id,call) { it.execute().use { response -> response.body!!.byteStream().read() } }
            } finally { cleaned=true }
        }
        verify(requestEntered.await(3,TimeUnit.SECONDS), "real HTTP body starts before cancellation")
        TransferTasks.cancel(id)
        eventually { !TransferTasks.running(id) && TransferWork.active.value==0 }
        verify(cleaned && TransferTasks.tasks.value.first().cancelled && !TransferTasks.tasks.value.first().failed, "cancel interrupts a blocked HTTP body and reports cancellation after cleanup")
    } finally { releaseServer.countDown(); server.stop(0) }
    val pauseGate=CompletableDeferred<Unit>()
    var pauseDiscarded=0
    var resumed=0
    fun startPausedTask() {
        TransferTasks.start("pause file","upload","pause-control",discard={pauseDiscarded++},cancel={TransferWork.cancel("pause-control")},retry={resumed++;startPausedTask()})
        TransferWork.launch("pause-control") {pauseGate.await();TransferTasks.finish("pause-control",true,"stored")}
    }
    startPausedTask()
    eventually {TransferWork.active.value==1}
    TransferTasks.pause("pause-control")
    eventually {TransferWork.active.value==0 && !TransferTasks.running("pause-control")}
    verify(TransferTasks.tasks.value.first().paused && !TransferTasks.tasks.value.first().failed && pauseDiscarded==0,"pause releases its slot and retains retry source without reporting failure")
    TransferTasks.progress("pause-control",99,100)
    verify(TransferTasks.tasks.value.first().done==0L,"late progress cannot overwrite paused task state")
    TransferTasks.resume("pause-control")
    eventually {TransferWork.active.value==1}
    TransferTasks.resume("pause-control")
    verify(resumed==1 && !TransferTasks.tasks.value.first().paused,"continue starts one full retry; duplicate continue cannot create another transfer")
    TransferTasks.pause("pause-control")
    eventually {TransferWork.active.value==0 && !TransferTasks.running("pause-control")}
    TransferTasks.cancel("pause-control")
    verify(TransferTasks.tasks.value.first().cancelled && !TransferTasks.canRetry("pause-control") && pauseDiscarded==1,"canceling paused work releases cache and removes continue action")
    TransferTasks.resume("pause-control")
    verify(resumed==1,"a canceled task cannot be resumed accidentally")
    TransferTasks.start("completion race","upload","completion-race",discard={pauseDiscarded++},cancel={},retry={})
    TransferTasks.pause("completion-race")
    TransferTasks.finish("completion-race",true,"stored")
    verify(!TransferTasks.tasks.value.first().paused && TransferTasks.tasks.value.first().state=="stored","actual confirmed completion wins a concurrent pause request")
    TransferTasks.savedTo("completion-race","Pictures/Liuzhuan")
    verify(TransferTasks.tasks.value.first().savedFolder=="Pictures/Liuzhuan","saved directory remains attached to the completed task")
    println("All $checks Android power and transfer checks passed.")
}
