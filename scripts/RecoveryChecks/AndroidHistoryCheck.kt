package com.liuzhuan.checks.history

import android.content.Context
import com.liuzhuan.app.*
import kotlinx.coroutines.*
import java.io.File

fun main(args:Array<String>)=runBlocking {
    val root=File(args[1]).apply{mkdirs()};RecoveryHistory.init(Context(root))
    suspend fun waitFor(test:()->Boolean){withTimeout(10000){while(!test())delay(20)}}
    if(args[0]=="seed") {
        TransferTasks.loadHistory(listOf(TransferTask("hidden-fixture","finished fixture","download",state="已完成",running=false,started=1),
            TransferTask("paused-fixture","paused fixture","download",state="已暂停",running=false,paused=true,started=1)))
        delay(600)
        check(TransferTasks.clearHistory(setOf("hidden-fixture","paused-fixture"))==1)
        waitFor{File(root,"records/task-history-hidden.json").let {it.exists()&&it.readText().contains("hidden-fixture")} &&
            File(root,"records/task-history.json").let {it.exists()&&!it.readText().contains("hidden-fixture")}}
        println("PASS production history writer persists tombstone before display history and keeps paused rows")
    }else {
        waitFor{TransferTasks.tasks.value.any {it.id=="paused-fixture"}}
        TransferTasks.restoreDurable("hidden-fixture","old descriptor","download",1,1,"stored",{}, {})
        check(TransferTasks.tasks.value.none {it.id=="hidden-fixture"})
        println("PASS fresh JVM history load prevents completed descriptors resurrecting cleared history")
        TransferTasks.restoreDurable("hidden-fixture","active descriptor","download",0,1,"paused",{}, {})
        check(TransferTasks.tasks.value.any {it.id=="hidden-fixture"&&it.paused})
        println("PASS tombstone never hides an authoritative active recovery task")
        val hidden=File(root,"records/task-history-hidden.json");val stamp=hidden.lastModified();TransferTasks.savedTo("paused-fixture","Download/Liuzhuan");delay(700)
        check(hidden.lastModified()==stamp)
        println("PASS unchanged history tombstones do not cause repeat disk writes on task updates")
    }
}
