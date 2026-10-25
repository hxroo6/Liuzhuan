package com.liuzhuan.checks.admission

import android.content.Context
import android.net.Uri
import com.liuzhuan.app.*
import com.liuzhuan.app.core.FixtureDownloadBarrier
import com.liuzhuan.app.net.Proto
import kotlinx.coroutines.*
import java.io.File
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.TimeUnit

private fun verify(ok:Boolean,label:String){check(ok){label};println("PASS $label")}
private suspend fun await(test:()->Boolean){withTimeout(10000){while(!test())delay(10)}}

fun main(args:Array<String>):Unit=runBlocking {
    val root=File(args[0]).apply{mkdirs()};val context=Context(root)
    LanHub.client=FixtureClient(DurableTransfers.Link("127.0.0.1",1,"fixture","00000000-0000-0000-0000-000000000001","fixture"))
    DurableTransfers.init(context)
    val source=File(root,"source.bin").apply{writeBytes(byteArrayOf(1))}
    val callbacks=CopyOnWriteArrayList<Pair<Boolean,String>>()
    repeat(16){TransferWork.launch("occupied-$it"){awaitCancellation()}}
    await {TransferWork.active.value==2}
    verify(DurableTransfers.upload(context,Uri.fromFile(source),"rejected","android",null,{true},{ok,msg->callbacks.add(ok to msg)}),"rejected scheduling still handles the negotiated route without legacy fallback")
    verify(callbacks.size==1 && !callbacks.single().first,"queue rejection completes preparation callback exactly once")
    repeat(16){TransferWork.cancel("occupied-$it")}
    repeat(16){TransferWork.awaitStopped("occupied-$it")}
    callbacks.clear()
    repeat(2){TransferWork.launch("queued-blocker-$it"){awaitCancellation()}}
    await {TransferWork.active.value==2}
    DurableTransfers.upload(context,Uri.fromFile(source),"cancel-before-start","android",null,{true},{ok,msg->callbacks.add(ok to msg)})
    TransferTasks.cancel("cancel-before-start")
    await {callbacks.isNotEmpty()}
    delay(100)
    verify(callbacks.size==1 && !callbacks.single().first && callbacks.single().second.contains("取消"),"cancellation before IO slot admission completes preparation callback exactly once")
    repeat(2){TransferWork.cancel("queued-blocker-$it");TransferWork.awaitStopped("queued-blocker-$it")}
    callbacks.clear()
    DurableTransfers.upload(context,Uri.fromFile(source),"queued-not-stored","android",null,{true},{ok,msg->callbacks.add(ok to msg)})
    await {callbacks.isNotEmpty() && TransferTasks.tasks.value.any {it.id=="queued-not-stored"&&it.failed}}
    verify(callbacks.single()==(true to DurableTransfers.QUEUED_RESULT),"preparation success is explicitly queued and does not claim remote storage")
    verify(TransferTasks.tasks.value.single {it.id=="queued-not-stored"}.pending,"unreachable remote remains a recoverable pending task after queued feedback")
    System.setProperty("fixture.blockDownload","true")
    DurableTransfers.download(Proto.ItemData("same-item","same.bin"))
    check(FixtureDownloadBarrier.entered.await(5,TimeUnit.SECONDS))
    DurableTransfers.download(Proto.ItemData("same-item","same.bin"))
    delay(150);FixtureDownloadBarrier.release.countDown()
    await {File(root,"records").listFiles().orEmpty().any {it.extension=="json"&&it.name.startsWith("file-")&&it.name!="file-index.json"&&runCatching {it.readText().contains("same-item")}.getOrDefault(false)}}
    delay(300)
    val downloads=File(root,"records").listFiles().orEmpty().filter {it.extension=="json"&&it.name.startsWith("file-")&&it.name!="file-index.json"&&it.readText().contains("same-item")}
    verify(downloads.size==1,"concurrent taps share one durable download reservation even while its first descriptor write is blocked")
    val budget=TaskHistoryWriteBudget();val initial=listOf(TransferTask("budget","fixture","upload",total=10,started=1))
    verify(budget.delayMs(initial,0)==0L,"first task history snapshot is immediately eligible")
    budget.committed(initial,0)
    val progress=initial.map {it.copy(done=1,updated=200)}
    verify(budget.delayMs(progress,200)==2800L && budget.delayMs(progress,3000)==0L,"display-only progress writes coalesce to a three-second budget")
    verify(budget.delayMs(progress.map {it.copy(paused=true,state="已暂停")},201)==0L,"pause state bypasses the progress write delay")
    verify(budget.delayMs(progress.map {it.copy(running=false,state="已完成")},201)==0L,"terminal state bypasses the progress write delay")
    verify(budget.delayMs(initial,500)==null,"unchanged task history does not rewrite disk")
    kotlin.system.exitProcess(0)
}
