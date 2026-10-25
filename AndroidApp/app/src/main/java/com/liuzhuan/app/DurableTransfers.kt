package com.liuzhuan.app

import android.content.ContentValues
import android.content.Context
import android.net.Uri
import android.provider.MediaStore
import android.provider.OpenableColumns
import com.liuzhuan.app.core.RecoveryStorage
import com.liuzhuan.app.net.HttpClients
import com.liuzhuan.app.net.Proto
import kotlinx.coroutines.*
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.MediaType.Companion.toMediaType
import org.json.JSONObject
import java.io.File
import java.io.IOException
import java.io.RandomAccessFile
import java.security.MessageDigest
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import com.liuzhuan.app.core.hexString

/** Persistent descriptors and private source snapshots. Two actual IO jobs at most, no idle polling. */
object DurableTransfers {
    data class Link(val host:String,val port:Int,val auth:String,val serverId:String,val target:String,val legacyTarget:String="")
    @Volatile var pinnedReceiver:(suspend (target:String,itemId:String,sourceRevision:Long,pinToken:String,file:File,size:Long,sha256:String)->Unit)?=null
    private fun libraryTarget(link:Link):String?=link.target.takeIf {it.startsWith("library:")}?.split(':')?.takeIf {it.size==4}?.let {"${it[1]}:${it[2]}"}
    private val scope=CoroutineScope(SupervisorJob()+Dispatchers.IO+CoroutineExceptionHandler {_,_->LanController.appendLog("恢复状态未能保存，操作已停止；请检查手机存储空间")})
    private val ready=CompletableDeferred<Unit>()
    private val entries=ConcurrentHashMap<String,JSONObject>()
    private class LockNode {val gate=Any();var users=0}
    private val locks=mutableMapOf<String,LockNode>()
    private lateinit var app:Context
    private lateinit var root:File
    private var initialized=false
    @Volatile private var unavailable=false
    private inline fun <T> withTaskLock(id:String,action:()->T):T {
        val node=synchronized(locks){locks.getOrPut(id){LockNode()}.also {it.users++}}
        try {return synchronized(node.gate){action()}}
        finally {synchronized(locks){node.users--;if(node.users==0 && locks[id]===node)locks.remove(id)}}
    }
    @Synchronized fun init(context:Context) {
        if(initialized)return
        initialized=true;app=context.applicationContext;root=File(app.noBackupFilesDir,"transfer-payloads");root.mkdirs()
        TransferPolicyStore.init(app)
        try {
            val identity=File(app.noBackupFilesDir,"recovery/client-id");identity.parentFile!!.mkdirs()
            Proto.clientId=if(identity.exists())UUID.fromString(identity.readText().trim()).toString() else UUID.randomUUID().toString().also {
                val temp=File(identity.path+".tmp");temp.outputStream().use {output->output.write(it.toByteArray());output.fd.sync()}
                java.nio.file.Files.move(temp.toPath(),identity.toPath(),java.nio.file.StandardCopyOption.ATOMIC_MOVE)
            }
        }catch(_:Exception){unavailable=true;LanController.appendLog("设备身份无法读取，已停止任务自动恢复；请检查设备存储")}
        scope.launch {
            try {
                val index=RecoveryStorage(app,"file-index").read()?.let {org.json.JSONArray(it)} ?: org.json.JSONArray()
                require(index.length()<=1000)
                for(i in 0 until index.length()) {
                    val id=index.getString(i);require(UUID.fromString(id).toString()==id)
                    val d=RecoveryStorage(app,"file-$id").read()?.let(::JSONObject) ?: continue
                    if(d.optString("state")=="active")d.put("state","waiting")
                    entries[id]=d;install(d)
                }
                root.listFiles()?.filter {it.name.endsWith(".payload") && runCatching {UUID.fromString(it.name.removeSuffix(".payload"))}.isSuccess && !entries.containsKey(it.name.removeSuffix(".payload"))}?.forEach {it.delete()}
            }catch(_:Exception){unavailable=true;LanController.appendLog("文件恢复记录无法读取，原记录已保留，已停止自动恢复")}
            ready.complete(Unit)
            LanHub.client?.recoveryLink()?.let(::connected)
        }
    }
    private fun save(d:JSONObject) {
        val id=d.getString("id")
        RecoveryStorage(app,"file-$id").write(d.toString());entries[id]=JSONObject(d.toString())
    }
    private fun snapshot(id:String):JSONObject=withTaskLock(id){JSONObject((entries[id] ?: error("任务不存在")).toString())}
    private fun update(id:String,block:(JSONObject)->Unit):JSONObject=withTaskLock(id) {
        val d=snapshot(id);block(d);save(d);d
    }
    private fun add(d:JSONObject) = synchronized(entries) {
        check(!unavailable) {"恢复存储不可用，请检查设备存储后重启流转"}
        val completed=entries.values.filter {it.optString("state") in listOf("stored","cancelled")}.sortedByDescending {it.optLong("created")}
        completed.drop(40).forEach {old->
            val oldId=old.getString("id");entries.remove(oldId);File(root,oldId+".payload").delete()
        }
        require(entries.size<1000) {"任务恢复记录已满"}
        save(d)
        try {RecoveryStorage(app,"file-index").write(org.json.JSONArray(entries.keys.toList()).toString())}
        catch(ex:Exception){entries.remove(d.getString("id"));throw ex}
        completed.drop(40).forEach {old->RecoveryStorage(app,"file-${old.getString("id")}").delete()}
    }
    private fun eligible(d:JSONObject,link:Link):Boolean=d.optString("target")==link.target && d.optString("serverId")==link.serverId
    private fun ensure(id:String,link:Link) {
        val d=snapshot(id)
        val row=TransferTasks.tasks.value.firstOrNull {it.id==d.getString("localId")}
        if(row?.paused==true || row?.cancelled==true)throw CancellationException("任务已暂停或取消")
        if(d.optString("state") in listOf("paused","cancelled"))throw CancellationException("任务已暂停或取消")
        if(!eligible(d,link) || LanHub.client?.recoveryLink()!=link)throw IOException("电脑连接已变化，已保留断点")
        if(d.optBoolean("automatic") && com.liuzhuan.app.clipboard.ClipboardPrivacy.blockReason(automaticEvent(d))!=null)throw IOException("隐私规则已变化，已停止旧自动文件发送")
        if(d.optString("origin")=="directory") {
            val folder=d.optString("folderId")
            if(FolderSubscriptions.state.value.entries.none {it.id==folder && it.target==link.target})throw IOException("来源订阅已移除，已停止发送")
        }
    }
    private fun automaticEvent(d:JSONObject)=com.liuzhuan.app.clipboard.ClipboardEvent(null,d.optLong("capturedAt"),d.optString("sourcePackage").ifBlank {null},
        com.liuzhuan.app.clipboard.ClipboardCaptureSource.XPOSED_HOOK,emptyList(),"",privacyRevision=if(d.isNull("privacyRevision"))null else d.getLong("privacyRevision"))
    fun cached(file:File,name:String,localId:String,event:com.liuzhuan.app.clipboard.ClipboardEvent):Boolean {
        if(!initialized || LanHub.client?.recoveryLink()==null)return false
        return upload(app,Uri.fromFile(file),localId,"automatic",null,{com.liuzhuan.app.clipboard.ClipboardPrivacy.blockReason(event)==null},
            {ok,_->if(ok)file.delete()},name,event)
    }
    fun removeFolder(folderId:String) {scope.launch {
        ready.await();entries.values.filter {it.optString("folderId")==folderId && it.optString("state") !in listOf("stored","cancelled")}.forEach {control(it.getString("id"),"cancelled")}
    }}
    fun invalidateAutomatic() {scope.launch {
        ready.await();entries.values.filter {it.optBoolean("automatic") && it.optString("state") !in listOf("stored","cancelled")}.forEach {d->control(d.getString("id"),"cancelled")}
    }}
    private fun install(d:JSONObject) {
        val local=d.getString("localId");val id=d.getString("id");val state=d.optString("state")
        TransferTasks.restoreDurable(local,d.getString("name"),if(d.getString("direction")=="upload")"手机 → 电脑" else "电脑 → 手机",
            d.optLong("offset"),d.optLong("size"),state,
            cancel={control(id,if(TransferTasks.tasks.value.firstOrNull {it.id==local}?.cancelled==true)"cancelled" else "paused")},
            retry={resume(id)})
    }
    fun disconnected(link:Link) {
        entries.values.filter {eligible(it,link) && it.optString("state") in listOf("active","waiting")}.forEach {TransferWork.cancel(it.getString("localId"))}
    }
    fun connected(link:Link) {scope.launch {
        ready.await();if(unavailable)return@launch
        try {FolderSubscriptions.rebindVerifiedTargetAwait(link.legacyTarget,link.target)}
        catch(_:Exception){LanController.appendLog("目录目标升级未能保存，文件断点已保留；请检查存储后重连");return@launch}
        entries.keys.toList().forEach {id->
            var d=snapshot(id)
            if(link.legacyTarget.isNotEmpty() && d.optString("target")==link.legacyTarget && d.optString("serverId")==link.serverId) d=update(id){it.put("target",link.target)}
            if(eligible(d,link)) {
                if(d.optString("state") !in listOf("stored","cancelled","expired")) {
                    TransferWork.awaitStopped(d.getString("localId"))
                    if(LanHub.client?.recoveryLink()!=link)return@launch
                    val status=runCatching {request(d,link,"GET","status").first}.getOrNull()
                    if(status?.optString("controlSource")=="desktop" && status.optLong("controlRevision")>d.optLong("controlRevision")) {
                        remoteState(link.serverId,id,status.optString("state"),status.optLong("controlRevision"))
                    }else if(d.optString("state") in listOf("waiting","active"))run(id)
                }
                if(d.optString("state")=="cancelled")runCatching {request(d,link,"POST","cancel")}
            }
        }
    }}
    fun remoteState(serverId:String,id:String,state:String,revision:Long) {scope.launch {
        ready.await();val d=entries[id]?.let {snapshot(id)} ?: return@launch
        val link=LanHub.client?.recoveryLink() ?: return@launch
        if(link.serverId!=serverId || !eligible(d,link) || revision<=d.optLong("controlRevision") || d.optString("state") in listOf("stored","expired","cancelled"))return@launch
        var applied=false
        update(id){if(revision>it.optLong("controlRevision")){it.put("controlRevision",revision);applied=true}}
        if(!applied)return@launch
        if(state=="active")resume(id,revision) else if(state in listOf("paused","cancelled"))control(id,state,false,revision)
    }}
    private fun resume(id:String,revision:Long?=null) {scope.launch {
        ready.await();if(unavailable){LanController.appendLog("恢复存储不可用，原记录已保留；请先检查设备存储");return@launch};val link=LanHub.client?.recoveryLink()
        if(snapshot(id).optString("state")=="expired"){TransferTasks.expired(snapshot(id).getString("localId"),"电脑原任务已清除或过期，请检查对端后取消并重新发送");return@launch}
        if(link==null || !eligible(snapshot(id),link)){TransferTasks.finish(snapshot(id).getString("localId"),false,"请连接原电脑后继续，断点已保留");return@launch}
        var applied=false
        update(id){if(revision==null || revision==it.optLong("controlRevision")){it.put("state","waiting").put("resume",true);applied=true}}
        if(applied)run(id)
    }}
    private fun control(id:String,state:String,notify:Boolean=true,revision:Long?=null) {scope.launch {
        ready.await()
        var applied=false
        val d=update(id){if(it.optString("state")!="stored" && (revision==null || revision==it.optLong("controlRevision"))){it.put("state",state).put("generation",it.optLong("generation")+1);applied=true}}
        if(!applied)return@launch
        if(d.optString("state")=="stored")return@launch
        val local=d.getString("localId")
        if(state=="cancelled")TransferTasks.markCancelled(local) else TransferTasks.markPaused(local)
        val stopped=TransferWork.cancel(local)
        TransferTasks.finish(local,false,"")
        stopped?.join()
        val latest=snapshot(id)
        if(latest.optString("state")=="stored" || latest.optLong("generation")!=d.optLong("generation"))return@launch
        if(state=="cancelled"){File(root,id+".payload").delete();cleanupPending(id)}
        if(notify)LanHub.client?.recoveryLink()?.takeIf {eligible(d,it)}?.let {link->runCatching {
            val reply=request(d,link,"POST",if(state=="cancelled")"cancel" else "pause").first
            update(id){it.put("controlRevision",maxOf(it.optLong("controlRevision"),reply.optLong("controlRevision")))}
            if(reply.optString("state")=="cancelled" && state!="cancelled")control(id,"cancelled",false)
            if(reply.optString("state")=="stored"){update(id){it.put("state","stored")};TransferTasks.finish(local,true,if(d.getString("direction")=="upload")"电脑已持久登记" else "已校验并保存到手机")}
        }}
    }}
    fun upload(context:Context,uri:Uri,localId:String,origin:String,expectedTarget:String?,allowed:()->Boolean,onResult:(Boolean,String)->Unit,providedName:String?=null,automatic:com.liuzhuan.app.clipboard.ClipboardEvent?=null):Boolean {
        val link=LanHub.client?.recoveryLink() ?: return false
        val completed=java.util.concurrent.atomic.AtomicBoolean(false)
        val result:(Boolean,String)->Unit={ok,message->if(completed.compareAndSet(false,true))runCatching {onResult(ok,message)};Unit}
        TransferTasks.start("准备续传文件","手机 → 电脑",localId,cancel={TransferWork.cancel(localId)})
        TransferWork.launch(localId,onNotStarted={result(false,it)}) {
            val workContext=currentCoroutineContext()
            val id=UUID.randomUUID().toString();val file=File(root,id+".payload")
            try {
                ready.await()
                check(allowed() && (expectedTarget==null || expectedTarget==link.target)) {"来源订阅或电脑已变化"}
                val resolver=context.applicationContext.contentResolver
                var name="upload.bin";var reported=-1L;var sourceStamp:Pair<String,Long>?=null
                resolver.query(uri,null,null,null,null)?.use {c->if(c.moveToFirst()) {
                    listOf("last_modified","date_modified").firstOrNull {c.getColumnIndex(it)>=0 && !c.isNull(c.getColumnIndex(it))}?.let {column->sourceStamp=column to c.getLong(c.getColumnIndex(column))}
                    c.getColumnIndex(OpenableColumns.DISPLAY_NAME).takeIf {it>=0}?.let {name=c.getString(it) ?: name}
                    c.getColumnIndex(OpenableColumns.SIZE).takeIf {it>=0 && !c.isNull(it)}?.let {reported=c.getLong(it)}
                }}
                if(providedName!=null)name=providedName
                if(name.length>220){val extension=name.substringAfterLast('.',"").takeIf {it.length in 1..20}?.let {".$it"}.orEmpty();name=name.take(220-extension.length)+extension}
                if(reported>root.usableSpace-1048576)throw IOException("手机空间不足以保存可恢复的发送副本")
                TransferTasks.progress(localId,0,reported.coerceAtLeast(0),name,phase="准备可恢复副本")
                val digest=MessageDigest.getInstance("SHA-256");var size=0L
                TransferWork.useResource(localId,resolver.openInputStream(uri) ?: throw IOException("无法读取来源文件")) {input->
                    file.outputStream().use {output->val buffer=ByteArray(65536)
                        while(true){workContext.ensureActive();check(allowed() && LanHub.client?.recoveryLink()==link){"来源授权或电脑已变化"};val n=input.read(buffer);if(n<0)break;if(n==0)throw IOException("来源暂未返回数据，请重试")
                            if(size%1048576==0L && root.usableSpace<n+1048576)throw IOException("手机存储空间不足")
                            output.write(buffer,0,n);digest.update(buffer,0,n);size+=n;TransferTasks.progress(localId,size,reported.coerceAtLeast(0),phase="准备可恢复副本")}
                        output.fd.sync()
                    }
                }
                check(allowed() && LanHub.client?.recoveryLink()==link){"来源授权或电脑已变化"}
                check(reported<0 || reported==size){"来源文件在准备期间变化，请稳定后重新选择"}
                sourceStamp?.let {stamp->
                    resolver.query(uri,null,null,null,null)?.use {c->
                        val column=c.getColumnIndex(stamp.first)
                        check(c.moveToFirst() && column>=0 && !c.isNull(column) && c.getLong(column)==stamp.second){"来源文件在准备期间变化，请稳定后重新选择"}
                    } ?: throw IOException("来源文件已不可读取")
                }
                val d=JSONObject().put("id",id).put("localId",localId).put("direction","upload").put("name",name.take(220)).put("size",size)
                    .put("sha256",digest.digest().hexString()).put("offset",0).put("state","waiting").put("target",link.target).put("serverId",link.serverId)
                    .put("origin",origin).put("folderId",if(origin=="directory")localId.split(':').getOrNull(1).orEmpty() else "").put("created",System.currentTimeMillis())
                if(automatic!=null)d.put("automatic",true).put("privacyRevision",automatic.privacyRevision).put("sourcePackage",automatic.sourcePackage).put("capturedAt",automatic.timestamp)
                workContext.ensureActive()
                add(d);workContext.ensureActive();install(d)
                // The transfer worker remains the sole owner of this local ID while preparation finishes.
                scope.launch {run(id)}
                result(true,QUEUED_RESULT)
            }catch(ex:Exception){
                if(ex is CancellationException && entries.containsKey(id)){update(id){it.put("state","cancelled")};TransferTasks.markCancelled(localId)}
                if(!entries.containsKey(id) || snapshot(id).optString("state")=="cancelled")file.delete()
                TransferTasks.finish(localId,false,ex.message ?: "准备文件失败")
                result(false,ex.message ?: "准备失败")
                if(ex is CancellationException)throw ex
            }
        }
        return true
    }
    const val QUEUED_RESULT="文件已加入可恢复任务"
    fun download(data:Proto.ItemData):Boolean=downloadInternal(data)
    fun downloadPinned(data:Proto.ItemData,expectedTarget:String,sourceRevision:Long,pinToken:String=""):Boolean {
        val link=LanHub.client?.recoveryLink()?:return false
        if(sourceRevision<=0 || libraryTarget(link)!=expectedTarget || pinnedReceiver==null || runCatching {UUID.fromString(pinToken).toString()==pinToken}.getOrDefault(false).not())return false
        return downloadInternal(data,expectedTarget,sourceRevision,pinToken)
    }
    fun cancelPinned(target:String,itemId:String,sourceRevision:Long,pinToken:String) {scope.launch {
        ready.await();entries.values.filter {it.optString("pinTarget")==target && it.optString("itemId")==itemId && it.optLong("pinRevision")==sourceRevision && it.optString("pinToken")==pinToken && it.optString("state") !in listOf("stored","cancelled")}
            .forEach {control(it.getString("id"),"cancelled")}
    }}
    private fun downloadInternal(data:Proto.ItemData,pinTarget:String="",pinRevision:Long=0,pinToken:String=""):Boolean {
        val link=LanHub.client?.recoveryLink() ?: return false
        scope.launch {
            ready.await()
            try {
                // Reserve by original target + item atomically, including slow descriptor writes.
                val (d,created)=synchronized(entries) {
                    val existing=entries.values.firstOrNull {it.optString("itemId")==data.id && it.optString("direction")=="download" && eligible(it,link) && it.optString("state") !in listOf("stored","cancelled","expired") &&
                        (pinTarget.isEmpty() || it.optLong("pinRevision")==pinRevision && it.optString("pinTarget")==pinTarget && it.optString("pinToken")==pinToken)}
                    if(existing!=null)JSONObject(existing.toString()) to false else {
                        val id=UUID.randomUUID().toString()
                        val fresh=JSONObject().put("id",id).put("localId","receive:$id").put("direction","download").put("name",data.name.take(220).ifBlank {"download.bin"})
                            .put("itemId",data.id).put("size",0).put("offset",0).put("state","waiting").put("target",link.target).put("serverId",link.serverId).put("created",System.currentTimeMillis())
                        if(pinTarget.isNotEmpty())fresh.put("pinWanted",true).put("pinTarget",pinTarget).put("pinRevision",pinRevision).put("pinToken",pinToken)
                        add(fresh);install(fresh);fresh to true
                    }
                }
                if(created)run(d.getString("id"))
                else if(!TransferTasks.running(d.getString("localId")))resume(d.getString("id"))
            }catch(ex:Exception){LanController.appendLog(ex.message ?: "无法创建下载恢复任务")}
        }
        return true
    }
    private class RemoteExpired:IOException("电脑原任务已清除或过期，请检查对端后取消并重新发送")
    private class RemotePause:IOException("电脑已暂停此任务")
    private class RemoteCancel:IOException("电脑已取消此任务")
    private suspend fun request(d:JSONObject,link:Link,method:String,op:String,offset:Long?=null,bytes:ByteArray?=null):Pair<JSONObject,ByteArray?> {
        val transferJob=currentCoroutineContext()[Job]
        val url="http://${link.host}:${link.port}/v1/tasks/${d.getString("id")}?op=$op"+(offset?.let {"&offset=$it"} ?: "")
        val body=when {bytes!=null->object:okhttp3.RequestBody(){
            override fun contentType():okhttp3.MediaType?=null
            override fun contentLength()=bytes.size.toLong()
            override fun writeTo(sink:okio.BufferedSink){var sent=0;while(sent<bytes.size){val count=minOf(65536,bytes.size-sent);TransferPolicyRuntime.payloadBlocking("upload",count){transferJob?.ensureActive()};sink.write(bytes,sent,count);sent+=count}}
        };op=="create"->JSONObject().put("direction",d.getString("direction")).put("name",d.getString("name"))
            .put("origin",d.optString("origin")).put("device",Proto.deviceLabel).put("size",d.optLong("size")).put("sha256",d.optString("sha256")).put("itemId",d.optString("itemId")).put("sourceRevision",d.optLong("pinRevision")).toString().toRequestBody("application/json".toMediaType());else->ByteArray(0).toRequestBody()}
        val builder=Request.Builder().url(url).header("X-Liuzhuan-Auth",link.auth).header("X-Liuzhuan-Client",Proto.clientId)
            .header("X-File-SHA256",d.optString("sha256")).header("X-Control-Sequence",d.optLong("generation").toString())
        if(bytes!=null)builder.header("X-Chunk-SHA256",MessageDigest.getInstance("SHA-256").digest(bytes).hexString())
        builder.method(method,if(method=="GET")null else body)
        val call=HttpClients.transfer.newCall(builder.build())
        return TransferWork.withCall(d.getString("localId"),call) {active->
            val response=if(d.optBoolean("automatic") && op !in listOf("cancel","pause")) {
                val result=java.util.concurrent.CompletableFuture<okhttp3.Response>()
                val permitted=com.liuzhuan.app.clipboard.ClipboardPrivacy.runIfAllowed(automaticEvent(d)) {
                    active.enqueue(object:okhttp3.Callback {
                        override fun onFailure(call:okhttp3.Call,e:IOException){result.completeExceptionally(e)}
                        override fun onResponse(call:okhttp3.Call,response:okhttp3.Response){result.complete(response)}
                    })
                }
                if(!permitted)throw IOException("隐私规则已拦截自动文件")
                result.get()
            }else active.execute()
            response.use {r->
            if(r.code==423)throw RemotePause();if(r.code==410)throw RemoteCancel()
            if(r.code==404 && d.optBoolean("remoteCreated"))throw RemoteExpired()
            if(!r.isSuccessful)throw IOException(when(r.code){409->"断点或任务身份不一致，请检查原任务";412->"电脑来源文件已变化，请取消后重新下载";422->"文件校验失败，未登记完成";else->"传输 HTTP ${r.code}，断点已保留"})
            if(op=="chunk" && method=="GET") {
                val payload=r.body?.byteStream()?.use {input->val out=java.io.ByteArrayOutputStream();val buffer=ByteArray(65536)
                    while(out.size()<=1048576){transferJob?.ensureActive();val count=input.read(buffer,0,minOf(buffer.size,1048577-out.size()));if(count<0)break;if(count==0)throw IOException("下载内容暂不可读")
                        TransferPolicyRuntime.payloadBlocking("download",count){transferJob?.ensureActive()};out.write(buffer,0,count)}
                    out.toByteArray()} ?: throw IOException("下载内容为空")
                if(payload.size>1048576 || MessageDigest.getInstance("SHA-256").digest(payload).hexString()!=r.header("X-Chunk-SHA256"))throw IOException("分块校验失败，断点未推进")
                JSONObject() to payload
            }else {
                val reply=JSONObject(r.body?.string() ?: "{}")
                if(reply.optString("serverId")!=link.serverId)throw IOException("电脑身份已变化，已停止恢复")
                reply to null
            }
        }}
    }
    private fun run(id:String) {
        withTaskLock(id) {
        val original=snapshot(id);val local=original.getString("localId")
        if(original.optString("state") !in listOf("waiting","active") || TransferTasks.running(local))return
        val d=update(id){it.put("generation",it.optLong("generation")+1)}
        val generation=d.optLong("generation")
        TransferTasks.start(d.getString("name"),if(d.getString("direction")=="upload")"手机 → 电脑" else "电脑 → 手机",local,
            cancel={control(id,if(TransferTasks.tasks.value.firstOrNull {it.id==local}?.cancelled==true)"cancelled" else "paused")},retry={resume(id)})
        TransferTasks.restoreProgress(local,d.optLong("offset"),d.optLong("size"))
        TransferWork.launch(local) {
            val workContext=currentCoroutineContext()
            try {
                val link=LanHub.client?.recoveryLink() ?: throw IOException("等待原电脑连接")
                if(d.optString("origin")=="directory")FolderSubscriptions.awaitReady()
                ensure(id,link)
                var info=if(snapshot(id).optBoolean("remoteCreated"))request(snapshot(id),link,"GET","status").first else request(snapshot(id),link,"POST","create").first
                if(d.optBoolean("pinWanted"))check(libraryTarget(link)==d.optString("pinTarget") && info.optLong("sourceRevision")==d.optLong("pinRevision")){"电脑原件版本已变化，请刷新素材后重新固定"}
                update(id){it.put("size",info.getLong("size")).put("sha256",info.getString("sha256")).put("remoteCreated",true)}
                if(snapshot(id).optBoolean("resume")){
                    info=request(snapshot(id),link,"POST","resume").first
                    update(id){it.put("resume",false).put("controlRevision",maxOf(it.optLong("controlRevision"),info.optLong("controlRevision")))}
                }
                if(info.optString("state")=="cancelled")throw RemoteCancel()
                if(info.optString("state")=="paused")throw RemotePause()
                ensure(id,link)
                if(snapshot(id).optLong("generation")!=generation)return@launch
                update(id){it.put("state","active")}
                TransferTasks.restoreProgress(local,if(d.getString("direction")=="upload")info.getLong("offset") else snapshot(id).optLong("offset"),info.getLong("size"))
                if(info.optString("state")!="stored") {
                    if(d.getString("direction")=="upload") {
                        var offset=info.getLong("offset");val file=File(root,id+".payload")
                        RandomAccessFile(file,"r").use {input->
                            check(input.length()==info.getLong("size")){"发送副本缺失或被修改，请重新选择文件"}
                            while(offset<input.length()) {
                                ensure(id,link);input.seek(offset);val bytes=ByteArray(minOf(1048576L,input.length()-offset).toInt());input.readFully(bytes)
                                info=request(snapshot(id),link,"PUT","chunk",offset,bytes).first
                                check(info.getLong("offset")==offset+bytes.size){"电脑断点确认不一致"};offset=info.getLong("offset")
                                update(id){it.put("offset",offset)};TransferTasks.progress(local,offset,input.length());PowerSignals.touch()
                            }
                        }
                    }else {
                        val file=File(root,id+".payload");var offset=snapshot(id).optLong("offset");val size=info.getLong("size")
                        RandomAccessFile(file,"rw").use {output->
                            check(output.length()>=offset){"下载断点文件缺失，请取消后重新下载"};output.setLength(offset)
                            if(root.usableSpace<size-offset+1048576)throw IOException("手机空间不足，断点已保留")
                            request(snapshot(id),link,"POST","ack",offset)
                            while(offset<size) {
                                ensure(id,link);val bytes=request(snapshot(id),link,"GET","chunk",offset).second!!
                                check(bytes.isNotEmpty() && bytes.size<=size-offset){"下载长度不正确"}
                                output.seek(offset);output.write(bytes);output.fd.sync();offset+=bytes.size
                                update(id){it.put("offset",offset)}
                                request(snapshot(id),link,"POST","ack",offset);TransferTasks.progress(local,offset,size);PowerSignals.touch()
                            }
                        }
                        val digest=MessageDigest.getInstance("SHA-256");file.inputStream().use {input->val b=ByteArray(65536);while(true){workContext.ensureActive();val n=input.read(b);if(n<0)break;if(n==0)throw IOException("来源暂未返回数据，请重试");digest.update(b,0,n)}}
                        check(digest.digest().hexString()==info.getString("sha256")){"完整文件校验失败，请取消后重新下载"}
                        publish(id,file)
                        notifyPinned(id,link,generation,file,info)
                    }
                    ensure(id,link);request(snapshot(id),link,"POST","complete")
                }
                else if(d.getString("direction")=="download" && d.optBoolean("pinWanted"))notifyPinned(id,link,generation,File(root,id+".payload"),info)
                update(id){it.put("state","stored").put("offset",info.getLong("size"))};File(root,id+".payload").delete()
                if(d.getString("direction")=="download")TransferTasks.savedTo(local,ReceivedFiles.FOLDER)
                TransferTasks.progress(local,info.getLong("size"),info.getLong("size"))
                TransferTasks.finish(local,true,if(d.getString("direction")=="upload")"电脑已持久登记" else "已校验并保存到手机")
            }catch(ex:Exception) {
                if(snapshot(id).optLong("generation")!=generation)return@launch
                val state=snapshot(id).optString("state")
                when {
                    d.optBoolean("automatic") && com.liuzhuan.app.clipboard.ClipboardPrivacy.blockReason(automaticEvent(d))!=null->{update(id){it.put("state","cancelled")};File(root,id+".payload").delete();cleanupPending(id);TransferTasks.markCancelled(local)}
                    ex is RemoteExpired->{update(id){it.put("state","expired")};TransferTasks.expired(local,ex.message!!);return@launch}
                    ex is RemotePause->{update(id){it.put("state","paused")};TransferTasks.markPaused(local)}
                    ex is RemoteCancel->{update(id){it.put("state","cancelled")};File(root,id+".payload").delete();cleanupPending(id);TransferTasks.markCancelled(local)}
                    state !in listOf("paused","cancelled","stored")->update(id){it.put("state","waiting")}
                }
                TransferTasks.finish(local,false,ex.message ?: "传输中断，断点已保留")
                if(ex is CancellationException)throw ex
            }
        }
    }
    }
    private suspend fun notifyPinned(id:String,link:Link,generation:Long,file:File,info:JSONObject) {
        val d=snapshot(id);if(!d.optBoolean("pinWanted"))return
        ensure(id,link);check(d.optLong("generation")==generation && libraryTarget(link)==d.optString("pinTarget")){"原素材库已变化"}
        val receiver=pinnedReceiver?:throw IOException("离线原件存储尚未就绪，下载断点已保留")
        check(file.exists() && file.length()==info.getLong("size")){"离线原件校验副本不可用，请重新固定"}
        val digest=MessageDigest.getInstance("SHA-256")
        file.inputStream().use {input->val buffer=ByteArray(65536);while(true){currentCoroutineContext().ensureActive();val count=input.read(buffer);if(count<0)break;if(count==0)throw IOException("原件暂不可读");digest.update(buffer,0,count)}}
        check(digest.digest().hexString()==info.getString("sha256")){"离线原件校验失败，未固定"}
        receiver(d.getString("pinTarget"),d.getString("itemId"),d.getLong("pinRevision"),d.getString("pinToken"),file,info.getLong("size"),info.getString("sha256"))
        ensure(id,link);check(snapshot(id).optLong("generation")==generation){"传输控制已变化"}
    }
    private fun findPending(id:String):Uri? {
        val collection=MediaStore.Downloads.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY)
        app.contentResolver.query(collection,arrayOf(MediaStore.MediaColumns._ID),
            "${MediaStore.MediaColumns.DISPLAY_NAME} = ? AND ${MediaStore.MediaColumns.RELATIVE_PATH} = ? AND ${MediaStore.MediaColumns.IS_PENDING} = 1",
            arrayOf(".liuzhuan-$id.pending",ReceivedFiles.FOLDER+"/"),null)?.use {cursor->
            if(cursor.moveToFirst())return android.content.ContentUris.withAppendedId(collection,cursor.getLong(0))
        }
        return null
    }
    private fun cleanupPending(id:String) {
        try {
            val d=snapshot(id);val resolver=app.contentResolver
            val uri=d.optString("savedUri").takeIf {it.isNotBlank()}?.let(Uri::parse) ?: findPending(id) ?: return
            resolver.query(uri,arrayOf(MediaStore.MediaColumns.IS_PENDING),null,null,null)?.use {cursor->
                if(cursor.moveToFirst() && cursor.getInt(0)==1)resolver.delete(uri,null,null)
            }
        }catch(_:Exception){LanController.appendLog("已取消传输；未完成接收文件暂未清理，请检查手机存储")}
    }
    private suspend fun publish(id:String,file:File) {
        var d=snapshot(id);val resolver=app.contentResolver;val local=d.getString("localId")
        val collection=MediaStore.Downloads.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY)
        val finalName=d.getString("name").substringAfterLast('/').substringAfterLast('\\')
        val pendingName=".liuzhuan-$id.pending"
        var uri=d.optString("savedUri").takeIf {it.isNotBlank()}?.let(Uri::parse)
        if(uri==null) {
            // A crash between MediaStore.insert and descriptor commit reuses our own unique pending row.
            uri=findPending(id)
            if(uri==null) {
                val values=ContentValues().apply {put(MediaStore.MediaColumns.DISPLAY_NAME,pendingName)
                    put(MediaStore.MediaColumns.MIME_TYPE,"application/octet-stream");put(MediaStore.MediaColumns.RELATIVE_PATH,ReceivedFiles.FOLDER);put(MediaStore.MediaColumns.IS_PENDING,1)}
                uri=resolver.insert(collection,values) ?: throw IOException("无法创建接收文件")
            }
            d=update(id){it.put("savedUri",uri.toString())}
        }
        val target=uri!!
        if(!d.optBoolean("published")) {
            var pending=true
            resolver.query(target,arrayOf(MediaStore.MediaColumns.IS_PENDING),null,null,null)?.use {cursor->
                check(cursor.moveToFirst()) {"接收文件已被删除，请取消后重新下载"};pending=cursor.getInt(0)!=0
            } ?: throw IOException("接收文件不可访问，请取消后重新下载")
            if(!pending) {
                val digest=MessageDigest.getInstance("SHA-256");var count=0L
                resolver.openInputStream(target)?.use {input->val b=ByteArray(65536);while(true){currentCoroutineContext().ensureActive();val n=input.read(b);if(n<0)break;if(n==0)throw IOException("来源暂未返回数据，请重试");digest.update(b,0,n);count+=n}}
                    ?: throw IOException("无法验证已发布接收文件")
                check(count==d.getLong("size") && digest.digest().hexString()==d.getString("sha256")){"已保存的文件被修改，已停止覆盖，请重新下载"}
            }else {
                val workContext=currentCoroutineContext()
                TransferWork.useResource(local,resolver.openFileDescriptor(target,"rwt") ?: throw IOException("无法写入接收文件")) {pfd->
                    android.os.ParcelFileDescriptor.AutoCloseOutputStream(pfd).use {output->
                        val digest=MessageDigest.getInstance("SHA-256");var written=0L
                        TransferWork.useResource(local,file.inputStream()) {input->val b=ByteArray(65536);while(true){workContext.ensureActive();val n=input.read(b);if(n<0)break;if(n==0)throw IOException("来源暂未返回数据，请重试");output.write(b,0,n);digest.update(b,0,n);written+=n}}
                        check(written==d.getLong("size") && digest.digest().hexString()==d.getString("sha256")){"最终保存校验失败，文件仍保持待完成状态"}
                        output.fd.sync()
                    }
                }
                check(resolver.update(target,ContentValues().apply {put(MediaStore.MediaColumns.DISPLAY_NAME,finalName);put(MediaStore.MediaColumns.IS_PENDING,0)},null,null)>0){"无法完成接收文件保存"}
            }
            update(id){it.put("published",true)}
        }
    }
}
