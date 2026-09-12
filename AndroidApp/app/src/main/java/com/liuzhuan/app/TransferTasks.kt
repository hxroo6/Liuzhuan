package com.liuzhuan.app

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.util.UUID

data class TransferTask(val id:String, val name:String, val direction:String, val done:Long=0, val total:Long=0,
    val state:String="传输中", val error:String="", val running:Boolean=true, val failed:Boolean=false,
    val started:Long=System.currentTimeMillis(), val updated:Long=0) {
    val speed:Long get() = (done * 1000 / (updated-started).coerceAtLeast(1000))
}

object TransferTasks {
    private val mutable = MutableStateFlow<List<TransferTask>>(emptyList())
    val tasks = mutable.asStateFlow()
    private val retries = mutableMapOf<String,()->Unit>()
    @Synchronized fun start(name:String, direction:String, id:String=UUID.randomUUID().toString(), retry:(()->Unit)?=null):String {
        val entries=mutable.value.filterNot { it.id==id }.toMutableList()
        entries.add(0,TransferTask(id,name,direction))
        while(entries.size>40) { val old=entries.lastOrNull { !it.running } ?: break; entries.remove(old);retries.remove(old.id) }
        mutable.value=entries
        if(retry!=null) retries[id]=retry else retries.remove(id)
        return id
    }
    @Synchronized fun progress(id:String, done:Long, total:Long, name:String?=null) {
        val now=System.currentTimeMillis()
        mutable.value=mutable.value.map { if(it.id==id && (now-it.updated>=200 || done==total || name!=null))
            it.copy(done=done,total=total,name=name ?: it.name,updated=now,state=if(total>0 && done>=total) "等待确认" else "传输中") else it }
    }
    @Synchronized fun finish(id:String, ok:Boolean, message:String) {
        mutable.value=mutable.value.map { if(it.id==id) it.copy(running=false,failed=!ok,state=if(ok) message else "失败",error=if(ok) "" else message,updated=System.currentTimeMillis()) else it }
        if(ok) retries.remove(id)
    }
    @Synchronized fun running(id:String)=mutable.value.any { it.id==id && it.running }
    fun retry(id:String) {
        val action = synchronized(this) {
            if(mutable.value.none { it.id==id && it.failed }) return
            val callback=retries[id] ?: return
            mutable.value=mutable.value.map { if(it.id==id) it.copy(failed=false,running=false,state="重试中") else it }
            callback
        }
        try { action() }
        catch(ex:Exception) { finish(id,false,ex.message ?: "重试未成功，请检查连接或文件权限") }
    }
    @Synchronized fun canRetry(id:String)=retries.containsKey(id)
}
