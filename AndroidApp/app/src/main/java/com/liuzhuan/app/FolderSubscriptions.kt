package com.liuzhuan.app

import android.content.Context
import android.database.ContentObserver
import android.net.Uri
import android.provider.DocumentsContract as Docs
import android.util.AtomicFile
import com.liuzhuan.app.core.SettingsStore
import com.liuzhuan.app.net.Proto
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.*
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import org.json.JSONArray
import org.json.JSONObject

/** Authorized source trees only. No polling, no destination picker and no all-files access. */
object FolderSubscriptions {
    data class Entry(val id: String, val uri: String, val name: String, val target: String, val seen: Map<String,String>)
    data class State(val entries: List<Entry> = emptyList(), val status: String = "首次跳过旧文件，只收集来源目录的直接子文件。", val unavailable: Boolean = false, val checking:Boolean=false)
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val gate = Mutex()
    private val scanning = Mutex()
    private val mutable = MutableStateFlow(State())
    val state = mutable.asStateFlow()
    private lateinit var app: Context
    private lateinit var storage: AtomicFile
    private val observers = mutableListOf<ContentObserver>()
    // Some DocumentsProviders own a FileObserver for the lifetime of a directory cursor.
    // Keep at most one cursor per explicitly granted tree while connected; close on stop/disconnect.
    private val observerCursors=mutableListOf<android.database.Cursor>()
    private var pending: Job? = null
    private var initialized = false
    private val ready=CompletableDeferred<Unit>()
    suspend fun awaitReady()=ready.await()
    private data class Document(val id: String, val uri: Uri, val stamp: String, val size: Long)
    fun target(settings: SettingsStore.Settings): String = if(settings.expectedDeviceId.isNotBlank() && settings.serverLibraryId.isNotBlank()) "library:${settings.expectedDeviceId}:${settings.serverLibraryId}:${Proto.sha256Hex(settings.password)}" else "${settings.serverIp.trim().lowercase()}:${settings.serverPort.toIntOrNull()?:8899}:${Proto.sha256Hex(settings.password)}"
    suspend fun rebindVerifiedTargetAwait(old:String,new:String) {
        ready.await();gate.withLock {
            if(old.isNotEmpty() && old!=new && !mutable.value.unavailable && mutable.value.entries.any {it.target==old})
                persist(mutable.value.entries.map {if(it.target==old)it.copy(target=new) else it})
        }
    }
    @Synchronized fun init(context: Context) {
        if (initialized) return
        initialized=true; app=context.applicationContext; storage=AtomicFile(java.io.File(app.filesDir,"source-folders.json"))
        scope.launch {
            gate.withLock {
                try {
                    if (storage.baseFile.exists() || java.io.File(storage.baseFile.path+".bak").exists()) {
                        if(storage.baseFile.length()>32*1024*1024) error("配置过大")
                        val arr=JSONArray(storage.openRead().bufferedReader().use { it.readText() })
                        require(arr.length()<=10)
                        val entries=(0 until arr.length()).map { i ->
                            val o=arr.getJSONObject(i);val seen=o.getJSONObject("seen");require(seen.length()<=5000)
                            Entry(o.getString("id"),o.getString("uri"),o.getString("name"),o.getString("target"),seen.keys().asSequence().associateWith { seen.getString(it) })
                        }
                        mutable.value=State(entries)
                    }
                } catch (_: Exception) { mutable.value=State(status="订阅配置无法读取，原文件已保留。",unavailable=true) }
            }
            ready.complete(Unit);observe(); requestScan()
        }
    }
    private fun persist(entries: List<Entry>) {
        check(!mutable.value.unavailable) { "订阅配置不可读取，不能覆盖原文件" }
        val arr=JSONArray();entries.forEach { e -> arr.put(JSONObject().put("id",e.id).put("uri",e.uri).put("name",e.name).put("target",e.target).put("seen",JSONObject(e.seen))) }
        val encoded=arr.toString().toByteArray()
        require(encoded.size<=32*1024*1024) { "订阅记录超过容量，请减少来源目录" }
        val output=storage.startWrite()
        try { output.write(encoded);storage.finishWrite(output) }catch(ex:Exception){storage.failWrite(output);throw ex}
        mutable.value=mutable.value.copy(entries=entries)
    }
    private fun read(tree: Uri): List<Document> {
        val root=Docs.getTreeDocumentId(tree)
        val children=Docs.buildChildDocumentsUriUsingTree(tree,root)
        val result=mutableListOf<Document>()
        app.contentResolver.query(children,arrayOf(Docs.Document.COLUMN_DOCUMENT_ID,Docs.Document.COLUMN_MIME_TYPE,Docs.Document.COLUMN_SIZE,Docs.Document.COLUMN_LAST_MODIFIED,Docs.Document.COLUMN_DISPLAY_NAME),null,null,null)?.use { c ->
            while(c.moveToNext()) {
                if(c.getString(1)==Docs.Document.MIME_TYPE_DIR) continue
                require(result.size<5000) { "目录超过 5000 个文件，请选择更小的来源目录" }
                val name=c.getString(4).orEmpty().lowercase()
                if(name.startsWith(".") || listOf(".tmp",".part",".crdownload").any { name.endsWith(it) }) continue
                val id=c.getString(0);val size=if(c.isNull(2)) -1 else c.getLong(2);val time=if(c.isNull(3)) 0 else c.getLong(3)
                result+=Document(id,Docs.buildDocumentUriUsingTree(tree,id),"$size:$time",size)
            }
        } ?: error("无法读取来源目录")
        return result
    }
    suspend fun add(uri: Uri) = withContext(Dispatchers.IO) {
        ready.await()
        val settings=SettingsStore(app).settings.first();val target=target(settings)
        check(LanHub.client?.collectionTarget()==target) { "请先连接要接收此目录文件的电脑" }
        val root=Docs.getTreeDocumentId(uri)
        val path=root.substringAfter(':',root).trim('/').lowercase()
        require(path.isNotBlank() && path!="download" && path!="downloads" && path!="download/liuzhuan" && !path.startsWith("download/liuzhuan/")) { "请选择独立的来源文件夹，不能订阅存储根目录、下载根目录或流转接收目录" }
        var name="来源目录"
        app.contentResolver.query(Docs.buildDocumentUriUsingTree(uri,root),arrayOf(Docs.Document.COLUMN_DISPLAY_NAME),null,null,null)?.use { if(it.moveToFirst()) name=it.getString(0) ?: name }
        require(!name.equals("Liuzhuan",true)) { "不能订阅流转接收目录" }
        gate.withLock {
            val entries=mutable.value.entries
            require(entries.size<10 && entries.none { it.uri==uri.toString() }) { "已订阅此目录，或已达到 10 个上限" }
            val documents=read(uri)
            check(LanHub.client?.collectionTarget()==target) { "电脑连接已变化，请重新授权订阅" }
            persist(entries+Entry(java.util.UUID.randomUUID().toString(),uri.toString(),name,target,documents.associate { it.id to it.stamp }))
            mutable.value=mutable.value.copy(status="订阅已开启，现有文件已跳过。只向授权时连接的电脑发送。")
        }
        observe()
    }
    suspend fun remove(id: String) = withContext(Dispatchers.IO) {
        ready.await()
        gate.withLock { persist(mutable.value.entries.filterNot { it.id==id }) }
        DurableTransfers.removeFolder(id)
        TransferTasks.tasks.value.filter { it.id.startsWith("folder:$id:") && it.running }.forEach { TransferWork.cancel(it.id) }
        observe()
    }
    private suspend fun observe() = gate.withLock {
        observers.forEach { runCatching { app.contentResolver.unregisterContentObserver(it) } };observers.clear()
        observerCursors.forEach { runCatching {it.close()} };observerCursors.clear()
        if(LanHub.client?.collectionTarget()==null) return@withLock
        mutable.value.entries.forEach { entry ->
            try {
                val tree=Uri.parse(entry.uri)
                val children=Docs.buildChildDocumentsUriUsingTree(tree,Docs.getTreeDocumentId(tree))
                val uris=mutableSetOf(children,Docs.buildDocumentUriUsingTree(tree,Docs.getTreeDocumentId(tree)))
                app.contentResolver.query(children,arrayOf(Docs.Document.COLUMN_DOCUMENT_ID),null,null,null)?.let { cursor ->
                    observerCursors+=cursor
                    cursor.notificationUris?.filter { it.authority==tree.authority }?.take(4)?.let { uris.addAll(it) }
                }
                for(uri in uris) {
                    val observer=object:ContentObserver(android.os.Handler(android.os.Looper.getMainLooper())) { override fun onChange(selfChange:Boolean) { requestScan() } }
                    try {app.contentResolver.registerContentObserver(uri,true,observer);observers+=observer}catch(_:Exception) { }
                }
            }catch(_:Exception) { mutable.value=mutable.value.copy(status="来源目录不可用，请检查授权后重试。") }
        }
    }
    fun connectionChanged() { if(initialized) scope.launch { observe();requestScan() } }
    @Synchronized fun requestScan() {
        if(!initialized)return
        pending?.cancel();pending=scope.launch { delay(1500);scan() }
    }
    fun scanNow() { if(initialized) scope.launch { scan() } }
    private suspend fun scan() {
        ready.await()
        if(!scanning.tryLock())return
        var more=false
        try {
            mutable.value=mutable.value.copy(checking=true,status="正在检查来源目录…")
            val target=LanHub.client?.collectionTarget() ?: run { mutable.value=mutable.value.copy(status="连接原电脑后再检查新文件。");return }
            if(mutable.value.unavailable)return
            var count=0;var failed=false
            for(entry in mutable.value.entries) {
                if(entry.target!=target) { failed=true;continue }
                try {
                    val files=read(Uri.parse(entry.uri));val candidates=files.filter { entry.seen[it.id]!=it.stamp && it.size>0 };val pendingFiles=candidates.take(16)
                    if(candidates.size>16)more=true
                    if(pendingFiles.isEmpty())continue
                    delay(1200)
                    val stable=read(Uri.parse(entry.uri)).associateBy { it.id }
                    for(doc in pendingFiles) {
                        if(stable[doc.id]?.stamp!=doc.stamp)continue
                        val valid={ LanHub.client?.collectionTarget()==target && mutable.value.entries.any { it.id==entry.id && it.target==target } }
                        if(!valid())break
                        gate.withLock {
                            val current=mutable.value.entries.firstOrNull { it.id==entry.id } ?: return@withLock
                            val existing=files.map { it.id }.toHashSet()
                            persist(mutable.value.entries.map { if(it.id==entry.id) it.copy(seen=current.seen.filterKeys { k->k in existing }+(doc.id to doc.stamp)) else it })
                        }
                        if(!valid())break
                        val done=CompletableDeferred<Boolean>()
                        uploadFileStreaming(app,doc.uri,SettingsStore(app),{_,_->},{ok,_->done.complete(ok)},"folder:${entry.id}:${java.util.UUID.randomUUID()}","directory",target,valid)
                        if(done.await()) count++ else failed=true
                    }
                } catch(ex:CancellationException){throw ex}
                catch(_:Exception){failed=true}
            }
            mutable.value=mutable.value.copy(status=if(failed) "已收集 $count 个文件；部分目录不可读、电脑不匹配或传输失败，请检查收发任务。失败文件不会自动反复发送。" else "检查完成，收集 $count 个文件。"+(if(more)"正在准备下一批…" else ""))
        }finally{mutable.value=mutable.value.copy(checking=false);scanning.unlock();if(more)scope.launch {delay(1500);scan()}}
    }
}
