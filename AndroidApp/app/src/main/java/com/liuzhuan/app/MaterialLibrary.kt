package com.liuzhuan.app

import com.liuzhuan.app.data.MaterialRepository
import com.liuzhuan.app.data.MaterialItem
import com.liuzhuan.app.data.toMaterialItem
import com.liuzhuan.app.net.LanClient
import com.liuzhuan.app.net.Proto
import kotlinx.coroutines.*
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.receiveAsFlow
import kotlinx.coroutines.flow.collectLatest
import org.json.JSONObject
import com.liuzhuan.app.core.*
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

/** One serial, event-driven page loader. UI items live only in MaterialRepository; no idle polling. */
object MaterialLibrary {
    data class SavedWorkspace(val target:String,val count:Int=0,val complete:Boolean=false,val saved:Long=0,val pending:Int=0,val error:String="")
    data class Query(val search:String="",val type:String="All",val favorite:Boolean=false,val group:String="",val tag:String="")
    data class State(val enabled:Boolean=false,val connected:Boolean=false,val loading:Boolean=false,val total:Int=0,val hasMore:Boolean=false,
        val query:Query=Query(),val groups:List<String> = emptyList(),val tags:List<String> = emptyList(),val error:String="",val epoch:String="",
        val canEditOffline:Boolean=false,val offlineCount:Int=0,val offlineComplete:Boolean=false,val offlineSaved:Long=0,
        val workBusy:Boolean=false,val workStatus:String="",val drafts:List<LibraryDraft> = emptyList(),
        val syncing:Boolean=false,val syncStatus:String="",val editStatus:String="",
        val savedSearches:List<SavedLibrarySearch> = emptyList(),val canSaveSearches:Boolean=false,val savedSearchError:String="")
    private val scope=CoroutineScope(SupervisorJob()+Dispatchers.IO)
    private val mutable=MutableStateFlow(State())
    val state=mutable.asStateFlow()
    private val signals=Channel<Unit>(Channel.CONFLATED)
    private val syncSignals=Channel<Unit>(Channel.CONFLATED)
    private val syncGuard=Any()
    private var pendingSync:CompletableDeferred<Unit>?=null
    @Volatile private var client:LanClient?=null
    @Volatile private var generation=0L
    private var more=false
    private var firstOffset=0
    private val work=Mutex()
    private val requests=Mutex()
    private var records:LibraryRecords?=null
    @Volatile private var workspace:LibraryWorkspace?=null
    private var workspaceTarget=""
    private var recentStamp=""
    // Connection identity is independent of query generations; an IO completion may never publish
    // a workspace merely because a newer connection has already disconnected again.
    @Volatile private var identityRevision=0L
    private var indexSave:Job?=null
    private var savedSearchStore:LibrarySavedSearches?=null
    private var savedSearchTarget=""
    private data class SearchStore(val store:LibrarySavedSearches?,val error:String="")
    private fun readSearchStore(disk:LibraryRecords,id:String)=try {SearchStore(LibrarySavedSearches(disk,id))}catch(ex:Exception){SearchStore(null,ex.message?:"常用搜索不可读取，原记录保留")}
    /** Owner lock + state monitor held by caller; corrupt preferences never disable the material library. */
    private fun setSearchStore(value:SearchStore,id:String) {savedSearchStore=value.store;savedSearchTarget=id;mutable.value=mutable.value.copy(savedSearchError=value.error)}
    private data class SyncContext(val client:LanClient,val workspace:LibraryWorkspace,val epoch:String)
    private data class SyncFlight(val context:SyncContext,val drafts:List<LibraryDraft>)
    private var inFlight:SyncFlight?=null // Only accessed while holding work; never held across network IO.
    private fun enqueueSync():Deferred<Unit> = synchronized(syncGuard) {
        val pending=pendingSync?:CompletableDeferred<Unit>().also {pendingSync=it}
        syncSignals.trySend(Unit);pending
    }
    private fun target(epoch:String):String=epoch.split(':').take(2).takeIf {it.size==2&&it.all {v->runCatching{java.util.UUID.fromString(v)}.isSuccess}}?.joinToString(":")?:""
    private fun savedTargets(disk:LibraryRecords):Pair<String,List<String>> {
        val registry=disk.read("library-r05-active")?.let(::JSONObject)?:return "" to emptyList()
        val arr=registry.optJSONArray("targets")?:org.json.JSONArray()
        require(arr.length()<=5){"离线库登记超出范围，原记录保留"}
        val targets=(0 until arr.length()).map {arr.getString(it)}
        require(targets.distinct().size==targets.size&&targets.all {target(it)==it}){"离线库登记不可读取，原记录保留"}
        return registry.optString("target") to targets
    }
    private fun saveTargets(disk:LibraryRecords,active:String,targets:List<String>) {
        disk.write("library-r05-active",JSONObject().put("target",active).put("targets",org.json.JSONArray().apply{targets.forEach {put(it)}}).toString())
    }
    fun attachRecords(value:LibraryRecords) {scope.launch {work.withLock {
        if(records!=null)return@withLock
        records=value
        val revision=synchronized(this@MaterialLibrary){identityRevision}
        try {
            val active=value.read("library-r05-active")?.let(::JSONObject)?.optString("target")?:""
            if(client==null&&active.isNotEmpty()) {
                val next=LibraryWorkspace(value,active)
                val searches=readSearchStore(value,active)
                synchronized(this@MaterialLibrary){if(client==null&&identityRevision==revision) {
                    workspace=next;workspaceTarget=active
                    MaterialRepository.setWindowLimit(LibraryBudget.MAX_ROWS);mutable.value=mutable.value.copy(enabled=true,epoch="$active:offline");setSearchStore(searches,active);updateWorkspaceState()
                    signals.trySend(Unit)
                }}
            }
        }catch(ex:Exception){message(ex.message?:"离线记录不可读取，原记录保留")}
    };client?.let {activate(it,mutable.value.epoch)}}}
    private fun updateWorkspaceState() {
        val w=workspace?.takeIf {workspaceTarget==target(mutable.value.epoch)};val info=w?.info()
        mutable.value=mutable.value.copy(canEditOffline=w!=null,offlineCount=info?.count?:0,offlineComplete=info?.complete?:false,
            offlineSaved=info?.saved?:0,drafts=w?.pending()?:emptyList(),canSaveSearches=savedSearchStore!=null&&savedSearchTarget==target(mutable.value.epoch),
            savedSearches=if(savedSearchTarget==target(mutable.value.epoch))savedSearchStore?.items()?:emptyList()else emptyList())
    }
    private fun message(text:String){synchronized(this){mutable.value=mutable.value.copy(error=text)}}
    private suspend fun request(c:LanClient,q:JSONObject,expectedEpoch:String)=requests.withLock {
        check(client===c&&mutable.value.epoch==expectedEpoch){"电脑会话已变化，操作未发送"}
        val expectedLibrary=target(expectedEpoch).substringAfter(':',"").ifBlank{c.libraryId}
        target(expectedEpoch).substringBefore(':',"").takeIf{it.isNotEmpty()}?.let{q.put("expectedDeviceId",it)}
        c.library(q.put("libraryId",expectedLibrary))
    }
    private fun activate(c:LanClient,identity:String) {scope.launch {work.withLock {
        val wanted=target(identity)
        val revision=synchronized(this@MaterialLibrary){if(client!==c||mutable.value.epoch!=identity)return@withLock;identityRevision}
        var next:LibraryWorkspace?=null;var failure=""
        try {
            val disk=records
            if(disk!=null&&wanted.isNotEmpty()) {
                val targets=savedTargets(disk).second.toMutableList()
                if(wanted !in targets){check(targets.size<5){"最多保留5个电脑素材库的离线记录，请先移除旧缓存"};targets+=wanted}
                next=workspace?.takeIf {workspaceTarget==wanted}?:LibraryWorkspace(disk,wanted)
                saveTargets(disk,wanted,targets)
            }
        }catch(ex:Exception){next=null;failure=ex.message?:"离线记录不可读取，停止离线修改"}
        val searches=records?.takeIf {next!=null&&wanted.isNotEmpty()}?.let {readSearchStore(it,wanted)}?:SearchStore(null)
        synchronized(this@MaterialLibrary){
            if(client!==c||mutable.value.epoch!=identity||identityRevision!=revision)return@withLock
            workspace=next;workspaceTarget=if(next!=null)wanted else ""
            setSearchStore(searches,wanted);updateWorkspaceState()
            if(failure.isNotEmpty())mutable.value=mutable.value.copy(error=failure)
        }
    };enqueueSync()}}
    @Synchronized fun firstRetainedOffset()=firstOffset
    @Synchronized fun latest() {firstOffset=0;if(client==null&&workspace!=null){generation++;more=false;mutable.value=mutable.value.copy(loading=true);signals.trySend(Unit)}else invalidated()}
    init {scope.launch {signals.receiveAsFlow().collectLatest {
        // Only this read action is superseded; the process actor and metadata writer stay alive.
        delay(250)
        val epoch=mutable.value.epoch;val version=generation
        try {load()}catch(ex:CancellationException) {
            // A request's withTimeout cancels its child, not this long-lived page actor.
            currentCoroutineContext().ensureActive()
            synchronized(this@MaterialLibrary){if(mutable.value.epoch==epoch&&generation==version)mutable.value=mutable.value.copy(loading=false,error=if(ex is TimeoutCancellationException)"素材库请求超时，请点刷新重试" else "素材库请求已中断，请点刷新重试")}
        }catch(ex:Exception) {
            synchronized(this@MaterialLibrary){if(mutable.value.epoch==epoch&&generation==version)mutable.value=mutable.value.copy(loading=false,error=ex.message?:"素材库未能加载，请点刷新重试")}
        }
    }};scope.launch {for(signal in syncSignals) {
        val completion=synchronized(syncGuard){pendingSync.also {pendingSync=null}}
        val epoch=mutable.value.epoch
        try {synchronizePending()}catch(ex:CancellationException) {
            // Preserve true parent cancellation, but keep consuming after a child request timeout.
            currentCoroutineContext().ensureActive()
            if(mutable.value.epoch==epoch)message(if(ex is TimeoutCancellationException)"整理同步请求超时，更改已保存在手机，可点击同步重试" else "整理同步已中断，更改已保存在手机，可点击同步重试")
        }
        catch(ex:Exception){message(ex.message?:"待同步操作未完成，更改仍保存在手机")}
        finally {completion?.complete(Unit)}
    }}}
    @Synchronized fun connected(value:LanClient,identity:String) {
        identityRevision++;indexSave?.cancel()
        firstOffset=0
        if(identity!=mutable.value.epoch)MaterialRepository.reset()
        if(target(identity)!=workspaceTarget){workspace=null;workspaceTarget="";savedSearchStore=null;savedSearchTarget=""}
        MaterialRepository.setWindowLimit(if(value.librarySupported)com.liuzhuan.app.core.LibraryBudget.MAX_ROWS else Int.MAX_VALUE)
        client=value;generation++;more=false
        mutable.value=mutable.value.copy(enabled=value.librarySupported,connected=true,epoch=identity,error="",loading=value.librarySupported,syncing=false,syncStatus="",editStatus="",savedSearchError="")
        updateWorkspaceState()
        if(value.librarySupported)signals.trySend(Unit)
        activate(value,identity)
    }
    @Synchronized fun disconnected() {identityRevision++;indexSave?.cancel();client=null;generation++;more=false;firstOffset=0;mutable.value=mutable.value.copy(connected=false,loading=false,syncing=false,syncStatus="");if(workspace!=null)signals.trySend(Unit)}
    @Synchronized fun query(value:Query) {
        if(value==mutable.value.query)return
        generation++;more=false;firstOffset=0;MaterialRepository.reset()
        mutable.value=mutable.value.copy(query=value,error="",loading=mutable.value.connected||workspace!=null)
        signals.trySend(Unit)
    }
    @Synchronized fun invalidated() {
        if(!mutable.value.enabled || !mutable.value.connected)return
        generation++;more=false;firstOffset=0;mutable.value=mutable.value.copy(loading=true);signals.trySend(Unit)
    }
    @Synchronized fun loadMore() {
        if(mutable.value.loading || !mutable.value.hasMore || (!mutable.value.connected&&workspace==null))return
        generation++;more=true;mutable.value=mutable.value.copy(loading=true);signals.trySend(Unit)
    }
    private suspend fun load() {
        if(client==null){loadOffline();return}
        val captured=synchronized(this){Triple(client?:return, generation, more)}
        val connection=captured.first;val version=captured.second;val append=captured.third
        val before=mutable.value
        if(!before.enabled || !before.connected)return
        val existing=if(append)MaterialRepository.materials.value else emptyList()
        val wanted=if(append)existing.size+100 else maxOf(100,MaterialRepository.materials.value.size)
        val start=if(append)synchronized(this){firstOffset}else 0
        val rows=existing.toMutableList();var offset=start+rows.size;var sequence=if(append)MaterialRepository.sequence() else -1L
        var response=JSONObject()
        try {
            do {
                val q=before.query
                response=request(connection,JSONObject().put("action","query").put("search",q.search).put("type",q.type).put("favorite",q.favorite)
                    .put("group",q.group).put("tag",q.tag).put("offset",offset).put("sequence",sequence),before.epoch)
                if(response.optBoolean("stale")){invalidated();return}
                if(response.optString("error").isNotBlank())error(response.getString("error"))
                if(response.optString("libraryId")!=connection.libraryId)error("素材库已变化，请重新连接")
                sequence=response.getLong("sequence")
                val page=Proto.parseListData(response).map {it.toMaterialItem()}
                val next=response.getInt("nextOffset")
                check(next>=offset && (page.isNotEmpty() || next==offset)){"分页响应无效"}
                rows.addAll(page);offset=next
                synchronized(this){if(generation!=version || client!==connection)return}
            }while(rows.size<wanted && offset<response.getInt("total"))
            synchronized(this) {
                if(generation!=version || client!==connection)return
                val retained=com.liuzhuan.app.core.LibraryBudget.retain(rows.distinctBy {it.id})
                firstOffset=start+retained.second
                MaterialRepository.replaceAll(retained.first,sequence)
                fun strings(key:String)=response.optJSONArray(key)?.let {a->(0 until a.length()).map {a.optString(it)}}?:emptyList()
                mutable.value=mutable.value.copy(loading=false,total=response.getInt("total"),hasMore=offset<response.getInt("total"),error="",groups=strings("groups"),tags=strings("tags"))
            }
            work.withLock {
                val stamp="${before.epoch}:$sequence:${MaterialRepository.materials.value.size}"
                if(client===connection&&generation==version&&target(before.epoch)==workspaceTarget&&before.query==Query()&&firstOffset==0&&stamp!=recentStamp)try{
                    workspace?.recent(MaterialRepository.materials.value,mutable.value.total);recentStamp=stamp;synchronized(this){updateWorkspaceState()}
                }
                catch(ex:Exception){message("离线摘要未保存：${ex.message?:"存储不可用"}")}
            }
        }catch(ex:CancellationException){throw ex}
        catch(ex:Exception){synchronized(this){if(generation==version && client===connection)mutable.value=mutable.value.copy(loading=false,error=ex.message?:"素材库未能加载，请重试")}}
    }
    suspend fun edit(item:MaterialItem,favorite:Boolean,group:String,tags:List<String>,note:String,expectedEpoch:String=mutable.value.epoch):JSONObject {
        check(expectedEpoch==mutable.value.epoch){"电脑会话已变化，请重新打开整理面板"}
        val c=client?:error("请先连接原电脑")
        val epoch=mutable.value.epoch
        val response=request(c,JSONObject().put("action","edit").put("id",item.id).put("revision",item.revision).put("favorite",favorite)
            .put("group",group).put("tags",org.json.JSONArray(tags)).put("note",note),epoch)
        check(client===c && mutable.value.epoch==epoch){"电脑连接已变化，未确认保存"}
        if(response.optString("error").isEmpty())invalidated()
        return response
    }
    private suspend fun loadOffline()=work.withLock {
        val w=workspace?:return@withLock
        val captured=synchronized(this){Triple(generation,mutable.value.query,more)}
        try {
            val offset=if(captured.third)firstOffset+MaterialRepository.materials.value.size else 0
            val q=captured.second;val page=w.query(q.search,q.type,q.favorite,q.group,q.tag,offset,current={client==null&&generation==captured.first})
            synchronized(this){if(client!=null||generation!=captured.first)return@withLock
                val rows=if(captured.third)MaterialRepository.materials.value+page.rows else page.rows
                val retained=LibraryBudget.retain(rows);firstOffset=(if(captured.third)firstOffset else 0)+retained.second
                MaterialRepository.replaceAll(retained.first,0)
                mutable.value=mutable.value.copy(loading=false,total=page.total,hasMore=offset+page.rows.size<page.total,groups=page.groups,tags=page.tags,error="")
            }
        }catch(ex:Exception){synchronized(this){if(client==null&&generation==captured.first){mutable.value=mutable.value.copy(loading=false);message(ex.message?:"离线索引读取失败，连接原电脑后重试")}}}
    }
    suspend fun stage(changes:List<Pair<MaterialItem,LibraryMetadata>>,expectedEpoch:String,editedFields:Map<String,Set<String>>?=null)=withContext(Dispatchers.IO) {
        work.withLock {
            val w=synchronized(this@MaterialLibrary){ownedWorkspace(expectedEpoch)}
            val active=inFlight?.takeIf {it.context.workspace.target==w.target}?.drafts?.map {it.id}?.toSet()?:emptySet()
            w.stage(changes,active,editedFields);synchronized(this@MaterialLibrary){if(workspace===w&&mutable.value.epoch==expectedEpoch){updateWorkspaceState();mutable.value=mutable.value.copy(editStatus=if(client==null)"已保存到手机 · 离线待同步" else "已保存到手机 · 等待电脑确认")}}
        }
        if(client!=null)enqueueSync()else signals.trySend(Unit)
    }
    /** Manual retry can await this pass; UI stage only enqueues and never owns the worker lifetime. */
    suspend fun syncPending(){enqueueSync().await()}
    private suspend fun synchronizePending() {
        val captured=work.withLock {
            val c=client?:return;val w=workspace?:return;val epoch=mutable.value.epoch
            if(target(epoch)!=workspaceTarget)return
            if(!c.libraryEditSupported){if(w.pending().isNotEmpty())message("待同步更改已保留，请更新电脑端至1.15.0或更新版本");return}
            SyncContext(c,w,epoch) to w.pending().filter {it.issue.isEmpty()}
        }
        val (context,waiting)=captured;val c=context.client;val w=context.workspace;val epoch=context.epoch
        fun current()=client===c&&mutable.value.epoch==epoch&&workspace===w
        if(waiting.isEmpty())return
        synchronized(this@MaterialLibrary){if(current())mutable.value=mutable.value.copy(syncing=true,syncStatus="正在同步整理更改…")}
        try {
            for(planned in waiting.chunked(10)) {
                val flight=work.withLock {
                    check(current()){"连接已变化，更改保留在原素材库"}
                    val tokens=w.pending().associateBy {it.id}
                    val batch=planned.filter {tokens[it.id]?.token==it.token&&tokens[it.id]?.issue.isNullOrEmpty()}
                    if(batch.isEmpty())null else SyncFlight(context,batch).also {inFlight=it}
                }?:continue
                try {
                    val result=request(c,JSONObject().put("action","merge_batch").put("edits",org.json.JSONArray().apply{flight.drafts.forEach{put(it.request())}}),epoch)
                    work.withLock {
                        check(current()&&result.optString("libraryId")==c.libraryId){result.optString("error").ifBlank{"未确认原素材库，更改保留"}}
                        w.acknowledge(flight.drafts,result);synchronized(this@MaterialLibrary){updateWorkspaceState()}
                    }
                }finally {work.withLock {if(inFlight===flight)inFlight=null}}
            }
            if(current())invalidated()
        }catch(ex:CancellationException){throw ex}
        catch(ex:Exception){if(current())message(ex.message?:"尚未确认同步，更改已保留")}
        finally{work.withLock {synchronized(this@MaterialLibrary){if(current()){updateWorkspaceState();mutable.value=mutable.value.copy(syncing=false,syncStatus="",editStatus=if(w.pending().isEmpty())"整理更改已同步到电脑" else "已保存到手机 · ${w.pending().size} 条待同步或核对")}}}}
    }
    suspend fun resolveDraft(id:String,keepMine:Boolean,expectedEpoch:String)=withContext(Dispatchers.IO) {
        work.withLock {val w=synchronized(this@MaterialLibrary){ownedWorkspace(expectedEpoch)};if(keepMine)w.rebase(id)else w.discard(id);synchronized(this@MaterialLibrary){if(workspace===w&&mutable.value.epoch==expectedEpoch)updateWorkspaceState()}}
        if(client!=null)enqueueSync()else signals.trySend(Unit)
    }
    fun startOfflineIndexSave(expectedEpoch:String) {scope.launch {
        try {saveOfflineIndex(expectedEpoch)}catch(ex:CancellationException){throw ex}catch(ex:Exception){if(mutable.value.epoch==expectedEpoch)message(ex.message?:"离线索引未能保存，原索引保留")}
    }}
    @Synchronized fun cancelOfflineIndexSave(){indexSave?.cancel(CancellationException("已取消保存离线索引，原索引与草稿保留"))}
    suspend fun saveOfflineIndex(expectedEpoch:String)=withContext(Dispatchers.IO) {
        val job=currentCoroutineContext().job
        val captured=work.withLock {synchronized(this@MaterialLibrary){
            val w=ownedWorkspace(expectedEpoch);val c=client?:error("请连接原电脑保存离线索引")
            check(c.libraryEditSupported){"请更新两端并连接原电脑"}
            check(indexSave?.isActive!=true){"已有离线索引正在保存，请稍候或取消"}
            indexSave=job;mutable.value=mutable.value.copy(workBusy=true,workStatus="准备离线索引…")
            Triple(c,w,identityRevision)
        }}
        val (c,w,revision)=captured;val epoch=expectedEpoch
        fun current()=client===c&&mutable.value.epoch==epoch&&workspace===w&&workspaceTarget==target(epoch)&&identityRevision==revision
        var prepared:LibraryWorkspace.PreparedIndex?=null
        var base:LibraryWorkspace.ChangeBase?=null
        try {
            base=work.withLock {synchronized(this@MaterialLibrary){check(current()){"连接已变化，旧索引保留"}};w.beginChanges()}
            base?.let {previous->prepared=w.prepareChanges(previous,fetch={since,run,until->
                check(current()){"电脑连接已变化，旧索引保留"}
                val response=request(c,JSONObject().put("action","changes").put("since",since).put("serverRun",run).put("until",until),epoch)
                check(current()&&response.optString("libraryId")==c.libraryId){"素材库已变化，旧索引保留"};response
            },progress={done,total->synchronized(this@MaterialLibrary){if(current())mutable.value=mutable.value.copy(workStatus="合并离线更新 $done / $total")}})}
            if(prepared==null)prepared=w.prepareDownload(fetch={offset,sequence->
                check(current()){"连接已变化，旧索引保留"}
                val response=request(c,JSONObject().put("action","query").put("includeText",true).put("offset",offset).put("sequence",sequence),epoch)
                check(current()&&response.optString("libraryId")==c.libraryId){"素材库已变化，旧索引保留"};response
            },progress={done,total->synchronized(this@MaterialLibrary){if(current())mutable.value=mutable.value.copy(workStatus="保存离线索引 $done / $total")}})
            currentCoroutineContext().ensureActive()
            work.withLock {
                synchronized(this@MaterialLibrary){check(current()){"素材库已变化，旧索引保留"}}
                w.commitPrepared(prepared?:error("索引准备结果丢失"))
                synchronized(this@MaterialLibrary){if(current())updateWorkspaceState()}
            }
        }finally{
            withContext(NonCancellable){work.withLock {prepared?.let(w::discardPrepared);base?.let(w::endChanges)}}
            synchronized(this@MaterialLibrary){if(indexSave===job){indexSave=null;mutable.value=mutable.value.copy(workBusy=false,workStatus="");if(current())updateWorkspaceState()}}
        }
    }
    /** Caller holds the state monitor; even an offline editor must identify its exact workspace. */
    private fun ownedWorkspace(expectedEpoch:String):LibraryWorkspace {
        check(expectedEpoch==mutable.value.epoch&&target(expectedEpoch).isNotEmpty()&&workspaceTarget==target(expectedEpoch)){"素材库已变化，请重新打开整理面板"}
        return workspace?.takeIf {it.target==workspaceTarget}?:error("离线工作区未就绪，请连接新版电脑后重试")
    }
    suspend fun clearOfflineIndex(expectedEpoch:String)=withContext(Dispatchers.IO){work.withLock{val w=synchronized(this@MaterialLibrary){ownedWorkspace(expectedEpoch)};w.clearIndex();synchronized(this@MaterialLibrary){if(workspace===w&&mutable.value.epoch==expectedEpoch)updateWorkspaceState()}};if(client==null)signals.trySend(Unit)}
    suspend fun saveSearch(name:String,expectedEpoch:String,id:String?=null,query:Query=mutable.value.query)=withContext(Dispatchers.IO){work.withLock {
        check(expectedEpoch==mutable.value.epoch){"素材库已变化，请重新打开常用搜索"}
        val store=savedSearchStore?.takeIf {savedSearchTarget==target(expectedEpoch)}?:error("常用搜索不可用，请检查记录或连接原电脑")
        store.save(name,query.search,query.type,query.favorite,query.group,query.tag,id)
        synchronized(this@MaterialLibrary){updateWorkspaceState()}
    }}
    suspend fun deleteSearch(id:String,expectedEpoch:String)=withContext(Dispatchers.IO){work.withLock {
        check(expectedEpoch==mutable.value.epoch){"素材库已变化，请重新打开常用搜索"}
        val store=savedSearchStore?.takeIf {savedSearchTarget==target(expectedEpoch)}?:error("常用搜索不可读取，原记录保留")
        store.delete(id);synchronized(this@MaterialLibrary){updateWorkspaceState()}
    }}
    suspend fun applySearch(id:String,expectedEpoch:String) {
        val value=work.withLock {
            check(expectedEpoch==mutable.value.epoch){"素材库已变化，请重新打开常用搜索"}
            val store=savedSearchStore?.takeIf {savedSearchTarget==target(expectedEpoch)}?:error("常用搜索不可用")
            store.items().firstOrNull {it.id==id}?:error("此常用搜索已被移除，请刷新")
        }
        synchronized(this@MaterialLibrary){check(expectedEpoch==mutable.value.epoch){"素材库已变化，未应用常用搜索"};query(Query(value.search,value.type,value.favorite,value.group,value.tag))}
    }
    suspend fun savedWorkspaces():List<SavedWorkspace> = withContext(Dispatchers.IO){work.withLock {
        val disk=records?:error("离线记录正在初始化，请稍后重试")
        savedTargets(disk).second.map {id->try {
            val w=workspace?.takeIf {workspaceTarget==id}?:LibraryWorkspace(disk,id);val info=w.info()
            SavedWorkspace(id,info.count,info.complete,info.saved,w.pending().size)
        }catch(ex:Exception){SavedWorkspace(id,error=ex.message?:"记录不可读取，保留原数据并停止移除")}}
    }}
    suspend fun selectOfflineWorkspace(id:String)=withContext(Dispatchers.IO){work.withLock {
        val expected=synchronized(this@MaterialLibrary){check(client==null){"请先断开电脑，再切换离线素材库"};generation}
        val disk=records?:error("离线记录正在初始化");val targets=savedTargets(disk).second
        check(id in targets){"离线库登记已变化，请刷新列表"}
        val next=LibraryWorkspace(disk,id);val searches=readSearchStore(disk,id);saveTargets(disk,id,targets)
        synchronized(this@MaterialLibrary) {
            check(client==null&&generation==expected){"电脑连接已变化，未切换离线素材库"}
            workspace=next;workspaceTarget=id;generation++;more=false;firstOffset=0;MaterialRepository.reset()
            mutable.value=mutable.value.copy(enabled=true,connected=false,epoch="$id:offline",query=Query(),loading=true,error="",total=0,hasMore=false)
            setSearchStore(searches,id)
            updateWorkspaceState();signals.trySend(Unit)
        }
    }}
    suspend fun removeSavedWorkspace(id:String)=withContext(Dispatchers.IO) {
        work.withLock {
            val disk=records?:error("离线记录正在初始化");val (active,targets)=savedTargets(disk)
            check(id in targets){"离线库登记已变化，请刷新列表"}
            val w=workspace?.takeIf {workspaceTarget==id}?:LibraryWorkspace(disk,id)
            check(w.pending().isEmpty()){"此离线库有待同步草稿，请先打开处理；原电脑离线时也不能直接移除"}
            w.remove();LibrarySavedSearches.remove(disk,id);val remaining=targets.filterNot {it==id};saveTargets(disk,active.takeIf {it in remaining}?:"",remaining)
            synchronized(this@MaterialLibrary) {
                if(workspaceTarget==id) {
                    workspace=null;workspaceTarget="";savedSearchStore=null;savedSearchTarget="";generation++;more=false;firstOffset=0
                    if(client==null){MaterialRepository.reset();mutable.value=mutable.value.copy(epoch="",total=0,hasMore=false,loading=false,error="")}
                    updateWorkspaceState()
                }
            }
        }
        // A sixth connected library can claim the freed slot without another scan/reconnect.
        client?.takeIf {workspace==null&&target(mutable.value.epoch)!=id}?.let {activate(it,mutable.value.epoch)}
    }
    suspend fun forgetOfflineWorkspace(expectedEpoch:String) {
        val id=work.withLock {check(expectedEpoch==mutable.value.epoch){"素材库已变化，请重新确认"};workspaceTarget.takeIf {it.isNotEmpty()}?:error("没有当前离线库")}
        removeSavedWorkspace(id)
    }
}
