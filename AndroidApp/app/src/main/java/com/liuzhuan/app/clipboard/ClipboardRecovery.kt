package com.liuzhuan.app.clipboard

import android.content.Context
import com.liuzhuan.app.TransferTasks
import com.liuzhuan.app.core.RecoveryStorage
import kotlinx.coroutines.*
import org.json.JSONArray
import org.json.JSONObject

/** Encrypted outbox + bounded receipt ledger. Content is never placed in log messages. */
object ClipboardRecovery {
    private val scope=CoroutineScope(SupervisorJob()+Dispatchers.IO+CoroutineExceptionHandler {_,_->com.liuzhuan.app.LanController.appendLog("任务记录未能保存，请检查手机存储空间")})
    private val gate=Any()
    private var initialized=false
    private var storage:RecoveryStorage?=null
    private var persisted=emptyList<ClipboardEventQueue.Pending>()
    private val receipts=linkedMapOf<String,JSONObject>()
    @Synchronized fun init(context:Context,queue:ClipboardEventQueue) {
        if(initialized)return
        initialized=true;queue.persistenceReady=false;val app=context.applicationContext
        scope.launch {
            try {
                val file=RecoveryStorage(app,"text-outbox")
                val doc=file.read()?.let(::JSONObject) ?: JSONObject()
                val a=doc.optJSONArray("pending") ?: JSONArray();require(a.length()<=50)
                val loaded=(0 until a.length()).map {i->decode(a.getJSONObject(i))}
                val r=doc.optJSONArray("receipts") ?: JSONArray();require(r.length()<=200)
                synchronized(gate) {
                    storage=file
                    for(i in 0 until r.length()){val receipt=r.getJSONObject(i);receipts[receipt.getString("id")]=receipt}
                }
                queue.attachPersistence(loaded.filter {receipts[it.event.requestId]?.optString("status") !in listOf("stored","rejected")}) {pending->synchronized(gate){persist(pending)}}
                loaded.filter {queue.find(it.event.requestId)==null && !receipts.containsKey(it.event.requestId)}.forEach {
                    show(it.event,if(it.expiresAt<=System.currentTimeMillis())"请求已超期，待发内容已取消" else "恢复的待发未绑定原电脑，已取消")
                }
                receipts.values.toList().forEach {receipt->TransferTasks.outbox(receipt.getString("id"),"文字任务",
                    if(receipt.optString("status")=="stored")"电脑已登记文字" else "电脑拒绝登记",done=receipt.optString("status")=="stored",failed=receipt.optString("status")!="stored")}
                queue.snapshot().forEach {show(it.event,if(it.held)"自动重试已停止，文字仍在待发中；请点击重试或取消" else "待发文字已恢复，等待原电脑连接")}
            }catch(_:Exception){ClipboardMonitorCoordinator.setDiagnostic("文字恢复存储无法读取，已保留原文件并停止补发；请检查设备存储")}
        }
    }
    private fun persist(pending:List<ClipboardEventQueue.Pending>) {
        val a=JSONArray();pending.forEach {a.put(encode(it))}
        val r=JSONArray();receipts.values.forEach {r.put(it)}
        storage?.write(JSONObject().put("pending",a).put("receipts",r).toString()) ?: error("待发存储尚未就绪")
        persisted=pending
    }
    fun receipt(id:String,status:String) = synchronized(gate) {
        if(storage==null)return@synchronized
        val next=JSONObject().put("id",id).put("status",status).put("at",System.currentTimeMillis())
        val old=receipts.toMap();receipts[id]=next
        while(receipts.size>200)receipts.remove(receipts.keys.first())
        try {persist(persisted)}catch(ex:Exception){receipts.clear();receipts.putAll(old);throw ex}
    }
    fun show(event:ClipboardEvent,message:String) {
        val done=message.contains("电脑已登记文字")
        val failed=listOf("已取消","已超期","拒绝登记","停止重发","三次提交","停止自动重试","隐私规则已拦截","旧版电脑没有","已停止","停止发送").any(message::contains)
        val recoverable=ClipboardMonitorCoordinator.queue.find(event.requestId)!=null && !done
        TransferTasks.outbox(event.requestId,if(event.source==ClipboardCaptureSource.MANUAL)event.text.orEmpty().take(40) else "自动文字",message,done,failed,
            cancel=if(recoverable) { { scope.launch {ClipboardMonitorCoordinator.queue.remove(event.requestId);TransferTasks.finish(event.requestId,false,"")} } } else null,
            retry=if(recoverable && failed) { {scope.launch {
                ClipboardMonitorCoordinator.queue.update(event.requestId){it.copy(attempts=0,held=false,awaitingReceipt=false,nextAt=System.currentTimeMillis())}
                show(event,"文字待发已恢复，等待电脑登记");ClipboardMonitorCoordinator.delivery.wake()
            }} } else null)
    }
    private fun encode(p:ClipboardEventQueue.Pending):JSONObject {
        val e=p.event
        return JSONObject().put("text",e.text).put("timestamp",e.timestamp).put("package",e.sourcePackage).put("source",e.source.name)
            .put("mimes",JSONArray(e.mimeTypes)).put("fingerprint",e.fingerprint).put("diagnosticId",e.diagnosticId).put("revision",e.privacyRevision)
            .put("id",e.requestId).put("queuedAt",p.queuedAt).put("expiresAt",p.expiresAt).put("attempts",p.attempts).put("nextAt",p.nextAt)
            .put("awaitingReceipt",p.awaitingReceipt).put("target",p.target).put("receiptCapable",p.receiptCapable).put("mayHaveRegistered",p.mayHaveRegistered).put("held",p.held)
    }
    private fun decode(d:JSONObject):ClipboardEventQueue.Pending {
        val m=d.getJSONArray("mimes");val text=d.getString("text");require(text.length<=262144)
        val event=ClipboardEvent(text,d.getLong("timestamp"),if(d.isNull("package"))null else d.getString("package"),ClipboardCaptureSource.valueOf(d.getString("source")),
            (0 until m.length()).map {m.getString(it)},d.getString("fingerprint"),diagnosticId=d.optString("diagnosticId"),privacyRevision=if(d.isNull("revision"))null else d.getLong("revision"),requestId=d.getString("id"))
        return ClipboardEventQueue.Pending(event,d.getLong("queuedAt"),d.getLong("expiresAt"),d.getInt("attempts"),d.getLong("nextAt"),d.getBoolean("awaitingReceipt"),
            if(d.isNull("target"))null else d.getString("target"),d.getBoolean("receiptCapable"),d.getBoolean("mayHaveRegistered"),true,d.optBoolean("held"))
    }
}
