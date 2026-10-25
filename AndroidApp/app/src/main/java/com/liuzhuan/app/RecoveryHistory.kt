package com.liuzhuan.app

import android.content.Context
import com.liuzhuan.app.core.RecoveryStorage
import kotlinx.coroutines.*
import kotlinx.coroutines.channels.Channel
import org.json.JSONArray
import org.json.JSONObject

/** Display progress is expendable; protocol checkpoints are persisted independently. */
internal class TaskHistoryWriteBudget(private val intervalMs:Long=3000) {
    private var saved:List<TransferTask>?=null
    private var nextProgressAt=0L
    private fun state(value:List<TransferTask>)=value.map {it.copy(done=0,updated=0,baseDone=0)}
    fun delayMs(value:List<TransferTask>,now:Long):Long? {
        val previous=saved ?: return 0
        if(previous==value)return null
        return if(state(previous)!=state(value))0 else (nextProgressAt-now).coerceAtLeast(0)
    }
    fun committed(value:List<TransferTask>,now:Long){saved=value;nextProgressAt=now+intervalMs}
}

/** Bounded encrypted history, conflated IO writes. Durable file descriptors remain the recovery authority. */
object RecoveryHistory {
    private val scope=CoroutineScope(SupervisorJob()+Dispatchers.IO+CoroutineExceptionHandler {_,_->com.liuzhuan.app.LanController.appendLog("任务记录未能保存，请检查手机存储空间")})
    private val changed=Channel<List<TransferTask>>(Channel.CONFLATED)
    private var initialized=false
    @Synchronized fun init(context:Context) {
        if(initialized)return
        initialized=true;val app=context.applicationContext
        TransferTasks.snapshotChanged={changed.trySend(it)}
        scope.launch {
            val storage=RecoveryStorage(app,"task-history")
            val hidden=RecoveryStorage(app,"task-history-hidden")
            var savedHidden="[]"
            try {
                val hiddenText=hidden.read();val hiddenArr=hiddenText?.let(::JSONArray)?:JSONArray();require(hiddenArr.length()<=20000);savedHidden=hiddenText?:"[]"
                TransferTasks.loadHiddenHistory((0 until hiddenArr.length()).map {hiddenArr.getString(it)})
                val arr=storage.read()?.let(::JSONArray) ?: JSONArray();require(arr.length()<=1000)
                val entries=(0 until arr.length()).map {i->val d=arr.getJSONObject(i)
                    val ended=TransferTaskStates.isTerminal(d.optString("state"))
                    TransferTask(d.getString("id"),d.getString("name"),d.getString("direction"),d.optLong("done"),d.optLong("total"),
                        state=if(d.optBoolean("running"))"等待恢复" else d.optString("state"),error=d.optString("error"),running=false,failed=!ended,
                        started=d.optLong("started"),updated=d.optLong("updated"),paused=d.optBoolean("paused"),cancelled=d.optBoolean("cancelled"),savedFolder=d.optString("savedFolder"),pending=d.optBoolean("pending"))
                }
                TransferTasks.loadHistory(entries)
            }catch(_:Exception){LanController.appendLog("收发历史无法读取，原文件已保留");return@launch}
            val budget=TaskHistoryWriteBudget()
            var latest=changed.receive()
            while(true) {
                while(true){latest=changed.tryReceive().getOrNull() ?: break}
                val wait=budget.delayMs(latest,android.os.SystemClock.elapsedRealtime())
                if(wait==null){latest=changed.receive();continue}
                if(wait>0) {
                    val newer=withTimeoutOrNull(wait){changed.receive()}
                    if(newer!=null){latest=newer;continue}
                }
                val arr=JSONArray();latest.take(1000).forEach {t->arr.put(JSONObject().put("id",t.id).put("name",t.name).put("direction",t.direction)
                    .put("done",t.done).put("total",t.total).put("state",t.state).put("error",t.error).put("running",t.running)
                    .put("started",t.started).put("updated",t.updated).put("paused",t.paused).put("cancelled",t.cancelled).put("savedFolder",t.savedFolder).put("pending",t.pending))}
                try {
                    val nextHidden=JSONArray(TransferTasks.hiddenHistorySnapshot()).toString()
                    if(nextHidden!=savedHidden){hidden.write(nextHidden);savedHidden=nextHidden}
                    storage.write(arr.toString());budget.committed(latest,android.os.SystemClock.elapsedRealtime())
                }catch(_:Exception){LanController.appendLog("收发历史保存失败，请检查手机存储空间")}
                latest=changed.receive()
            }
        }
    }
}
