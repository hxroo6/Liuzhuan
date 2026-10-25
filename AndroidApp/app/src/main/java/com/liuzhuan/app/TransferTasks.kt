package com.liuzhuan.app

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.util.UUID

data class TransferTask(val id:String, val name:String, val direction:String, val done:Long=0, val total:Long=0,
    val state:String="传输中", val error:String="", val running:Boolean=true, val failed:Boolean=false,
    val started:Long=System.currentTimeMillis(), val updated:Long=0,
    val paused:Boolean=false, val cancelled:Boolean=false, val stopping:Boolean=false, val savedFolder:String="", val pending:Boolean=false, val baseDone:Long=0) {
    val speed:Long get() = ((done-baseDone).coerceAtLeast(0) * 1000 / (updated-started).coerceAtLeast(1000))
}

/** 任务面板的进程状态；新协议的恢复描述和断点由 DurableTransfers 持久化。 */
object TransferTasks {
    private val hiddenHistory=linkedSetOf<String>()
    @Synchronized fun hiddenHistorySnapshot():List<String> = hiddenHistory.toList()
    @Synchronized fun loadHiddenHistory(ids:List<String>) {
        require(ids.size<=20000)
        require(ids.all {it.length in 1..256})
        hiddenHistory.addAll(ids)
        publish(mutable.value.filterNot {it.id in hiddenHistory && finishedHistory(it)})
    }
    private fun finishedHistory(t:TransferTask)=!t.running && !t.paused && !t.pending && !t.stopping && t.id !in durableIds &&
        t.id !in retries && t.id !in cancellations && TransferTaskStates.isTerminal(t.state)
    @Synchronized fun historyCandidates(days:Int,now:Long=System.currentTimeMillis()):List<TransferTask> {
        require(days in 0..3650)
        val cutoff=if(days==0)Long.MAX_VALUE else now-days*86_400_000L
        return mutable.value.filter {finishedHistory(it) && maxOf(it.updated,it.started)<cutoff}
    }
    @Synchronized fun clearHistory(ids:Set<String>):Int {
        val removing=mutable.value.filter {it.id in ids && finishedHistory(it)}.map {it.id}.toSet()
        require(hiddenHistory.size+removing.count {it !in hiddenHistory}<=20000){"历史隐藏记录已达上限，未清理。"}
        hiddenHistory.addAll(removing)
        publish(mutable.value.filterNot {it.id in removing});return removing.size
    }
    private val mutable = MutableStateFlow<List<TransferTask>>(emptyList())
    val tasks = mutable.asStateFlow()
    var snapshotChanged: (List<TransferTask>)->Unit = {}
    @Synchronized private fun publish(value:List<TransferTask>) { mutable.value=value;snapshotChanged(value) }
    @Synchronized fun loadHistory(value:List<TransferTask>) {
        val current=mutable.value
        publish((current+value.filter {old->current.none {it.id==old.id} && !(old.id in hiddenHistory && finishedHistory(old))}).take(1000))
    }
    @Synchronized fun restoreDurable(id:String,name:String,direction:String,done:Long,total:Long,state:String,cancel:()->Unit,retry:()->Unit) {
        val ended=state in listOf("stored","cancelled")
        if(ended && id in hiddenHistory)return
        val task=TransferTask(id,name,direction,done,total,state=when(state){"stored"->"已完成";"cancelled"->"已取消";"paused"->"已暂停";"expired"->"续传已过期";else->"等待原电脑恢复"},
            running=false,failed=!ended && state!="paused",paused=state=="paused",cancelled=state=="cancelled",pending=!ended && state !in listOf("paused","expired"))
        publish(listOf(task)+mutable.value.filterNot {it.id==id})
        if(!ended){durableIds.add(id);if(state!="expired")retries[id]=retry else retries.remove(id);cancellations[id]=cancel}
    }
    @Synchronized fun expired(id:String,message:String) {
        publish(mutable.value.map {if(it.id==id)it.copy(state="续传已过期",error=message,running=false,failed=true,pending=false,stopping=false,paused=false) else it})
        retries.remove(id)
    }
    @Synchronized fun markPaused(id:String) {publish(mutable.value.map {if(it.id==id)it.copy(paused=true) else it})}
    @Synchronized fun queued(id:String) {publish(mutable.value.map {if(it.id==id && it.running && !it.stopping)it.copy(state="排队中 · 等待传输槽") else it})}
    @Synchronized fun executing(id:String) {publish(mutable.value.map {if(it.id==id && it.running && !it.stopping && it.state.startsWith("排队中"))it.copy(state="传输中") else it})}
    @Synchronized fun markCancelled(id:String) {publish(mutable.value.map {if(it.id==id)it.copy(paused=false,cancelled=true) else it})}
    @Synchronized fun outbox(id:String,name:String,status:String,done:Boolean=false,failed:Boolean=false,cancel:(()->Unit)?=null,retry:(()->Unit)?=null) {
        if(done && id in hiddenHistory)return
        val old=mutable.value.firstOrNull {it.id==id} ?: TransferTask(id,name,"手机 → 电脑")
        publish(listOf(old.copy(state=status,running=!done && !failed,failed=failed,pending=!done && cancel!=null,updated=System.currentTimeMillis()))+mutable.value.filterNot {it.id==id})
        if(cancel!=null)cancellations[id]=cancel
        if(retry!=null)retries[id]=retry
        if(done || failed && cancel==null)cancellations.remove(id)
        if(done){retries.remove(id);cancellations.remove(id)}
    }
    private val retries = mutableMapOf<String,()->Unit>()
    private val cancellations = mutableMapOf<String,()->Unit>()
    private val discards = mutableMapOf<String,()->Unit>()
    private val durableIds=mutableSetOf<String>()
    private val progressAt = mutableMapOf<String,Long>()
    @Synchronized fun start(name:String, direction:String, id:String=UUID.randomUUID().toString(), discard:(()->Unit)?=null, cancel:(()->Unit)?=null, retry:(()->Unit)?=null):String {
        val entries=mutable.value.filterNot { it.id==id }.toMutableList()
        entries.add(0,TransferTask(id,name,direction))
        while(entries.size>40) { val old=entries.lastOrNull { !it.running && !it.paused && !it.pending && it.id !in durableIds } ?: break; entries.remove(old);retries.remove(old.id);cancellations.remove(old.id);progressAt.remove(old.id);discards.remove(old.id)?.invoke() }
        publish(entries)
        if(retry!=null) retries[id]=retry else retries.remove(id)
        if(cancel!=null) cancellations[id]=cancel else cancellations.remove(id)
        if(discard!=null) discards[id]=discard else discards.remove(id)
        progressAt.remove(id)
        return id
    }
    @Synchronized fun progress(id:String, done:Long, total:Long, name:String?=null, phase:String?=null) {
        val now=System.currentTimeMillis()
        if(now-(progressAt[id] ?: 0L)<200 && done!=total && name==null) return
        if(mutable.value.none { it.id==id && it.running && !it.stopping }) return
        progressAt[id] = now
        publish(mutable.value.map { if(it.id==id)
            it.copy(done=done,total=total,name=name ?: it.name,updated=now,state=phase ?: if(total>0 && done>=total) "等待确认" else "传输中") else it })
    }
    @Synchronized fun restoreProgress(id:String,done:Long,total:Long) {
        publish(mutable.value.map {if(it.id==id)it.copy(done=done,total=total,baseDone=done,updated=System.currentTimeMillis()) else it})
    }
    @Synchronized fun savedTo(id:String, folder:String) {
        publish(mutable.value.map { if(it.id==id) it.copy(savedFolder=folder) else it })
    }
    @Synchronized fun finish(id:String, ok:Boolean, message:String) {
        val task=mutable.value.firstOrNull { it.id==id } ?: return
        val paused=task.paused && !ok
        val cancelled=task.cancelled && !ok
        publish(mutable.value.map { if(it.id==id) it.copy(
            running=false,failed=!ok && !paused && !cancelled,paused=paused,cancelled=cancelled,stopping=false,pending=!ok && !paused && !cancelled && id in durableIds,
            state=when {ok->message;cancelled->"已取消";paused->"已暂停";id in durableIds->"等待恢复";else->"失败"},
            error=if(ok || paused || cancelled) "" else message,updated=System.currentTimeMillis()) else it })
        if(ok || cancelled) { durableIds.remove(id);retries.remove(id); discards.remove(id)?.invoke() }
        if(!paused && (ok || cancelled || id !in durableIds)) cancellations.remove(id)
        progressAt.remove(id)
    }
    fun pause(id:String) {
        val action=synchronized(this) {
            val task=mutable.value.firstOrNull { it.id==id } ?: return
            if(!task.running || task.stopping || !retries.containsKey(id)) return
            val callback=cancellations[id] ?: return
            publish(mutable.value.map { if(it.id==id) it.copy(paused=true,stopping=true,state="正在暂停…") else it })
            callback
        }
        action()
    }
    fun cancel(id:String) {
        val action=synchronized(this) {
            val task=mutable.value.firstOrNull { it.id==id } ?: return
            if(task.stopping || (!task.running && !task.paused && !task.failed)) return
            val callback=cancellations[id] ?: return
            publish(mutable.value.map { if(it.id==id) it.copy(paused=false,cancelled=true,stopping=task.running,state="正在取消…") else it })

            callback
        }
        action()
        if(!running(id))finish(id,false,"")
    }
    @Synchronized fun isDurable(id:String)=id in durableIds
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
            publish(mutable.value.map { if(it.id==id) it.copy(failed=false,paused=false,cancelled=false,running=false,state="重试中") else it })
            callback
        }
        try { action() }
        catch(ex:Exception) { finish(id,false,ex.message ?: "重试未成功，请检查连接或文件权限") }
    }
    @Synchronized fun canRetry(id:String)=retries.containsKey(id)
}
