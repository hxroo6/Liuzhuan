package com.liuzhuan.app

import android.content.Context
import android.content.Intent
import android.net.Uri
import com.liuzhuan.app.core.*
import com.liuzhuan.app.data.MaterialItem
import com.liuzhuan.app.net.Proto
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.io.File
import java.nio.file.Files
import java.util.concurrent.ConcurrentHashMap

/** Owns explicit original pins. It observes the existing reliable transfer engine, never downloads itself. */
object OfflineOriginals {
    data class State(val ready:Boolean=false,val unavailable:Boolean=false,val limit:Long=512L*1024*1024,val pins:List<OfflineOriginalPin> = emptyList(),val orphanBytes:Long=0,val error:String="") {
        val reserved get()=pins.sumOf {it.size}+orphanBytes
    }
    private val scope=CoroutineScope(SupervisorJob()+Dispatchers.IO+CoroutineExceptionHandler {_,_->
        LanController.appendLog("离线原件状态未能保存，原记录保留，请检查手机存储")
        mutable.value=mutable.value.copy(error="离线原件状态未能保存，请检查手机存储后重试")
    })
    private val gate=Mutex();private val ready=CompletableDeferred<Unit>()
    private val mutable=MutableStateFlow(State());val state=mutable.asStateFlow()
    private var catalog:OfflineOriginalCatalog?=null
    private lateinit var app:Context;private lateinit var root:File
    private var initialized=false
    private val requested=ConcurrentHashMap<String,Pair<String,Long>>()
    @Synchronized fun init(context:Context) {
        if(initialized)return
        initialized=true;app=context.applicationContext;root=File(app.noBackupFilesDir,"offline-originals")
        DurableTransfers.pinnedReceiver={target,itemId,revision,token,file,size,sha->acceptValidated(target,itemId,revision,token,file,size,sha)}
        scope.launch {gate.withLock {
            try {catalog=OfflineOriginalCatalog(EncryptedLibraryRecords(app),root);publish()}
            catch(_:Exception){mutable.value=State(ready=true,unavailable=true,error="离线原件记录不可读取，原文件保留，停止覆盖与自动整理")}
            finally{ready.complete(Unit)}
        }}
    }
    private fun publish(){val s=catalog?.snapshot()?:error("离线原件未就绪");mutable.value=State(true,limit=s.limit,pins=s.pins,orphanBytes=s.orphanBytes)}
    private fun target(epoch:String)=OfflineOriginalCatalog.target(epoch.split(':').take(2).joinToString(":"))
    suspend fun pin(item:MaterialItem,expectedEpoch:String)=withContext(Dispatchers.IO) {
        ready.await();val id=target(expectedEpoch)
        val pin=gate.withLock {
            check(MaterialLibrary.state.value.epoch==expectedEpoch&&MaterialLibrary.state.value.connected){"请连接原电脑后固定原件"}
            (catalog?:error("离线原件记录不可用")).pin(id,item,expectedEpoch.substringAfterLast(':')).also {publish()}
        }
        if(pin.state!="ready")request(pin,expectedEpoch)
    }
    suspend fun retry(key:String,expectedEpoch:String)=withContext(Dispatchers.IO) {
        ready.await();val pin=gate.withLock {
            val c=catalog?:error("离线原件记录不可用");val p=c.snapshot().pins.firstOrNull {it.key==key}?:error("原件已取消固定")
            check(MaterialLibrary.state.value.epoch==expectedEpoch&&MaterialLibrary.state.value.connected&&p.target==target(expectedEpoch)){"请连接原电脑和原素材库后重试"}
            c.waiting(key);publish();p
        };request(pin,expectedEpoch)
    }
    private suspend fun request(pin:OfflineOriginalPin,epoch:String) {
        check(MaterialLibrary.state.value.epoch==epoch&&MaterialLibrary.state.value.connected){"电脑连接已变化，固定请求保留，请重试"}
        requested[pin.key]=epoch to android.os.SystemClock.elapsedRealtime()
        withContext(Dispatchers.Main){
            if(MaterialLibrary.state.value.epoch==epoch&&MaterialLibrary.state.value.connected)LanHub.client?.requestItem(pin.itemId)
            else throw IllegalStateException("电脑连接已变化，请重试")
        }
    }
    /** Call inside the existing SessionItemData.apply guard, before normal receiveFile handling. */
    fun consumeItemData(data:Proto.ItemData,expectedEpoch:String):Boolean {
        val wanted=runCatching {target(expectedEpoch)}.getOrNull()?:return false
        val pin=mutable.value.pins.firstOrNull {it.target==wanted&&it.itemId==data.id&&requested[it.key]?.first==expectedEpoch}?:return false
        val request=requested.remove(pin.key)?:return false
        if(android.os.SystemClock.elapsedRealtime()-request.second>60_000) {
            scope.launch {gate.withLock {catalog?.fail(pin.key,"原件请求已超时，请重新连接原电脑后重试");publish()}}
            return true
        }
        if(data.error.isNotBlank()||data.downloadUrl.isBlank()) {scope.launch{gate.withLock {catalog?.fail(pin.key,"电脑原件不可读取，请刷新素材后重试");publish()}};return true}
        if(!DurableTransfers.downloadPinned(data,wanted,pin.sourceRevision,pin.token))scope.launch{gate.withLock{catalog?.fail(pin.key,"电脑尚未支持可靠原件下载，请更新两端后重试");publish()}}
        return true
    }
    suspend fun acceptValidated(target:String,itemId:String,revision:Long,token:String,file:File,size:Long,sha:String)=withContext(Dispatchers.IO) {
        ready.await();val key=OfflineOriginalCatalog.key(target,itemId,revision)
        val captured=gate.withLock {val c=catalog?:error("离线原件存储不可用");val pin=c.snapshot().pins.firstOrNull {it.key==key&&it.token==token}?:return@withContext;c to pin}
        val (c,pin)=captured
        if(pin.state=="ready"&&c.verify(key))return@withContext
        val context=currentCoroutineContext()
        val temp=c.prepareCopy(pin,file,size,sha) {context.ensureActive();mutable.value.pins.any {it.key==key&&it.token==token}}
        try {gate.withLock {if(mutable.value.pins.none {it.key==key&&it.token==token})return@withLock;c.commitCopy(key,temp,sha,token);publish()}}
        finally{c.discardCopy(temp)}
    }
    suspend fun unpin(key:String)=withContext(Dispatchers.IO) {ready.await();gate.withLock {
        requested.remove(key);val c=catalog?:error("离线原件不可用");val pin=c.snapshot().pins.firstOrNull {it.key==key}
        val removed=c.unpin(key);publish()
        pin?.let {DurableTransfers.cancelPinned(it.target,it.itemId,it.sourceRevision,it.token)}
        check(removed){"固定已取消，私有副本暂未删除，可在原件管理中清理失联副本"}
    }}
    suspend fun setLimit(bytes:Long)=withContext(Dispatchers.IO){ready.await();gate.withLock{(catalog?:error("离线原件不可用")).setLimit(bytes);publish()}}
    suspend fun cleanOrphans()=withContext(Dispatchers.IO){ready.await();gate.withLock{val n=(catalog?:error("离线原件不可用")).cleanOrphans();publish();n}}
    suspend fun verify(key:String)=withContext(Dispatchers.IO){ready.await();val c=gate.withLock{catalog?:error("离线原件不可用")};val context=currentCoroutineContext();c.verify(key){context.ensureActive();mutable.value.pins.any {it.key==key}}}
    suspend fun open(context:Context,key:String) {
        check(verify(key)){"私有原件已变化或丢失，请连接原电脑重新固定"}
        withContext(Dispatchers.Main){
            check(mutable.value.pins.any {it.key==key&&it.state=="ready"}){"此原件已取消固定"}
            context.startActivity(Intent(Intent.ACTION_VIEW).setDataAndType(Uri.parse("content://${context.packageName}.offline-originals/$key"),mime(key))
                .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK))
        }
    }
    fun mime(key:String):String {val p=mutable.value.pins.firstOrNull {it.key==key}?:return "application/octet-stream";return android.webkit.MimeTypeMap.getSingleton().getMimeTypeFromExtension(p.name.substringAfterLast('.',"").lowercase())?:"application/octet-stream"}
    internal fun authorizedFile(key:String):File? {
        if(!initialized||!Regex("[a-f0-9]{64}").matches(key))return null
        val pin=mutable.value.pins.firstOrNull {it.key==key&&it.state=="ready"}?:return null
        val file=File(root,"$key.bin")
        return file.takeIf {!Files.isSymbolicLink(it.toPath())&&it.canonicalFile.parentFile==root.canonicalFile&&it.isFile&&it.length()==pin.size}
    }
}
