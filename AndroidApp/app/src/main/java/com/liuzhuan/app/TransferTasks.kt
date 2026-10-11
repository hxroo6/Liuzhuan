package com.liuzhuan.app

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.util.UUID

data class TransferTask(val id:String, val name:String, val direction:String, val done:Long=0, val total:Long=0,
    val state:String="传输中", val error:String="", val running:Boolean=true, val failed:Boolean=false,
    val started:Long=System.currentTimeMillis(), val updated:Long=0,
    val paused:Boolean=false, val cancelled:Boolean=false, val stopping:Boolean=false, val savedFolder:String="") {
    val speed:Long get() = (done * 1000 / (updated-started).coerceAtLeast(1000))
}

/** 暂停会关闭当前 I/O；继续调用原任务的完整重传，不冒充断点续传。 */
object TransferTasks {
    private val mutable = MutableStateFlow<List<TransferTask>>(emptyList())
    val tasks = mutable.asStateFlow()
    private val retries = mutableMapOf<String,()->Unit>()
    private val cancellations = mutableMapOf<String,()->Unit>()
    private val discards = mutableMapOf<String,()->Unit>()
    private val progressAt = mutableMapOf<String,Long>()
    @Synchronized fun start(name:String, direction:String, id:String=UUID.randomUUID().toString(), discard:(()->Unit)?=null, cancel:(()->Unit)?=null, retry:(()->Unit)?=null):String {
        val entries=mutable.value.filterNot { it.id==id }.toMutableList()
        entries.add(0,TransferTask(id,name,direction))
        while(entries.size>40) { val old=entries.lastOrNull { !it.running && !it.paused } ?: break; entries.remove(old);retries.remove(old.id);cancellations.remove(old.id);progressAt.remove(old.id);discards.remove(old.id)?.invoke() }
        mutable.value=entries
        if(retry!=null) retries[id]=retry else retries.remove(id)
        if(cancel!=null) cancellations[id]=cancel else cancellations.remove(id)
        if(discard!=null) discards[id]=discard else discards.remove(id)
        progressAt.remove(id)
        return id
    }
    @Synchronized fun progress(id:String, done:Long, total:Long, name:String?=null) {
        val now=System.currentTimeMillis()
        if(now-(progressAt[id] ?: 0L)<200 && done!=total && name==null) return
        if(mutable.value.none { it.id==id && it.running && !it.stopping }) return
        progressAt[id] = now
        mutable.value=mutable.value.map { if(it.id==id)
            it.copy(done=done,total=total,name=name ?: it.name,updated=now,state=if(total>0 && done>=total) "等待确认" else "传输中") else it }
    }
    @Synchronized fun savedTo(id:String, folder:String) {
        mutable.value=mutable.value.map { if(it.id==id) it.copy(savedFolder=folder) else it }
    }
    @Synchronized fun finish(id:String, ok:Boolean, message:String) {
        val task=mutable.value.firstOrNull { it.id==id } ?: return
        val paused=task.paused && !ok
        val cancelled=task.cancelled && !ok
        mutable.value=mutable.value.map { if(it.id==id) it.copy(
            running=false,failed=!ok && !paused && !cancelled,paused=paused,cancelled=cancelled,stopping=false,
            state=when {ok->message;cancelled->"已取消";paused->"已暂停";else->"失败"},
            error=if(ok || paused || cancelled) "" else message,updated=System.currentTimeMillis()) else it }
        if(ok || cancelled) { retries.remove(id); discards.remove(id)?.invoke() }
        if(!paused) cancellations.remove(id)
        progressAt.remove(id)
    }
    fun pause(id:String) {
        val action=synchronized(this) {
            val task=mutable.value.firstOrNull { it.id==id } ?: return
            if(!task.running || task.stopping || !retries.containsKey(id)) return
            val callback=cancellations[id] ?: return
            mutable.value=mutable.value.map { if(it.id==id) it.copy(paused=true,stopping=true,state="正在暂停…") else it }
            callback
        }
        action()
    }
    fun cancel(id:String) {
        val action=synchronized(this) {
            val task=mutable.value.firstOrNull { it.id==id } ?: return
            if(task.stopping || (!task.running && !task.paused)) return
            val callback=cancellations[id] ?: return
            mutable.value=mutable.value.map { if(it.id==id) it.copy(paused=false,cancelled=true,stopping=task.running,state="正在取消…") else it }
            if(!task.running) { finish(id,false,""); return }
            callback
        }
        action()
    }
    @Synchronized fun canPause(id:String)=retries.containsKey(id) && cancellations.containsKey(id)
    @Synchronized fun canCancel(id:String)=cancellations.containsKey(id)
    @Synchronized fun running(id:String)=mutable.value.any { it.id==id && it.running }
    fun resume(id:String) = restart(id,true)
    fun retry(id:String) = restart(id,false)
    private fun restart(id:String, fromPause:Boolean) {
        val action = synchronized(this) {
            val task=mutable.value.firstOrNull { it.id==id } ?: return
            if(task.stopping || task.running || (if(fromPause) !task.paused else !task.failed)) return
            val callback=retries[id] ?: return
            mutable.value=mutable.value.map { if(it.id==id) it.copy(failed=false,paused=false,cancelled=false,running=false,state="重试中") else it }
            callback
        }
        try { action() }
        catch(ex:Exception) { finish(id,false,ex.message ?: "重试未成功，请检查连接或文件权限") }
    }
    @Synchronized fun canRetry(id:String)=retries.containsKey(id)
}
