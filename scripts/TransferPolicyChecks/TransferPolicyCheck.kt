package com.liuzhuan.app

import com.liuzhuan.app.clipboard.*
import kotlinx.coroutines.*
import kotlin.system.measureTimeMillis

private var checks=0
private fun verify(ok:Boolean,label:String){check(ok){label};checks++;println("PASS $label")}
fun main()=runBlocking {
    var now=1000L
    val queue=ClipboardEventQueue(now={now})
    val event=ClipboardEvent("synthetic manual",now,"android",ClipboardCaptureSource.MANUAL,listOf("text/plain"),"fixture")
    queue.enqueue(event,"fixture-pc");queue.update(event.requestId){it.copy(held=true,attempts=3)}
    var persisted=queue.snapshot();queue.attachPersistence(persisted){persisted=it}
    val inactive=CoroutineScope(SupervisorJob().apply {cancel()}+Dispatchers.Default)
    val delivery=AutomaticClipboardDelivery(queue,{null},{_,action->action();true},{_,_->},inactive,{now})
    verify(queue.nextExpiry()==now+ClipboardEventQueue.MANUAL_MAX_AGE_MS,"held manual requests expose the original seven-day deadline")
    now+=ClipboardEventQueue.MANUAL_MAX_AGE_MS+1;delivery.step()
    verify(queue.size()==0 && persisted.isEmpty(),"held expired text is durably removed without process restart")
    queue.enqueue(event.copy(requestId="failure"),"fixture-pc");queue.update("failure"){it.copy(held=true)}
    var reject=false;queue.attachPersistence(queue.snapshot()){if(reject)throw java.io.IOException("fixture disk failure")}
    reject=true;now+=ClipboardEventQueue.MANUAL_MAX_AGE_MS+1
    verify(runCatching {queue.expire(now)}.isFailure && queue.size()==1,"failed expiry persistence preserves the old in-memory request")

    TransferPolicyRuntime.apply(TransferPolicy(priority="upload",uploadMiB=100,downloadMiB=-2))
    verify(TransferPolicyRuntime.state.value.downloadMiB==0,"persisted policy limits normalize invalid negative values")
    val order=mutableListOf<String>();val slots=PriorityTransferSlots(1);val first=CompletableDeferred<Unit>();val release=CompletableDeferred<Unit>()
    val blocker=launch {slots.withSlot("download",{},{first.complete(Unit)}){release.await()}}
    first.await()
    val receive=launch {slots.withSlot("download",{},{}){order.add("receive")}}
    val send=launch {slots.withSlot("upload",{},{}){order.add("send")}}
    delay(40);release.complete(Unit);joinAll(blocker,receive,send)
    verify(order==listOf("send","receive"),"saved sending priority selects a queued upload before an earlier download")
    var clock=0L;val aging=PriorityTransferSlots(1,{clock});val occupied=CompletableDeferred<Unit>();val free=CompletableDeferred<Unit>();order.clear()
    val active=launch {aging.withSlot("upload",{},{occupied.complete(Unit)}){free.await()}}
    occupied.await();val old=launch {aging.withSlot("download",{},{}){order.add("aged")}};delay(20)
    clock=30_001;val preferred=launch {aging.withSlot("upload",{},{}){order.add("preferred")}};delay(20)
    free.complete(Unit);joinAll(active,old,preferred)
    verify(order==listOf("aged","preferred"),"waiting more than thirty seconds prevents priority starvation")
    val one=PriorityTransferSlots(1);val oneReady=CompletableDeferred<Unit>();val oneFree=CompletableDeferred<Unit>()
    val holder=launch {one.withSlot("upload",{},{oneReady.complete(Unit)}){oneFree.await()}};oneReady.await()
    var accidentallyStarted=false;val cancelled=launch {one.withSlot("download",{},{}){accidentallyStarted=true}}
    delay(20);cancelled.cancelAndJoin();oneFree.complete(Unit);holder.join()
    withTimeout(1000){one.withSlot("upload",{},{}){}}
    verify(!accidentallyStarted,"cancelling a queued task releases its wait entry and never leaks or starts a slot")
    TransferPolicyRuntime.apply(TransferPolicy(uploadMiB=1))
    val elapsed=measureTimeMillis {coroutineScope {repeat(2){launch {TransferPolicyRuntime.payload("upload",262144)}}}}
    verify(elapsed>=400 && elapsed<4000,"two workers share one direction budget instead of each receiving the full limit")
    TransferPolicyRuntime.apply(TransferPolicy(yieldToAudio=true))
    TransferPolicyRuntime.audioActive={true}
    verify(TransferPolicyRuntime.limit("upload")==1 && TransferPolicyRuntime.limit("download")==1,"active microphone temporarily constrains otherwise unlimited file budgets")
    TransferPolicyRuntime.audioActive={false}
    verify(TransferPolicyRuntime.limit("upload")==0,"ending audio restores the saved unlimited preference")
    TransferPolicyRuntime.apply(TransferPolicy(uploadMiB=1))
    val throttled=launch {TransferPolicyRuntime.payload("upload",Int.MAX_VALUE)};delay(30)
    val cancelTime=measureTimeMillis {throttled.cancelAndJoin()}
    verify(cancelTime<500,"cancellation interrupts a rate wait without a long blocking sleep")
    val blocking=launch(Dispatchers.IO){val owner=currentCoroutineContext()[Job]!!;TransferPolicyRuntime.payloadBlocking("upload",Int.MAX_VALUE){owner.ensureActive()}}
    delay(30);val blockingCancel=measureTimeMillis {blocking.cancelAndJoin()}
    verify(blockingCancel<500,"blocking OkHttp payload waits also observe task cancellation promptly")
    TransferPolicyRuntime.apply(TransferPolicy())
    println("All $checks transfer policy checks passed")
}
