package com.liuzhuan.app.core

import com.liuzhuan.app.data.MaterialItem
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.IOException
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.security.MessageDigest
import java.util.UUID

/** Explicit pins only. Metadata is encrypted by LibraryRecords; payloads stay in noBackupFilesDir. */
data class OfflineOriginalPin(val key:String,val target:String,val itemId:String,val sourceRevision:Long,val name:String,val type:String,
    val size:Long,val state:String="waiting",val sha256:String="",val error:String="",val saved:Long=0,val token:String=UUID.randomUUID().toString(),val sourceRun:String="")
class OfflineOriginalCatalog(private val records:LibraryRecords,private val root:File) {
    data class Snapshot(val limit:Long,val pins:List<OfflineOriginalPin>,val orphanBytes:Long=0) {val reserved get()=pins.sumOf {it.size}+orphanBytes}
    private val record="library-r05-originals"
    private var limit=512L*1024*1024
    private var pins:List<OfflineOriginalPin> = emptyList()
    private val activeCopies=java.util.concurrent.ConcurrentHashMap.newKeySet<String>()
    init {
        require(!Files.isSymbolicLink(root.toPath())){"离线原件目录不可使用链接"};root.mkdirs();require(root.isDirectory)
        records.read(record)?.let {raw->
            require(raw.toByteArray().size<=1024*1024){"离线原件记录过大，原记录保留"}
            val o=JSONObject(raw);require(o.getInt("schema")==1);limit=o.getLong("limit");checkLimit(limit)
            val arr=o.getJSONArray("pins");require(arr.length()<=200)
            pins=(0 until arr.length()).map {i->val d=arr.getJSONObject(i)
                OfflineOriginalPin(d.getString("key"),d.getString("target"),d.getString("itemId"),d.getLong("sourceRevision"),d.getString("name"),d.getString("type"),d.getLong("size"),d.getString("state"),d.optString("sha256"),d.optString("error"),d.optLong("saved"),d.getString("token"),d.optString("sourceRun")).also(::validate)
            };require(pins.map {it.key}.distinct().size==pins.size&&pins.sumOf {it.size}<=limit)
        }
    }
    private fun checkLimit(value:Long)=require(value in 128L*1024*1024..2L*1024*1024*1024){"离线原件配额应为128MB–2GB"}
    private fun validate(p:OfflineOriginalPin) {
        require(p.key==key(p.target,p.itemId,p.sourceRevision)&&p.name.length in 1..256&&p.size in 0..2L*1024*1024*1024)
        require(p.type in setOf("Text","Image","Video","Audio","Other")&&p.state in setOf("waiting","ready","error"))
        require(p.error.length<=240&&(p.state!="ready"||Regex("[a-f0-9]{64}").matches(p.sha256)))
        require(UUID.fromString(p.token).toString()==p.token&&p.sourceRun.length<=128)
    }
    private fun file(key:String):File {require(Regex("[a-f0-9]{64}").matches(key));return File(root,"$key.bin").also {require(!Files.isSymbolicLink(it.toPath())&&it.canonicalFile.parentFile==root.canonicalFile)}}
    private fun persist(next:List<OfflineOriginalPin>,nextLimit:Long=limit) {
        require(next.size<=200&&next.sumOf {it.size}<=nextLimit);next.forEach(::validate)
        val arr=JSONArray();next.forEach {p->arr.put(JSONObject().put("key",p.key).put("target",p.target).put("itemId",p.itemId).put("sourceRevision",p.sourceRevision).put("name",p.name).put("type",p.type).put("size",p.size).put("state",p.state).put("sha256",p.sha256).put("error",p.error).put("saved",p.saved).put("token",p.token).put("sourceRun",p.sourceRun))}
        records.write(record,JSONObject().put("schema",1).put("limit",nextLimit).put("pins",arr).toString());pins=next;limit=nextLimit
    }
    private fun orphans():List<File> {
        val known=pins.map {it.key}.toSet();val files=root.listFiles()?.toList()?:error("离线原件目录不可读取")
        require(files.size<=1000){"离线原件临时文件过多，请先清理失联副本"}
        return files.filter {f->val unbound=Regex("[a-f0-9]{64}\\.bin").matches(f.name)&&f.name.removeSuffix(".bin") !in known
            val interrupted=Regex("[a-f0-9]{64}\\.partial-[a-f0-9-]{36}").matches(f.name)&&f.name !in activeCopies
            (unbound||interrupted)&&!Files.isSymbolicLink(f.toPath())&&f.isFile}
    }
    fun snapshot()=Snapshot(limit,pins.toList(),orphans().sumOf {it.length()})
    fun setLimit(value:Long){checkLimit(value);require(snapshot().reserved<=value){"现有固定项及预留超过新配额，请先取消部分固定"};persist(pins,value)}
    fun pin(target:String,item:MaterialItem,sourceRun:String=""):OfflineOriginalPin {
        require(item.hasFile&&!item.sourceMissing&&item.size>=0&&item.name.isNotBlank()){"请选择在线可用且大小已知的文件原件"}
        val id=key(target,item.id,item.sourceRevision);pins.firstOrNull {it.key==id}?.let {return it}
        require(snapshot().reserved+item.size<=limit){"离线原件配额不足，请取消部分固定或提高配额"}
        val p=OfflineOriginalPin(id,target,item.id,item.sourceRevision,item.name.take(256),item.type,item.size,sourceRun=sourceRun)
        persist(pins+p);return p
    }
    fun fail(key:String,message:String){val p=pins.firstOrNull {it.key==key}?:return;if(p.state!="ready")persist(pins.map {if(it.key==key)p.copy(state="error",error=message.take(240))else it})}
    fun waiting(key:String){persist(pins.map {if(it.key==key&&it.state!="ready")it.copy(state="waiting",error="")else it})}
    fun unpin(key:String):Boolean {if(pins.none {it.key==key})return true;persist(pins.filterNot {it.key==key});val f=file(key);return !f.exists()||f.delete()}
    fun cleanOrphans():Int {var n=0;orphans().forEach {if(it.delete())n++};return n}
    fun readyFile(key:String):File?=pins.firstOrNull {it.key==key&&it.state=="ready"}?.let {p->file(key).takeIf {it.isFile&&it.length()==p.size}}
    /** Copy outside the owner lock; current() also checks explicit unpin and coroutine cancellation. */
    fun prepareCopy(pin:OfflineOriginalPin,source:File,size:Long,sha256:String,current:()->Boolean):File {
        require(size==pin.size&&Regex("[a-f0-9]{64}").matches(sha256)){"原件版本或大小已变化，请刷新素材后重试"}
        require(source.isFile&&source.length()==size&&root.usableSpace>=size+1048576){"手机空间不足以保留私有原件副本"}
        val temp=File(root,"${pin.key}.partial-${UUID.randomUUID()}")
        activeCopies+=temp.name
        try {
            val digest=MessageDigest.getInstance("SHA-256");var count=0L
            source.inputStream().use {input->temp.outputStream().use {output->val b=ByteArray(65536)
                while(true){check(current()){"固定已取消或原件请求已变化"};val n=input.read(b);if(n<0)break;if(n==0)throw IOException("原件暂不可读取")
                    count+=n;require(count<=size);output.write(b,0,n);digest.update(b,0,n)
                };output.fd.sync()
            }}
            require(count==size&&digest.digest().joinToString(""){"%02x".format(it)}==sha256){"私有原件校验失败，未标记离线可用"}
            return temp
        }catch(t:Throwable){discardCopy(temp);throw t}
    }
    fun discardCopy(temp:File) {require(temp.canonicalFile.parentFile==root.canonicalFile&&Regex("[a-f0-9]{64}\\.partial-[a-f0-9-]{36}").matches(temp.name));temp.delete();activeCopies-=temp.name}
    fun commitCopy(key:String,temp:File,sha256:String,token:String) {
        val pin=pins.firstOrNull {it.key==key}?:error("此原件已取消固定")
        check(pin.token==token){"固定请求已变化，旧副本不会关联新请求"}
        require(temp.canonicalFile.parentFile==root.canonicalFile&&temp.name.startsWith("$key.partial-")&&!Files.isSymbolicLink(temp.toPath())&&temp.length()==pin.size)
        try {
            Files.move(temp.toPath(),file(key).toPath(),StandardCopyOption.ATOMIC_MOVE,StandardCopyOption.REPLACE_EXISTING)
            persist(pins.map {if(it.key==key)it.copy(state="ready",sha256=sha256,error="",saved=System.currentTimeMillis())else it})
        }finally {activeCopies-=temp.name}
    }
    fun verify(key:String,current:()->Boolean={true}):Boolean {
        val p=pins.firstOrNull {it.key==key&&it.state=="ready"}?:return false;val f=readyFile(key)?:return false
        val d=MessageDigest.getInstance("SHA-256");f.inputStream().use {input->val b=ByteArray(65536);while(true){check(current()){"已取消校验"};val n=input.read(b);if(n<0)break;if(n==0)throw IOException("原件暂不可读取");d.update(b,0,n)}}
        return d.digest().joinToString(""){"%02x".format(it)}==p.sha256
    }
    companion object {
        fun target(value:String):String {val p=value.split(':');require(p.size==2&&p.all {runCatching{UUID.fromString(it).toString()==it}.getOrDefault(false)}){"需要已认证的电脑与素材库身份"};return value}
        fun key(target:String,itemId:String,sourceRevision:Long):String {
            target(target);require(itemId.length in 1..256&&sourceRevision>=0)
            return MessageDigest.getInstance("SHA-256").digest("$target|${itemId.length}:$itemId|$sourceRevision".toByteArray()).joinToString(""){"%02x".format(it)}
        }
    }
}
