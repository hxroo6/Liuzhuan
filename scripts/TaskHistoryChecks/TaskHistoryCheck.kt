package com.liuzhuan.checks.taskhistory
import android.content.Context
import com.liuzhuan.app.*
import com.liuzhuan.app.core.FixtureHistoryIo
import kotlinx.coroutines.*
import org.json.JSONArray
import java.io.File

private val completed=listOf("已完成","电脑已持久登记","已校验并保存到手机","电脑已接收","电脑已登记文字","已保存到手机")
private fun verify(ok:Boolean,label:String){check(ok){label};println("PASS $label")}
private suspend fun await(test:()->Boolean){withTimeout(6000){while(!test())delay(10)}}
fun main(args:Array<String>):Unit=runBlocking {
    val root=File(args[1]).apply{mkdirs()};val history=File(root,"task-history.json");val hidden=File(root,"task-history-hidden.json")
    if(args[0]=="io-probe") {
        val file=File(root,"io-probe.json");val temporary=File(file.path+".tmp")
        FixtureHistoryIo.write(file,"old");temporary.writeText("new")
        if(System.getProperty("os.name").startsWith("Windows")) {
            var failure:Exception?=null
            file.inputStream().use {
                try {java.nio.file.Files.move(temporary.toPath(),file.toPath(),java.nio.file.StandardCopyOption.REPLACE_EXISTING,java.nio.file.StandardCopyOption.ATOMIC_MOVE)}
                catch(ex:Exception){failure=ex}
            }
            verify(failure is java.nio.file.AccessDeniedException,"an open Windows fixture reader deterministically reproduces the original atomic-replacement failure")
        }
        FixtureHistoryIo.write(file,"old")
        val entered=java.util.concurrent.CountDownLatch(1);val release=java.util.concurrent.CountDownLatch(1)
        val readValue=java.util.concurrent.atomic.AtomicReference<String>();val failure=java.util.concurrent.atomic.AtomicReference<Throwable>()
        val reader=Thread {try {FixtureHistoryIo.withReader(file){input->entered.countDown();check(release.await(5,java.util.concurrent.TimeUnit.SECONDS));readValue.set(input.bufferedReader().readText())}}catch(ex:Throwable){failure.set(ex)}}
        val writer=Thread {try {FixtureHistoryIo.write(file,"new")}catch(ex:Throwable){failure.set(ex)}}
        reader.start();check(entered.await(5,java.util.concurrent.TimeUnit.SECONDS));writer.start()
        verify(java.util.concurrent.TimeUnit.SECONDS.toNanos(3).let {budget->val until=System.nanoTime()+budget;while(writer.state!=Thread.State.BLOCKED&&writer.isAlive&&System.nanoTime()<until)Thread.yield();writer.state==Thread.State.BLOCKED},
            "the fixture writer waits for an in-flight same-path reader instead of racing Windows replacement")
        release.countDown();reader.join(5000);writer.join(5000)
        verify(!reader.isAlive&&!writer.isAlive&&failure.get()==null&&readValue.get()=="old"&&FixtureHistoryIo.read(file)=="new",
            "serialized fixture IO preserves the reader snapshot and completes atomic replacement without retries")
        kotlin.system.exitProcess(0)
    }
    RecoveryHistory.init(Context(root))
    if(args[0]=="seed") {
        await {history.exists()}
        completed.forEachIndexed {i,state->
            val id="success-$i";TransferTasks.start("success fixture","download",id)
            TransferTasks.savedTo(id,"Download/Liuzhuan");TransferTasks.finish(id,true,state)
        }
        TransferTasks.start("active fixture","download","active",cancel={})
        TransferTasks.start("paused fixture","download","paused",cancel={TransferTasks.finish("paused",false,"")},retry={})
        TransferTasks.pause("paused")
        TransferTasks.start("cancel fixture","download","cancelled",cancel={TransferTasks.finish("cancelled",false,"")})
        TransferTasks.cancel("cancelled")
        await {runCatching {val arr=JSONArray(FixtureHistoryIo.read(history)!!);arr.length()==9&& (0 until arr.length()).any {arr.getJSONObject(it).getString("id")=="success-5"}}.getOrDefault(false)}
        println("LEGACY_HISTORY_OBSERVED eligibleBeforeRestart=${TransferTasks.historyCandidates(0).any {it.id=="success-5"}}")
        println("PASS saved isolated production task history for a fresh JVM")
    } else if(args[0]=="verify") {
        await {TransferTasks.tasks.value.any {it.id=="success-5"}}
        val legacy=TransferTasks.tasks.value.single {it.id=="success-5"}
        println("LEGACY_HISTORY_OBSERVED restoredState=${legacy.state} restoredFailed=${legacy.failed}")
        verify(!legacy.failed&&!legacy.running&&legacy.state=="已保存到手机","legacy download success remains completed after a fresh process restores production history")
        verify(legacy.savedFolder=="Download/Liuzhuan","restored legacy download retains its saved-folder action")
        val ids=TransferTasks.historyCandidates(0).map {it.id}.toSet()
        verify(completed.indices.all {"success-$it" in ids}&&"cancelled" in ids,"every real completed label and cancelled history can be selected for cleanup")
        verify("active" !in ids&&"paused" !in ids,"restored interrupted and paused tasks remain protected from history cleanup")
        val previous=FixtureHistoryIo.read(history)!!
        verify(TransferTasks.clearHistory(ids)==7,"cleanup removes exactly the seven finished fixture records")
        await {hidden.exists()&&FixtureHistoryIo.read(hidden)!!.contains("success-5")&&runCatching {!FixtureHistoryIo.read(history)!!.contains("success-5")}.getOrDefault(false)}
        // Simulate interruption after the hidden set is committed but before the old history file is replaced.
        FixtureHistoryIo.write(history,previous)
        println("PASS retained an old fixture history file to exercise hidden-set recovery")
    } else {
        await {TransferTasks.tasks.value.any {it.id=="active"}}
        verify(TransferTasks.tasks.value.none {it.id.startsWith("success-")||it.id=="cancelled"},"hidden completed downloads cannot reappear from an older history snapshot after another restart")
        verify(TransferTasks.tasks.value.any {it.id=="active"}&&TransferTasks.tasks.value.any {it.id=="paused"},"hidden-set recovery preserves unfinished task records")
    }
    kotlin.system.exitProcess(0)
}
