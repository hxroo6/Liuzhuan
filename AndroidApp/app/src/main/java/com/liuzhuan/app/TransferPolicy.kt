package com.liuzhuan.app

import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow

data class TransferPolicy(val priority:String="balanced",val uploadMiB:Int=0,val downloadMiB:Int=0,val yieldToAudio:Boolean=false) {
    fun normalized()=copy(priority=priority.takeIf {it in listOf("balanced","upload","download")}?:"balanced",
        uploadMiB=uploadMiB.coerceIn(0,100),downloadMiB=downloadMiB.coerceIn(0,100))
}

/** Process-wide budgets: file payload only. Control, receipts and microphone frames never wait here. */
object TransferPolicyRuntime {
    private val mutable=MutableStateFlow(TransferPolicy())
    val state=mutable.asStateFlow()
    @Volatile var audioActive:()->Boolean={false}
    internal fun apply(value:TransferPolicy){mutable.value=value.normalized()}
    fun rank(direction:String):Int=if(state.value.priority==direction)1 else 0
    fun limit(direction:String):Int {
        val policy=state.value
        val saved=if(direction=="upload")policy.uploadMiB else policy.downloadMiB
        return if(policy.yieldToAudio && audioActive())if(saved==0)1 else minOf(1,saved) else saved
    }
    fun summary():String {
        fun rate(value:Int)=if(value==0)"不限速" else "$value MiB/s"
        val name=when(state.value.priority){"upload"->"发送优先";"download"->"接收优先";else->"按加入顺序"}
        return "$name · 发送${rate(limit("upload"))} · 接收${rate(limit("download"))}"+
            if(state.value.yieldToAudio && audioActive())" · 正为麦克风让路" else ""
    }
    private class Bucket {var tokens=0.0;var at=System.nanoTime();var rate=0}
    private val buckets=mapOf("upload" to Bucket(),"download" to Bucket())
    private fun take(bucket:Bucket,cap:Int,remaining:Int)=synchronized(bucket){
        val now=System.nanoTime();val perSecond=cap*1048576.0
        if(bucket.rate!=cap){bucket.tokens=0.0;bucket.at=now;bucket.rate=cap}
        bucket.tokens=minOf(65536.0,bucket.tokens+(now-bucket.at).coerceAtLeast(0)*perSecond/1_000_000_000.0);bucket.at=now
        minOf(remaining,bucket.tokens.toInt()).also {bucket.tokens-=it}
    }
    suspend fun payload(direction:String,bytes:Int) {
        val bucket=buckets[direction]?:return
        var remaining=bytes
        while(remaining>0) {
            currentCoroutineContext().ensureActive()
            val cap=limit(direction)
            if(cap==0)return
            remaining-=take(bucket,cap,remaining)
            if(remaining>0)delay(10)
        }
    }
    /** Called only by existing blocking OkHttp I/O workers, never from UI or a coroutine policy actor. */
    fun payloadBlocking(direction:String,bytes:Int,ensureActive:()->Unit) {
        val bucket=buckets[direction]?:return;var remaining=bytes
        while(remaining>0){ensureActive();val cap=limit(direction);if(cap==0)return;remaining-=take(bucket,cap,remaining);if(remaining>0)Thread.sleep(10)}
    }
}

/** The original two slots, with priority selection at admission and bounded aging against starvation. */
internal class PriorityTransferSlots(private val maximum:Int=2,private val now:()->Long=System::currentTimeMillis) {
    private data class Waiter(val direction:String,val at:Long,val sequence:Long,val ready:CompletableDeferred<Unit>)
    private var active=0
    private var sequence=0L
    private val waiting=mutableListOf<Waiter>()
    private fun drain() {
        while(active<maximum && waiting.isNotEmpty()) {
            val next=waiting.maxWith(compareBy<Waiter> {if(now()-it.at>=30_000)2 else TransferPolicyRuntime.rank(it.direction)}.thenBy {-it.sequence})
            waiting.remove(next)
            if(next.ready.complete(Unit))active++
        }
    }
    suspend fun <T> withSlot(direction:String,onQueued:()->Unit,onStart:()->Unit,block:suspend()->T):T {
        val waiter=synchronized(this){Waiter(direction,now(),sequence++,CompletableDeferred<Unit>()).also {waiting.add(it);drain()}}
        var admitted=false
        try {
            if(!waiter.ready.isCompleted)onQueued()
            waiter.ready.await();admitted=true;currentCoroutineContext().ensureActive();onStart()
            return block()
        }finally {
            synchronized(this){
                if(admitted || waiter.ready.isCompleted && !waiter.ready.isCancelled)active-- else waiting.remove(waiter)
                drain()
            }
        }
    }
}
