import android.content.Context
import com.liuzhuan.app.LanHub
import com.liuzhuan.app.TransferTasks
import com.liuzhuan.app.clipboard.*
import com.liuzhuan.app.core.SettingsStore
import com.liuzhuan.app.net.LanClient
import com.liuzhuan.app.net.Proto
import kotlinx.coroutines.*
import java.util.concurrent.CopyOnWriteArrayList
import kotlin.coroutines.CoroutineContext

private var checks = 0
private fun verify(ok: Boolean, label: String) { check(ok) { label }; checks++; println("PASS $label") }
private fun copyEvent(at: Long, text: String = "isolated copy", pkg: String? = "com.allowed.app") = ClipboardEvent(text, at, pkg,
    ClipboardCaptureSource.XPOSED_HOOK, listOf("text/plain"), fingerprintOf(text, listOf("text/plain")), diagnosticId = "COPY-test")
private suspend fun eventually(test: () -> Boolean) { withTimeout(5000) { while (!test()) delay(10) } }

private class HeldDispatcher : CoroutineDispatcher() {
    private val pending = java.util.concurrent.ConcurrentLinkedQueue<Runnable>()
    override fun dispatch(context: CoroutineContext, block: Runnable) { pending.add(block) }
    fun release() { while (true) (pending.poll() ?: break).run() }
}

private class Fixture(capable: Boolean = true) {
    var at = 10_000L
    val policy = ClipboardPrivacyPolicy { at }.apply { initialize(ClipboardPrivacyPolicy.State(automaticEnabled = true)) }
    val queue = ClipboardEventQueue(now = { at })
    val sent = mutableListOf<ClipboardEvent>()
    val diagnostics = mutableListOf<String>()
    var accepted = true
    var connected = true
    var capability = capable
    var durable=false
    var identity = "pc-one"
    var onSend: (ClipboardEvent) -> Unit = {}
    val inactiveScope = CoroutineScope(SupervisorJob().apply { cancel() } + Dispatchers.Default)
    val transport = object : AutomaticClipboardDelivery.Transport {
        override val identity get() = this@Fixture.identity
        override val supportsReceipts get() = capability
        override val durableReceipts get()=durable
        override fun send(event: ClipboardEvent): Boolean { sent.add(event); onSend(event); return accepted }
    }
    val delivery = AutomaticClipboardDelivery(queue, { if (connected) transport else null }, policy::runIfAllowed,
        { _, message -> diagnostics.add(message) }, inactiveScope, { at }, receiptTimeoutMs = 20, retryDelayMs = 1)
    fun submit(text: String = "isolated copy", pkg: String? = "com.allowed.app"): ClipboardEvent =
        copyEvent(at, text, pkg).let { policy.stampIfAllowed(it) ?: it }.also { delivery.submit(it) }
    fun tick(increment: Long = 0) { at += increment; delivery.step() }
}

fun runChecks(args: Array<String>): Unit = runBlocking {
    val upgradedQueue=ClipboardEventQueue(now={1000L})
    val upgrading=copyEvent(1000,"fixture upgrade")
    upgradedQueue.enqueue(upgrading,"old-endpoint")
    upgradedQueue.update(upgrading.requestId){it.copy(attempts=1,mayHaveRegistered=true)}
    upgradedQueue.rebindVerifiedTarget("old-endpoint","stable-computer")
    verify(upgradedQueue.find(upgrading.requestId)?.let{it.target=="stable-computer" && it.attempts==1 && it.mayHaveRegistered}==true,"verified identity upgrade preserves original request and registration uncertainty")
    upgradedQueue.changeTarget("stable-computer")
    verify(upgradedQueue.find(upgrading.requestId)!=null,"upgraded outbox survives reconnect to same stable target")
    var disk=emptyList<ClipboardEventQueue.Pending>()
    val durable=Fixture();durable.durable=true
    durable.queue.attachPersistence(emptyList()){disk=it.toList()}
    val committed=durable.submit("durable-outbox-fixture")
    durable.onSend={verify(disk.single().event.requestId==committed.requestId && disk.single().awaitingReceipt && disk.single().mayHaveRegistered,"send intent and stable request ID are persisted before network submission")}
    durable.tick()
    val restored=Fixture();restored.at=durable.at+21;restored.durable=true
    restored.queue.attachPersistence(disk){disk=it.toList()};restored.tick();restored.tick(2)
    verify(restored.sent.single().requestId==committed.requestId,"fresh delivery actor restores and retries the original durable request")
    val legacyRecovery=Fixture();legacyRecovery.at=restored.at+30
    legacyRecovery.queue.attachPersistence(disk){};legacyRecovery.tick();legacyRecovery.tick(3)
    verify(legacyRecovery.sent.isEmpty() && legacyRecovery.queue.size()==0,"restored possibly submitted text is not replayed against a legacy receipt store")
    val noDisk=Fixture();noDisk.queue.attachPersistence(emptyList()){}
    val unsaved=noDisk.submit("write-failure")
    noDisk.queue.attachPersistence(noDisk.queue.snapshot()){if(it.any {p->p.awaitingReceipt})throw java.io.IOException("injected storage failure")}
    runCatching {noDisk.tick()}
    verify(noDisk.sent.isEmpty() && noDisk.queue.find(unsaved.requestId)!=null,"storage failure cannot submit an unjournaled request")
    val expiredRestore=Fixture();expiredRestore.at=ClipboardEventQueue.MAX_AGE_MS+50_000
    expiredRestore.queue.attachPersistence(listOf(ClipboardEventQueue.Pending(copyEvent(10_000),10_000,20_000,target="pc-one"))){}
    verify(expiredRestore.queue.size()==0,"expired automatic content is pruned during restoration")
    val privateRestore=Fixture();privateRestore.durable=true;privateRestore.queue.attachPersistence(disk){};privateRestore.policy.pause();privateRestore.tick()
    verify(privateRestore.sent.isEmpty() && privateRestore.queue.size()==0,"restored automatic content still passes current privacy rules before replay")
    val manualHold=Fixture();manualHold.durable=true
    val manualEvent=copyEvent(manualHold.at,"explicit durable text").copy(source=ClipboardCaptureSource.MANUAL)
    manualHold.delivery.submit(manualEvent);manualHold.tick();repeat(2){manualHold.tick(20);manualHold.tick(3)};manualHold.tick(20)
    verify(manualHold.queue.find(manualEvent.requestId)?.held==true && manualHold.queue.head()==null,"manual text remains durable for explicit retry after automatic retry budget is exhausted")
    val later=manualHold.submit("later automatic event");manualHold.tick()
    verify(manualHold.sent.last().requestId==later.requestId,"held manual request does not block later eligible messages")
    manualHold.tick(ClipboardEventQueue.MANUAL_MAX_AGE_MS)
    verify(manualHold.queue.find(manualEvent.requestId)==null,"held manual request expires during the same process lifetime without a restart or retry")

    val f = Fixture()
    val first = f.submit(); f.tick()
    verify(f.sent.size == 1 && f.queue.size() == 1 && f.diagnostics.last().contains("等待"), "accepted network send remains pending and cannot report registration success")
    f.tick(20); f.tick(1)
    verify(f.sent.size == 2 && f.sent.all { it.requestId == first.requestId }, "lost acknowledgement retries with the stable original request identity")
    f.delivery.acknowledge("wrong-pc", first.requestId, "stored")
    verify(f.queue.size() == 1, "another endpoint cannot acknowledge this request")
    f.delivery.acknowledge("pc-one", first.requestId, "stored")
    verify(f.queue.size() == 0 && f.diagnostics.last() == "电脑已登记文字", "only a matching stored receipt completes the pending copy")
    val confirmedDiagnostics = f.diagnostics.toList(); f.delivery.acknowledge("pc-one", first.requestId, "stored")
    verify(f.diagnostics == confirmedDiagnostics, "duplicate late acknowledgement cannot overwrite completed diagnostics")

    val bounded = Fixture(); bounded.submit(); bounded.tick(); repeat(2) { bounded.tick(20); bounded.tick(3) }; bounded.tick(20)
    verify(bounded.sent.size == 3 && bounded.queue.size() == 0 && bounded.diagnostics.last().contains("三次"), "three unconfirmed submissions stop retrying with a visible final diagnostic")
    val failed = Fixture(); failed.accepted = false; failed.submit(); repeat(4) { failed.tick(4) }
    verify(failed.sent.size == 3 && failed.queue.size() == 0 && failed.diagnostics.last().contains("停止"), "immediate send failures are retained and retried a bounded number of times")
    val old = Fixture(false); val oldEvent = old.submit(); old.tick(); old.tick(20); old.tick(100)
    verify(old.sent.size == 1 && old.queue.size() == 0 && old.diagnostics.last().contains("旧版"), "legacy server without capability is never blindly resent an accepted message")
    val oldAck = Fixture(false); val oldAckEvent = oldAck.submit(); oldAck.tick(); oldAck.delivery.acknowledge("pc-one", oldAckEvent.requestId, "stored")
    verify(oldAck.queue.size() == 0 && oldAck.diagnostics.last() == "电脑已登记文字", "legacy server's existing stored acknowledgement can still confirm the single submission")
    val downgraded = Fixture(); downgraded.submit(); downgraded.tick(); downgraded.tick(20); downgraded.capability = false; downgraded.tick(1)
    verify(downgraded.sent.size == 1 && downgraded.queue.size() == 0, "reconnecting to an endpoint without deduplication capability cannot replay a possibly registered copy")

    val fifo = Fixture(); val a = fifo.submit("first"); val b = fifo.submit("second"); fifo.tick(); fifo.tick(10)
    verify(fifo.sent.map { it.requestId } == listOf(a.requestId), "FIFO keeps later copy pending while the earlier receipt is outstanding")
    fifo.delivery.acknowledge("pc-one", a.requestId, "stored"); fifo.tick()
    verify(fifo.sent.map { it.requestId } == listOf(a.requestId, b.requestId), "confirmed first copy releases the next queued copy")
    val repeated = Fixture(); val repeating = repeated.submit(); repeated.delivery.submit(repeating); repeated.tick()
    verify(repeated.queue.size() == 1 && repeated.sent.size == 1, "duplicate local submit of one request cannot create another queue entry")

    val full = Fixture(); repeat(51) { full.submit("copy $it") }
    verify(full.queue.size() == 50 && full.queue.head()?.event?.text == "copy 1" && full.diagnostics.any { it.contains("队列已满") }, "max fifty queue evicts the oldest with a visible diagnostic")
    val expired = Fixture(); expired.connected = false; val exp = expired.submit(); expired.tick(ClipboardEventQueue.MAX_AGE_MS)
    verify(expired.sent.isEmpty() && expired.queue.size() == 0 && expired.diagnostics.last().contains("超期"), "five-minute offline expiry cancels without submitting content")
    val expiredAck = Fixture(); val expAck = expiredAck.submit(); expiredAck.tick(); expiredAck.at += ClipboardEventQueue.MAX_AGE_MS
    expiredAck.delivery.acknowledge("pc-one", expAck.requestId, "stored")
    verify(expiredAck.queue.size() == 0 && expiredAck.diagnostics.last().contains("晚到"), "receipt arriving after the delivery deadline cannot falsely mark success")
    val target = Fixture(); target.delivery.changeTarget("pc-one"); val oldPc = target.submit(); target.tick(); target.identity = "pc-two"; target.delivery.changeTarget("pc-two"); target.tick(100)
    val targetDiagnostic = target.diagnostics.last(); target.delivery.acknowledge("pc-one", oldPc.requestId, "stored")
    verify(target.sent.size == 1 && target.queue.size() == 0 && target.diagnostics.last() == targetDiagnostic && targetDiagnostic.contains("变更"), "switching PC cancels old outbox and ignores its late receipt")
    val firstConnection = Fixture(); firstConnection.connected = false; firstConnection.submit(); firstConnection.delivery.changeTarget("pc-one"); firstConnection.connected = true; firstConnection.tick()
    verify(firstConnection.sent.size == 1, "copies captured before the first connection bind to that first chosen endpoint")
    val disconnected = Fixture(); val beforeDisconnect = disconnected.submit(); disconnected.tick(); disconnected.connected = false; disconnected.tick(20); disconnected.tick(5)
    verify(disconnected.sent.size == 1 && disconnected.queue.size() == 1, "unexpected disconnect keeps the pending request without consuming offline attempts")
    disconnected.connected = true; disconnected.tick()
    verify(disconnected.sent.size == 2 && disconnected.sent.last().requestId == beforeDisconnect.requestId, "reconnected receipt-capable transport retries the same request")

    val paused = Fixture(); val privateCopy = paused.submit(); paused.tick(); paused.policy.pause(); paused.queue.discardAutomatic()
    val cancelledMessage = paused.diagnostics.last(); paused.delivery.acknowledge("pc-one", privateCopy.requestId, "stored"); paused.policy.resume(); paused.tick(100)
    verify(paused.sent.size == 1 && paused.queue.size() == 0 && paused.diagnostics.last() == cancelledMessage && cancelledMessage.contains("取消"), "pause invalidates waiting data and its late ack cannot replace the cancellation diagnostic")
    val changed = Fixture(); val changedCopy = changed.submit(); changed.tick(); changed.policy.replaceExcludedPackages("com.allowed.app"); changed.delivery.acknowledge("pc-one", changedCopy.requestId, "stored"); changed.tick(100)
    verify(changed.sent.size == 1 && changed.queue.size() == 0 && changed.diagnostics.none { it == "电脑已登记文字" }, "source rule changes block both retry and a racing late acknowledgement")
    val unknown = Fixture(); unknown.policy.replaceExcludedPackages("com.secret.app"); unknown.at++; unknown.submit(pkg = null); unknown.tick()
    verify(unknown.queue.size() == 0 && unknown.sent.isEmpty(), "unknown source under exclusions is rejected before outbox insertion")
    val fresh = Fixture(); val captured = fresh.submit(); fresh.policy.pause(); fresh.policy.resume(); fresh.tick(1)
    verify(fresh.sent.isEmpty() && fresh.queue.size() == 0, "resume cannot revive a pre-rule-change pending event")
    val immediateAck = Fixture(); immediateAck.onSend = { immediateAck.delivery.acknowledge("pc-one", it.requestId, "stored") }; immediateAck.submit(); immediateAck.tick()
    verify(immediateAck.queue.size() == 0 && immediateAck.diagnostics.last() == "电脑已登记文字", "synchronous or very fast receipt is correlated before send returns and cannot revert to waiting")
    val rejection = Fixture(); val rejected = rejection.submit(); rejection.tick(); rejection.delivery.acknowledge("pc-one", rejected.requestId, "rejected"); rejection.tick(100)
    verify(rejection.sent.size == 1 && rejection.queue.size() == 0 && rejection.diagnostics.last().contains("拒绝"), "server rejection is final and does not automatically duplicate a possibly partial registration")
    val receiptRaceQueue=ClipboardEventQueue(now={10_000L})
    val receiptRaceEvent=copyEvent(10_000L,"receipt race fixture")
    receiptRaceQueue.enqueue(receiptRaceEvent,"pc-one");receiptRaceQueue.update(receiptRaceEvent.requestId){it.copy(attempts=1)}
    var receiptWrites=0
    val receiptRace=AutomaticClipboardDelivery(receiptRaceQueue,{null},{_,action->receiptRaceQueue.remove(receiptRaceEvent.requestId);action();true},{_,_->},
        CoroutineScope(SupervisorJob().apply{cancel()}+Dispatchers.Default),{10_000L})
    verify(!receiptRace.acknowledge("pc-one",receiptRaceEvent.requestId,"stored"){receiptWrites++} && receiptWrites==0,
        "cancellation racing receipt application cannot write a receipt or report completion")
    val receiptFailure=Fixture();val receiptFailureEvent=receiptFailure.submit();receiptFailure.tick()
    verify(runCatching {receiptFailure.delivery.acknowledge("pc-one",receiptFailureEvent.requestId,"stored"){throw java.io.IOException("fixture write failed")}}.isFailure &&
        receiptFailure.queue.find(receiptFailureEvent.requestId)!=null,"receipt journal failure retains the original pending request")

    val frame = org.json.JSONObject(Proto.buildClipboardPush("wire text", "com.allowed.app", "stable-wire", automatic = true))
    verify(frame.optString("id") == "stable-wire" && frame.optJSONObject("data")?.optString("content") == "wire text", "production Proto preserves the stable request identity and original text on the wire")

    // 下列连接使用真实生产 LanClient/OkHttp ↔ 真实生产 PC WsHub。
    val port = args.single()
    val context = Context(); ClipboardMonitorCoordinator.init(context); ClipboardPrivacy.migrateLegacyAutomaticEnabled(true)
    val held = HeldDispatcher(); val heldScope = CoroutineScope(SupervisorJob() + held)
    val uiReplies = CopyOnWriteArrayList<() -> Unit>(); var staleReplyEffects = 0
    val delayedClient = LanClient(scope = heldScope, onSessionItemData = { reply -> uiReplies.add { reply.apply {staleReplyEffects++} };Unit })
    LanHub.client = delayedClient
    val firstTargetSettings = SettingsStore.Settings(serverIp="127.0.0.1",serverPort=port,password="isolated-only",deviceName="session-target-A")
    val delayedRequest = "deferred-session-boundary"
    try {
        delayedClient.connect(firstTargetSettings);eventually {delayedClient.state==LanClient.State.Connected}
        delayedClient.requestItem("private-file");eventually {uiReplies.isNotEmpty()}
        verify(delayedClient.sendText("deferred boundary fixture",delayedRequest),"manual text accepts its original authenticated target before deferred work")
        ClipboardMonitorCoordinator.queue.persistenceReady=false
        delayedClient.connect(firstTargetSettings.copy(deviceName="session-target-B"));eventually {delayedClient.state==LanClient.State.Connected}
        val secondTarget=delayedClient.automaticClipboardTransport()!!.identity
        held.release();uiReplies.forEach {it()}
        val rebound=ClipboardMonitorCoordinator.queue.find(delayedRequest)?.target==secondTarget
        println("SESSION_BOUNDARY_OBSERVED reboundToNewTarget=$rebound staleReplyEffects=$staleReplyEffects")
        verify(!rebound,"deferred manual text cannot bind to a newly connected target")
        verify(staleReplyEffects==0,"old queued item reply cannot start a download after a session switch")
        delayedClient.requestItem("private-text");eventually {uiReplies.size==2}
        val currentReply:()->Unit=uiReplies[uiReplies.size-1];currentReply()
        verify(staleReplyEffects==1,"a current-session item reply still applies normally")
        val abaId="deferred-session-aba"
        ClipboardMonitorCoordinator.queue.persistenceReady=true
        verify(delayedClient.sendText("ABA boundary fixture",abaId),"ABA fixture accepts work before changing connection intents")
        ClipboardMonitorCoordinator.queue.persistenceReady=false
        delayedClient.connect(firstTargetSettings);eventually {delayedClient.state==LanClient.State.Connected}
        delayedClient.connect(firstTargetSettings.copy(deviceName="session-target-B"));eventually {delayedClient.state==LanClient.State.Connected}
        held.release()
        verify(ClipboardMonitorCoordinator.queue.find(abaId)==null && TransferTasks.tasks.value.any {it.id==abaId&&it.failed},
            "switching away and back cannot revive unpersisted work from an earlier connection intent")
        ClipboardMonitorCoordinator.queue.persistenceReady=true
        delayedClient.disconnect()
        val offlineId="same-target-offline"
        verify(delayedClient.sendText("offline original target fixture",offlineId),"offline text remains available after a prior authenticated connection")
        held.release()
        verify(ClipboardMonitorCoordinator.queue.find(offlineId)?.target==secondTarget,"offline text persists against its original target")
        delayedClient.connect(firstTargetSettings.copy(deviceName="session-target-B"));eventually {delayedClient.state==LanClient.State.Connected}
        eventually {ClipboardMonitorCoordinator.queue.find(offlineId)==null && TransferTasks.tasks.value.any {it.id==offlineId&&!it.failed&&!it.running}}
        verify(TransferTasks.tasks.value.any {it.id==offlineId&&it.state=="电脑已接收"},"same-computer reconnect still sends and confirms a previously persisted offline request")
    } finally {
        ClipboardMonitorCoordinator.queue.remove(delayedRequest)
        ClipboardMonitorCoordinator.queue.persistenceReady=true
        delayedClient.disconnect();heldScope.cancel()
    }
    val receivedDetails = CopyOnWriteArrayList<Proto.ItemData>()
    val liveClient = LanClient(onItemData = { receivedDetails.add(it) })
    LanHub.client = liveClient
    liveClient.connect(SettingsStore.Settings(serverIp = "127.0.0.1", serverPort = port, password = "isolated-only"))
    eventually { liveClient.state == LanClient.State.Connected }
    verify(liveClient.state == LanClient.State.Connected, "production Android client authenticates against the production PC websocket hub")
    liveClient.requestItem("registered-count");eventually {receivedDetails.isNotEmpty()}
    val registeredBeforeMain=receivedDetails.last().content.toInt()
    receivedDetails.clear()
    ClipboardEventDispatcher.dispatch(copyEvent(System.currentTimeMillis() + 10, "end-to-end automatic"))
    eventually { ClipboardMonitorCoordinator.diagnostic.value.contains("电脑已登记文字") }
    verify(ClipboardMonitorCoordinator.queue.size() == 0, "production dispatcher-action-outbox-LanClient chain completes only after the actual stored wire acknowledgement")
    liveClient.requestItem("registered-count"); eventually { receivedDetails.isNotEmpty() }
    verify(receivedDetails.last().content == (registeredBeforeMain+1).toString(), "real server registration callback executed once for the automatic client event")
    val manualId = "manual-real"
    liveClient.sendText("manual preserved", manualId); eventually { TransferTasks.tasks.value.any {it.id==manualId} && !TransferTasks.running(manualId) }
    verify(TransferTasks.tasks.value.any { it.id == manualId && !it.failed && it.state == "电脑已接收" }, "actual manual task still completes through its existing acknowledgement handling")
    val conflictStarted = System.nanoTime()
    liveClient.sendText("different manual content", manualId)
    withTimeout(2000) { while (TransferTasks.running(manualId)) delay(10) }
    verify(TransferTasks.tasks.value.any { it.id == manualId && it.failed && it.error.contains("电脑拒绝登记") && !it.error.contains("request_conflict") }
        && (System.nanoTime() - conflictStarted) / 1_000_000 < 2000,
        "real rejected manual acknowledgement fails the matching task immediately without exposing technical reason fields")
    val countBeforeConflictQuery = receivedDetails.size
    liveClient.requestItem("registered-count"); eventually { receivedDetails.size > countBeforeConflictQuery }
    verify(receivedDetails.last().content == (registeredBeforeMain+2).toString(), "conflicting real manual retry does not register another material on PC")
    ClipboardPrivacy.pause(); ClipboardEventDispatcher.dispatch(copyEvent(System.currentTimeMillis() + 10, "should not register"))
    delay(80)
    val countBeforePauseQuery = receivedDetails.size
    liveClient.requestItem("registered-count"); eventually { receivedDetails.size > countBeforePauseQuery }
    verify(receivedDetails.last().content == (registeredBeforeMain+2).toString(), "privacy pause prevents production automatic capture from submitting another network registration")

    val priorIdentity = liveClient.automaticClipboardTransport()!!.identity
    liveClient.disconnect()
    ClipboardPrivacy.resume()
    val awaitingPreviousDevice = ClipboardPrivacy.stampIfAllowed(copyEvent(System.currentTimeMillis() + 10, "pending old device name"))!!
    ClipboardMonitorCoordinator.queue.enqueue(awaitingPreviousDevice, priorIdentity)
    ClipboardMonitorCoordinator.queue.update(awaitingPreviousDevice.requestId) {
        it.copy(attempts = 1, awaitingReceipt = true, nextAt = System.currentTimeMillis() + 20_000, receiptCapable = true)
    }
    val currentProtocolLabel = Proto.deviceLabel
    liveClient.connect(SettingsStore.Settings(serverIp = "127.0.0.1", serverPort = port, password = "isolated-only", deviceName = "isolated-renamed"))
    eventually { liveClient.state == LanClient.State.Connected }
    val renamedIdentity = liveClient.automaticClipboardTransport()!!.identity
    verify(renamedIdentity != priorIdentity && Proto.deviceLabel == currentProtocolLabel,
        "production connection identity changes for deviceName alone at the same endpoint and password")
    verify(ClipboardMonitorCoordinator.queue.find(awaitingPreviousDevice.requestId) == null &&
        ClipboardMonitorCoordinator.diagnosticHistory.value.any { it.contains("旧请求已取消") },
        "changing saved device name cancels the old awaiting request rather than replaying under another PC deduplication scope")
    val countBeforeRenameQuery = receivedDetails.size
    liveClient.requestItem("registered-count"); eventually { receivedDetails.size > countBeforeRenameQuery }
    verify(receivedDetails.last().content == (registeredBeforeMain+2).toString(), "renamed production client does not create another server registration from the cancelled old request")
    val filePosts=java.util.concurrent.atomic.AtomicInteger()
    val failFileOnce=java.util.concurrent.atomic.AtomicBoolean(false)
    val uploadedBytes=CopyOnWriteArrayList<ByteArray>()
    val fileHost=com.sun.net.httpserver.HttpServer.create(java.net.InetSocketAddress("127.0.0.1",port.toInt()+1),0)
    fileHost.createContext("/upload") { exchange ->
        filePosts.incrementAndGet()
        uploadedBytes.add(exchange.requestBody.use { it.readBytes() })
        exchange.sendResponseHeaders(if(failFileOnce.getAndSet(false)) 500 else 200,-1)
        exchange.close()
    }
    fileHost.start()
    try {
        val gates=CompletableDeferred<Unit>()
        repeat(2) { index -> com.liuzhuan.app.TransferWork.launch("privacy-file-slot-$index") { gates.await() } }
        eventually { com.liuzhuan.app.TransferWork.active.value==2 }
        val blockedFile=java.io.File.createTempFile("queued-privacy", ".upload",context.cacheDir).apply { writeBytes(byteArrayOf(1,2,3)) }
        val blockedId="queued-file-privacy"
        val fileEvent=ClipboardPrivacy.stampIfAllowed(copyEvent(System.currentTimeMillis()+10))!!
        liveClient.pushFileCached(blockedFile,"sample.bin","application/octet-stream",blockedId,fileEvent)
        ClipboardPrivacy.pause()
        gates.complete(Unit)
        eventually { !TransferTasks.running(blockedId) }
        verify(filePosts.get()==0 && TransferTasks.tasks.value.first { it.id==blockedId }.failed,
            "production queued file rechecks privacy at actual network admission and cannot POST after pause")
        blockedFile.delete()
        ClipboardPrivacy.resume()
        val freshEvent=ClipboardPrivacy.stampIfAllowed(copyEvent(System.currentTimeMillis()+10))!!
        val file=java.io.File.createTempFile("cached-retry", ".upload",context.cacheDir).apply { writeBytes(byteArrayOf(4,5,6)) }
        val fileId="cached-file-retry"
        failFileOnce.set(true)
        liveClient.pushFileCached(file,"sample.bin","application/octet-stream",fileId,freshEvent)
        eventually { !TransferTasks.running(fileId) }
        verify(file.exists() && TransferTasks.canRetry(fileId), "failed real HTTP upload retains its cache path for explicit retry")
        TransferTasks.retry(fileId)
        eventually { !TransferTasks.running(fileId) }
        verify(!file.exists() && !TransferTasks.tasks.value.first { it.id==fileId }.failed,
            "successful explicit HTTP retry releases its cached payload")
        verify(uploadedBytes.size==2 && uploadedBytes.all { it.contentEquals(byteArrayOf(4,5,6)) },
            "cached upload and retry preserve raw file bytes on the actual HTTP wire")
    } finally { fileHost.stop(0) }
    val beforeIdentitySwitch=receivedDetails.size
    liveClient.requestItem("enable-stable-identity");eventually{receivedDetails.size>beforeIdentitySwitch}
    liveClient.disconnect()
    Proto.clientId="33333333-3333-4333-8333-333333333333"
    val authenticated=CopyOnWriteArrayList<SettingsStore.Settings>()
    val stableClient=LanClient(onConnected={authenticated.add(it)})
    val stableSettings=SettingsStore.Settings(serverIp="127.0.0.1",serverPort=port,password="isolated-only",expectedDeviceId="11111111-1111-4111-8111-111111111111")
    stableClient.connect(stableSettings);eventually{stableClient.state==LanClient.State.Connected && authenticated.size==1}
    val identityBefore=stableClient.automaticClipboardTransport()!!.identity
    verify(authenticated.single().expectedDeviceId==stableSettings.expectedDeviceId && authenticated.single().serverLibraryId.isNotBlank(),"authenticated stable IDs are exposed for history upgrade")
    stableClient.disconnect();stableClient.connect(stableSettings.copy(deviceName="renamed-fixture"))
    eventually{stableClient.state==LanClient.State.Connected && authenticated.size==2}
    verify(stableClient.automaticClipboardTransport()!!.identity==identityBefore,"stable outbox target survives friendly-name change")
    stableClient.disconnect()
    val wrongIdentity=LanClient()
    wrongIdentity.connect(stableSettings.copy(expectedDeviceId="44444444-4444-4444-8444-444444444444"))
    eventually{wrongIdentity.state is LanClient.State.AuthFailed}
    verify(wrongIdentity.recoveryLink()==null && wrongIdentity.collectionTarget()==null,"mismatched historical computer identity cannot enable file or collection routes")
    wrongIdentity.disconnect()
    val offlineLogs = CopyOnWriteArrayList<String>()
    val unusedPort = java.net.ServerSocket(0).use { it.localPort.toString() }
    val retryClient = LanClient(onLog = { offlineLogs.add(it) })
    retryClient.connect(SettingsStore.Settings(serverIp="127.0.0.1", serverPort=unusedPort, password="isolated-only"))
    eventually { offlineLogs.any { it.contains("第 2 次重连") } }
    verify(offlineLogs.any { it.contains("等待 2s") }, "actual failed sockets retain the second backoff step instead of restarting at one second")
    retryClient.setNetworkAvailable(false)
    val attemptsBeforeNetworkLoss=offlineLogs.count { it.contains("正在连接") }
    delay(4_500)
    verify(offlineLogs.count { it.contains("正在连接") }==attemptsBeforeNetworkLoss,
        "missing LAN route suppresses actual socket attempts through the next retry deadline")
    retryClient.setNetworkAvailable(true)
    eventually { offlineLogs.count { it.contains("正在连接") }>attemptsBeforeNetworkLoss }
    verify(offlineLogs.any { it.contains("第 3 次重连") }, "network recovery immediately schedules the current target without losing retry history")
    retryClient.pause()
    println("All $checks production automatic receipt checks passed.")
    // OkHttp 与进程级 scope 刻意不按 Activity 生命周期关闭；独立检查程序到此退出。
    kotlin.system.exitProcess(0)
}

fun main(args:Array<String>) {
    var code=0
    try {runChecks(args)}catch(ex:Throwable){ex.printStackTrace();code=1}
    finally {kotlin.system.exitProcess(code)}
}
