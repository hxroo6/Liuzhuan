package com.liuzhuan.app.core

import com.liuzhuan.app.data.MaterialItem
import com.liuzhuan.app.data.toMaterialItem
import com.liuzhuan.app.net.Proto
import org.json.JSONArray
import org.json.JSONObject
import java.security.MessageDigest
import java.util.UUID
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive

/** IO-dispatcher only. Immutable encrypted page generations are published by one atomic manifest. */
interface LibraryRecords {
    fun read(name:String):String?
    fun write(name:String,text:String)
    fun delete(name:String)
    fun names():List<String>
}
data class LibraryMetadata(val favorite:Boolean,val group:String,val tags:List<String>,val note:String) {
    fun json()=JSONObject().put("favorite",favorite).put("group",group).put("tags",JSONArray().apply{tags.forEach {put(it)}}).put("note",note)
    fun apply(item:MaterialItem)=item.copy(favorite=favorite,group=group,tags=tags,note=note)
    fun sameField(other:LibraryMetadata,field:String)=when(field){"favorite"->favorite==other.favorite;"group"->group==other.group;"tags"->tags.size==other.tags.size&&tags.indices.all {tags[it].equals(other.tags[it],true)};"note"->note==other.note;else->error("未知整理字段")}
    companion object {
        val FIELDS=setOf("favorite","group","tags","note")
        fun of(item:MaterialItem)=LibraryMetadata(item.favorite,item.group,item.tags,item.note)
        fun parse(value:JSONObject):LibraryMetadata {
            val group=value.getString("group").trim();val note=value.getString("note")
            val arr=value.getJSONArray("tags");require(arr.length()<=20){"最多20个标签"}
            val tags=(0 until arr.length()).map {arr.getString(it).trim()}.filter {it.isNotEmpty()}.distinctBy {it.lowercase()}
            require(group.length<=48&&group.none {it.isISOControl()}&&note.length<=4000){"分组或备注超出范围"}
            require(tags.all {it.length<=32&&it.none {c->c.isISOControl()||c in ",，;；"}}){"标签无效"}
            return LibraryMetadata(value.getBoolean("favorite"),group,tags,note)
        }
    }
}
data class LibraryDraft(val token:String,val base:MaterialItem,val desired:LibraryMetadata,val issue:String="",val remote:MaterialItem?=null,val revertedFields:Set<String> = emptySet()) {
    val id get()=base.id
    fun changes(field:String)=field in revertedFields||!desired.sameField(LibraryMetadata.of(base),field)
    /** New server fields remain visible unless this durable draft actually edits them. */
    fun overlay(item:MaterialItem)=LibraryMetadata(if(changes("favorite"))desired.favorite else item.favorite,if(changes("group"))desired.group else item.group,
        if(changes("tags"))desired.tags else item.tags,if(changes("note"))desired.note else item.note).apply(item)
    fun request()=JSONObject().put("id",id).put("base",LibraryMetadata.of(base).json()).put("desired",desired.json())
    fun json()=JSONObject().put("token",token).put("base",LibraryWorkspace.itemJson(base)).put("desired",desired.json()).put("issue",issue)
        .put("revertedFields",JSONArray().apply {revertedFields.forEach {put(it)}})
        .apply {remote?.let {put("remote",LibraryWorkspace.itemJson(it))}}
}
data class LibraryIndex(val pages:List<String> = emptyList(),val count:Int=0,val total:Int=0,val complete:Boolean=false,val saved:Long=0,val sequence:Long=-1,val serverRun:String="")

class LibraryWorkspace(private val records:LibraryRecords,val target:String) {
    init {require(target.length in 1..160&&target.count {it==':'}==1){"需要已鉴权的电脑和素材库身份"}}
    private val prefix="library-r05-"+MessageDigest.getInstance("SHA-256").digest(target.toByteArray()).joinToString(""){"%02x".format(it)}
    private val pagePattern=Regex(Regex.escape(prefix)+"-page-[a-f0-9]{32}-[0-9]+")
    private var index=readIndex()
    private var drafts=readDrafts()
    @Volatile private var indexRevision=0L
    private val transientPages=java.util.concurrent.ConcurrentHashMap.newKeySet<String>()
    private val leasedPages=java.util.concurrent.ConcurrentHashMap<String,Int>()
    class PreparedIndex internal constructor(internal val owner:LibraryWorkspace,internal val index:LibraryIndex,internal val revision:Long) {
        internal var consumed=false
    }
    class ChangeBase internal constructor(internal val owner:LibraryWorkspace,internal val index:LibraryIndex,internal val revision:Long)
    /** Owner mutex held: lease old immutable pages before acknowledgements may replace them. */
    fun beginChanges():ChangeBase? {
        if(!index.complete||index.sequence<0||index.serverRun.isBlank())return null
        index.pages.forEach {name->leasedPages.compute(name){_,n->(n?:0)+1}};return ChangeBase(this,index,indexRevision)
    }
    fun endChanges(base:ChangeBase) {require(base.owner===this);base.index.pages.forEach {name->leasedPages.computeIfPresent(name){_,n->if(n>1)n-1 else null};if(name !in index.pages&&!leasedPages.containsKey(name))runCatching{records.delete(name)}}}
    // Cache positions rather than rows/text: at most 50,000 Ints (~200KB), one query only.
    // Every index publication and durable draft edit invalidates the same-generation cache.
    private data class QueryKey(val search:String,val type:String,val favorite:Boolean,val group:String,val tag:String)
    private data class PageMatches(val name:String,val positions:IntArray,val first:Int)
    private data class QueryCache(val key:QueryKey,val pages:List<PageMatches>,val total:Int,val groups:List<String>,val tags:List<String>)
    private var queryCache:QueryCache?=null
    // Classification/group switches use compact metadata, never cached full text or bitmaps.
    // The byte estimate includes repeated strings; larger libraries keep the bounded scan path.
    private data class FacetRow(val page:String,val position:Int,val id:String,val type:String,val favorite:Boolean,val group:String,val tags:List<String>)
    private var facets:List<FacetRow>?=null
    private fun facetBytes(value:FacetRow)=64L+2L*(value.id.length+value.type.length+value.group.length+value.tags.sumOf {it.length})
    fun info()=index
    fun pending()=drafts.toList()
    fun overlay(item:MaterialItem)=drafts.firstOrNull {it.id==item.id}?.overlay(item)?:item
    private fun readIndex():LibraryIndex {
        val raw=records.read("$prefix-meta")?:return LibraryIndex()
        val value=JSONObject(raw);require(value.getString("target")==target)
        val a=value.getJSONArray("pages");require(a.length()<=500)
        val pages=(0 until a.length()).map {a.getString(it)};require(pages.all {pagePattern.matches(it)}&&pages.distinct().size==pages.size)
        val count=value.getInt("count");val total=value.getInt("total");require(count in 0..50000&&total>=count)
        return LibraryIndex(pages,count,total,value.getBoolean("complete"),value.getLong("saved"),value.optLong("sequence",-1),value.optString("serverRun"))
    }
    private fun readDrafts():List<LibraryDraft> {
        val raw=records.read("$prefix-drafts")?:return emptyList()
        val a=JSONArray(raw);require(a.length()<=500)
        val result=(0 until a.length()).map {i->val v=a.getJSONObject(i)
            val fields=v.optJSONArray("revertedFields");require(fields==null||fields.length()<=4)
            val reverted=fields?.let {arr->(0 until arr.length()).map {arr.getString(it)}.toSet()}?:emptySet();require(reverted.all {it in LibraryMetadata.FIELDS})
            LibraryDraft(v.getString("token"),parseItem(v.getJSONObject("base")),LibraryMetadata.parse(v.getJSONObject("desired")),v.optString("issue"),v.optJSONObject("remote")?.let(::parseItem),reverted)}
        require(result.map {it.id}.distinct().size==result.size);return result
    }
    private fun saveDrafts(next:List<LibraryDraft>) {
        require(next.size<=500){"离线待同步最多500条，请先同步或处理旧更改"}
        val raw=JSONArray().apply{next.forEach {put(it.json())}}.toString()
        require(raw.toByteArray().size<=12*1024*1024){"离线更改容量已满，请先同步"}
        records.write("$prefix-drafts",raw);drafts=next;queryCache=null
    }
    /** Re-editing preserves the original base, so a newer PC snapshot cannot silently erase conflicts. */
    fun stage(changes:List<Pair<MaterialItem,LibraryMetadata>>,keepNoopIds:Set<String> = emptySet(),editedFields:Map<String,Set<String>>?=null) {
        require(changes.size in 1..100&&changes.map {it.first.id}.distinct().size==changes.size){"每次选择1–100项"}
        if(editedFields!=null)require(editedFields.keys==changes.map {it.first.id}.toSet()&&editedFields.values.all {fields->fields.all {it in LibraryMetadata.FIELDS}}){"编辑字段集合与素材不符"}
        val next=drafts.toMutableList()
        changes.forEach {(item,desired)->
            val input=LibraryMetadata.parse(desired.json());val old=next.firstOrNull {it.id==item.id}
            next.removeAll {it.id==item.id}
            val base=(old?.base?:item).copy(offlineText="",textComplete=false)
            val baseline=LibraryMetadata.of(base)
            val fields=editedFields?.get(item.id)?:LibraryMetadata.FIELDS
            // An editor may display newer remote values beside older local changes. Saving a
            // different field must not convert those displayed values into new local intents.
            val previous=old?.desired?:baseline
            val normalized=LibraryMetadata(if("favorite" in fields)input.favorite else previous.favorite,if("group" in fields)input.group else previous.group,
                if("tags" in fields)input.tags else previous.tags,if("note" in fields)input.note else previous.note)
            // A pending write may already have reached the PC even after a disconnect/lost ACK.
            // Remember explicit reversals until a fresh result confirms those fields or the user resolves them.
            val reverted=if(old==null)emptySet()else LibraryMetadata.FIELDS.filter {normalized.sameField(baseline,it)&&(!old.desired.sameField(baseline,it)||it in old.revertedFields)}.toSet()
            // A reversal to the original value is still an intent while the older write is in flight.
            if(normalized!=baseline||reverted.isNotEmpty()||item.id in keepNoopIds)next+=LibraryDraft(UUID.randomUUID().toString(),base,normalized,revertedFields=reverted)
        }
        saveDrafts(next)
    }
    fun discard(id:String) {saveDrafts(drafts.filterNot {it.id==id})}
    fun rebase(id:String) {
        val draft=drafts.first {it.id==id};val remote=draft.remote?:error("请先连接原电脑检查最新状态")
        // Preserve only the fields the user had actually changed, not unrelated remote changes.
        val b=LibraryMetadata.of(draft.base);val d=draft.desired;val r=LibraryMetadata.of(remote)
        val desired=LibraryMetadata(if(d.favorite!=b.favorite||"favorite" in draft.revertedFields)d.favorite else r.favorite,if(d.group!=b.group||"group" in draft.revertedFields)d.group else r.group,
            if(d.tags!=b.tags||"tags" in draft.revertedFields)d.tags else r.tags,if(d.note!=b.note||"note" in draft.revertedFields)d.note else r.note)
        saveDrafts(drafts.map {if(it.id==id)LibraryDraft(UUID.randomUUID().toString(),remote,desired)else it})
    }
    fun acknowledge(sent:List<LibraryDraft>,response:JSONObject) {
        val arr=response.getJSONArray("results");val byId=sent.associateBy {it.id};val next=drafts.toMutableList()
        val seen=hashSetOf<String>()
        val confirmed=mutableMapOf<String,MaterialItem>()
        for(i in 0 until arr.length()) {
            val value=arr.getJSONObject(i);val id=value.getString("id");val sentDraft=byId[id]?:error("电脑返回了未知素材")
            require(seen.add(id));val at=next.indexOfFirst {it.id==id}
            when(value.getString("code")) {
                "saved"->{
                    val item=parseItem(value.getJSONObject("item"));require(item.id==id);confirmed[id]=item
                    if(at>=0) {
                        val latest=next[at]
                        if(latest.token==sentDraft.token) {
                            if(latest.revertedFields.any {!latest.desired.sameField(LibraryMetadata.of(item),it)})next[at]=latest.copy(issue="电脑确认状态与我的反向修改不同，请核对后重新提交",remote=item)
                            else next.removeAt(at)
                        }
                        else {
                            val b=LibraryMetadata.of(latest.base);val oldBase=LibraryMetadata.of(sentDraft.base);val remote=LibraryMetadata.of(item)
                            fun advance(field:String)=!oldBase.sameField(sentDraft.desired,field)&&remote.sameField(sentDraft.desired,field)&&b.sameField(oldBase,field)
                            // Advance only the exact fields our old write demonstrably produced.
                            // Independent remote fields retain their original baseline and conflict protection.
                            val advanced=LibraryMetadata(if(advance("favorite"))remote.favorite else b.favorite,if(advance("group"))remote.group else b.group,
                                if(advance("tags"))remote.tags else b.tags,if(advance("note"))remote.note else b.note)
                            val updated=latest.copy(base=advanced.apply(latest.base))
                            next[at]=if(latest.revertedFields.any {advanced.sameField(latest.desired,it)&&!latest.desired.sameField(remote,it)})updated.copy(issue="上一轮已确认，但电脑另有变更；我的反向修改已保留，请核对",remote=item)else updated
                        }
                    }
                }
                "conflict"->{val remote=parseItem(value.getJSONObject("item"));require(remote.id==id);if(at>=0&&next[at].token==sentDraft.token)next[at]=next[at].copy(issue="同一字段发生冲突，请核对电脑状态与我的草稿",remote=remote)}
                "deleted"->if(at>=0&&next[at].token==sentDraft.token)next[at]=next[at].copy(issue="电脑已删除此素材，无法同步；草稿保留",remote=null)
                else->error("电脑返回未知整理状态")
            }
        }
        require(seen==byId.keys){"电脑未确认全部更改，保留待同步记录"}
        patchIndex(confirmed)
        saveDrafts(next)
    }
    private fun patchIndex(confirmed:Map<String,MaterialItem>) {
        if(confirmed.isEmpty()||index.pages.isEmpty())return
        val created=mutableListOf<String>();val generation=UUID.randomUUID().toString().replace("-","")
        try {
            val pages=index.pages.mapIndexed {i,name->
                val arr=JSONArray(records.read(name)?:error("离线索引缺页"));var changed=false
                val updated=JSONArray();for(j in 0 until arr.length()) {
                    val original=parseItem(arr.getJSONObject(j));val current=confirmed[original.id]
                    updated.put(if(current==null)arr.getJSONObject(j)else itemJson(current.copy(offlineText=original.offlineText,textComplete=original.textComplete)).also {changed=true})
                }
                if(!changed)name else "$prefix-page-$generation-$i".also {records.write(it,updated.toString());created+=it}
            }
            publish(index.copy(pages=pages))
        }catch(t:Throwable){created.forEach {runCatching{records.delete(it)}};throw t}
    }
    /** Preparation writes unique, unpublished pages and never owns the workspace write mutex. */
    suspend fun prepareDownload(totalHint:Int=0,serverRun:String="",fetch:suspend (Int,Long)->JSONObject,progress:(Int,Int)->Unit={_,_->}):PreparedIndex {
        val revision=indexRevision
        val generation=UUID.randomUUID().toString().replace("-","");val created=mutableListOf<String>()
        var offset=0;var sequence=-1L;var total=totalHint;var bytes=0L;val ids=hashSetOf<String>();var actualRun=serverRun
        try {
            do {
                currentCoroutineContext().ensureActive()
                val response=fetch(offset,sequence);check(!response.optBoolean("stale")){"电脑素材变化，请重新保存离线索引；旧索引保留"}
                check(response.optString("error").isBlank()){response.optString("error")}
                val incoming=response.getLong("sequence");if(sequence>=0)check(sequence==incoming){"离线索引版本已变化"};sequence=incoming
                val incomingRun=response.optString("serverRun",serverRun)
                if(offset>0)check(incomingRun==actualRun){"电脑运行代次变化，原索引保留"};actualRun=incomingRun
                total=response.getInt("total");require(total in 0..50000){"离线索引最多5万项，未替换旧索引"}
                val arr=response.getJSONArray("items");require(arr.length()<=100)
                for(i in 0 until arr.length())require(ids.add(parseItem(arr.getJSONObject(i)).id)){"离线索引存在重复项"}
                val next=response.getInt("nextOffset");check(next==offset+arr.length()&&(next>offset||next==total)&&next<=total){"离线分页不完整"}
                val raw=arr.toString();bytes+=raw.toByteArray().size;require(bytes<=64L*1024*1024){"离线索引超过64MB，未替换旧索引"}
                val name="$prefix-page-$generation-${created.size}";transientPages+=name;created+=name;records.write(name,raw);offset=next;progress(offset,total)
            }while(offset<total)
            check(ids.size==total)
            return PreparedIndex(this,LibraryIndex(created,total,total,true,System.currentTimeMillis(),sequence,actualRun),revision)
        }catch(t:Throwable){created.forEach {runCatching{records.delete(it)};transientPages-=it};throw t}
    }
    /** Owner serializes this short commit with draft acknowledgements and explicit index clearing. */
    fun commitPrepared(prepared:PreparedIndex) {
        require(prepared.owner===this&&!prepared.consumed){"离线索引准备结果已失效"}
        check(prepared.revision==indexRevision){"离线记录在保存期间变化，请重试；已有索引与草稿保留"}
        publish(prepared.index);prepared.consumed=true;prepared.index.pages.forEach {transientPages-=it}
    }
    fun discardPrepared(prepared:PreparedIndex) {
        require(prepared.owner===this)
        if(prepared.consumed)return
        prepared.index.pages.forEach {name->if(name !in index.pages)runCatching{records.delete(name)};transientPages-=name}
        prepared.consumed=true
    }
    /** Bounded journal merge. A declined cursor returns null so the caller can obtain a new snapshot. */
    suspend fun prepareChanges(base:ChangeBase,fetch:suspend (Long,String,Long)->JSONObject,progress:(Int,Int)->Unit={_,_->}):PreparedIndex? {
        require(base.owner===this)
        val updates=linkedMapOf<String,MaterialItem>();val removed=hashSetOf<String>();var cleared=false
        var cursor=base.index.sequence;var until=-1L;var total=base.index.total;var events=0;var networkBytes=0L
        while(true) {
            currentCoroutineContext().ensureActive()
            val r=fetch(cursor,base.index.serverRun,until)
            if(r.optBoolean("requiresFull"))return null
            check(r.optString("error").isBlank()){r.optString("error")}
            check(r.getString("serverRun")==base.index.serverRun){"电脑运行代次已变化，旧索引保留"}
            val end=r.getLong("sequence");if(until<0)until=end else check(end==until){"增量目标已变化"}
            require(until>=cursor);total=r.getInt("total");require(total in 0..50000)
            val changes=r.getJSONArray("changes");require(changes.length()<=100)
            networkBytes+=changes.toString().toByteArray().size;events+=changes.length();require(events<=4096&&networkBytes<=16L*1024*1024){"增量更新超过预算，旧索引保留"}
            for(i in 0 until changes.length()) {
                val change=changes.getJSONObject(i);check(change.getLong("sequence")==cursor+1){"增量序号不连续，旧索引保留"};cursor++
                val id=change.optString("id")
                when(change.getString("kind")) {
                    "clear"->{cleared=true;updates.clear();removed.clear()}
                    "delete"->{require(id.length in 1..256);updates.remove(id);removed+=id}
                    "upsert"->{val item=parseItem(change.getJSONObject("item"));require(item.id==id);removed-=id;updates[id]=item.copy(sequence=cursor)}
                    else->error("电脑返回未知增量类型，原索引保留")
                }
            }
            val next=r.getLong("nextSequence");check(next==cursor&&cursor<=until){"增量进度无效"}
            if(!r.getBoolean("hasMore")){check(cursor==until);break}
            check(changes.length()>0&&cursor<until){"增量分页未推进"}
            progress(events,total)
        }
        val order=hashMapOf<String,Int>();var ordinal=0
        if(!cleared)base.index.pages.forEach {name->currentCoroutineContext().ensureActive();val arr=JSONArray(records.read(name)?:error("离线索引缺页"));require(arr.length()<=1000)
            for(i in 0 until arr.length()){val id=arr.getJSONObject(i).getString("id");if(id in updates)order[id]=ordinal;ordinal++}
        }
        val changed=updates.values.sortedWith(compareByDescending<MaterialItem>{it.time}.thenBy {order[it.id]?.toLong()?:-it.sequence}.thenBy {it.id})
        val generation=UUID.randomUUID().toString().replace("-","");val created=mutableListOf<String>();var pending=JSONArray();var count=0;var bytes=0L;var at=0
        fun flush(){if(pending.length()==0)return;val raw=pending.toString();bytes+=raw.toByteArray().size;require(bytes<=64L*1024*1024&&created.size<500){"离线索引超过容量，旧索引保留"}
            val name="$prefix-page-$generation-${created.size}";transientPages+=name;created+=name;records.write(name,raw);pending=JSONArray()}
        fun emit(item:MaterialItem){require(++count<=50000);pending.put(itemJson(item));if(pending.length()>=100)flush()}
        try {
            ordinal=0
            if(!cleared)base.index.pages.forEach {name->currentCoroutineContext().ensureActive();val arr=JSONArray(records.read(name)?:error("离线索引缺页"));require(arr.length()<=1000)
                for(i in 0 until arr.length()) {
                    val item=parseItem(arr.getJSONObject(i));val oldOrdinal=ordinal++
                    if(item.id in updates||item.id in removed)continue
                    while(at<changed.size&&(changed[at].time>item.time||(changed[at].time==item.time&&(order[changed[at].id]?.toLong()?:-changed[at].sequence)<=oldOrdinal))){emit(changed[at++])}
                    emit(item)
                };progress(count,total)
            }
            while(at<changed.size)emit(changed[at++]);flush();check(count==total){"增量素材数量不符，旧索引保留"}
            return PreparedIndex(this,LibraryIndex(created,count,total,true,System.currentTimeMillis(),until,base.index.serverRun),base.revision)
        }catch(t:Throwable){created.forEach {runCatching{records.delete(it)};transientPages-=it};throw t}
    }
    /** Compatibility helper for single-owner callers and isolated workspace checks. */
    suspend fun download(totalHint:Int=0,fetch:suspend (Int,Long)->JSONObject,progress:(Int,Int)->Unit={_,_->}) {
        val prepared=prepareDownload(totalHint,fetch=fetch,progress=progress)
        try {commitPrepared(prepared)}finally{discardPrepared(prepared)}
    }
    fun recent(items:List<MaterialItem>,total:Int) {
        if(index.complete)return
        val name="$prefix-page-${UUID.randomUUID().toString().replace("-","")}-0"
        records.write(name,JSONArray().apply{items.take(1000).forEach {put(itemJson(it))}}.toString())
        try {publish(LibraryIndex(listOf(name),minOf(items.size,1000),maxOf(total,items.size),false,System.currentTimeMillis()))}
        catch(t:Throwable){runCatching{records.delete(name)};throw t}
    }
    private fun publish(next:LibraryIndex) {
        val previous=index
        records.write("$prefix-meta",JSONObject().put("target",target).put("pages",JSONArray().apply{next.pages.forEach {put(it)}})
            .put("count",next.count).put("total",next.total).put("complete",next.complete).put("saved",next.saved).put("sequence",next.sequence).put("serverRun",next.serverRun).toString())
        index=next;indexRevision++;queryCache=null;facets=null;previous.pages.filterNot {it in next.pages||leasedPages.containsKey(it)}.forEach {runCatching{records.delete(it)}}
    }
    fun clearIndex(){publish(LibraryIndex());cleanupOrphans()}
    fun remove() {
        check(drafts.isEmpty()){"请先同步或逐条放弃待同步草稿"}
        clearIndex();records.delete("$prefix-drafts");records.delete("$prefix-meta")
    }
    private fun cleanupOrphans(){records.names().filter {pagePattern.matches(it)&&it !in index.pages&&it !in transientPages&&!leasedPages.containsKey(it)}.forEach {records.delete(it)}}
    data class Page(val rows:List<MaterialItem>,val total:Int,val groups:List<String>,val tags:List<String>)
    fun query(search:String,type:String,favorite:Boolean,group:String,tag:String,offset:Int,limit:Int=100,current:()->Boolean={true}):Page {
        require(offset>=0&&limit in 1..1000)
        val key=QueryKey(search,type,favorite,group,tag)
        if(search.isEmpty()&&facets!=null&&queryCache?.key!=key) {
            val groups=linkedSetOf<String>();val tags=linkedSetOf<String>();val matches=mutableListOf<PageMatches>();val byId=drafts.associateBy {it.id}
            var found=mutableListOf<Int>();var page="";var first=0;var count=0
            for(row in facets!!) {
                check(current()){"离线查询已更新"}
                if(row.page!=page){if(found.isNotEmpty())matches+=PageMatches(page,found.toIntArray(),first);page=row.page;found=mutableListOf();first=count}
                val draft=byId[row.id];val local=draft?.desired
                val g=if(draft?.changes("group")==true)local!!.group else row.group;val t=if(draft?.changes("tags")==true)local!!.tags else row.tags;val f=if(draft?.changes("favorite")==true)local!!.favorite else row.favorite
                if(g.isNotBlank()&&groups.size<256)groups+=g;t.forEach {if(tags.size<256)tags+=it}
                if((type=="All"||type==row.type)&&(!favorite||f)&&(group.isEmpty()||group==g)&&(tag.isEmpty()||tag in t)){found+=row.position;count++}
            }
            if(found.isNotEmpty())matches+=PageMatches(page,found.toIntArray(),first)
            queryCache=QueryCache(key,matches,count,groups.toList(),tags.toList())
        }
        val cached=queryCache?.takeIf {it.key==key}
        if(cached!=null) {
            val rows=mutableListOf<MaterialItem>();val byId=drafts.associateBy {it.id}
            for(page in cached.pages) {
                check(current()){"离线查询已更新"}
                if(page.first+page.positions.size<=offset)continue
                if(rows.size>=limit)break
                val arr=JSONArray(records.read(page.name)?:error("离线索引缺页，请连接原电脑重新保存"))
                for(position in page.positions.drop((offset-page.first).coerceAtLeast(0))) {
                    if(rows.size>=limit)break
                    val original=parseItem(arr.getJSONObject(position));rows+=byId[original.id]?.overlay(original)?:original
                }
            }
            return Page(rows,cached.total,cached.groups,cached.tags)
        }
        val rows=mutableListOf<MaterialItem>();var matched=0;val groups=linkedSetOf<String>();val tags=linkedSetOf<String>();val byId=drafts.associateBy{it.id}
        val positions=mutableListOf<PageMatches>();var compact:MutableList<FacetRow>?=if(facets==null)mutableListOf()else null;var compactBytes=0L
        index.pages.forEach {name->
            check(current()){"离线查询已更新"}
            val arr=JSONArray(records.read(name)?:error("离线索引缺页，请连接原电脑重新保存"));require(arr.length()<=1000)
            val found=mutableListOf<Int>();val first=matched
            for(i in 0 until arr.length()) {
                val original=parseItem(arr.getJSONObject(i));val item=byId[original.id]?.overlay(original)?:original
                compact?.let {all->val row=FacetRow(name,i,original.id,original.type,original.favorite,original.group,original.tags);compactBytes+=facetBytes(row);if(compactBytes>12L*1024*1024||all.size>=50000)compact=null else all+=row}
                if(item.group.isNotBlank()&&groups.size<256)groups+=item.group
                item.tags.forEach {if(tags.size<256)tags+=it}
                if((type=="All"||type==item.type)&&(!favorite||item.favorite)&&(group.isEmpty()||group==item.group)&&(tag.isEmpty()||tag in item.tags)&&
                    (search.isEmpty()||listOf(item.name,item.note,item.group,item.source,item.sourceDevice,item.offlineText).plus(item.tags).any {it.contains(search,true)})) {
                    if(matched>=offset&&rows.size<limit)rows+=item;matched++;found+=i
                }
            }
            if(found.isNotEmpty())positions+=PageMatches(name,found.toIntArray(),first)
        }
        check(current()){"离线查询已更新"}
        require(matched<=50000){"离线索引超出查询预算"}
        if(facets==null&&compact!=null)facets=compact
        queryCache=QueryCache(key,positions,matched,groups.toList(),tags.toList())
        return Page(rows,matched,groups.toList(),tags.toList())
    }
    companion object {
        fun itemJson(item:MaterialItem)=JSONObject().put("id",item.id).put("type",item.type).put("name",item.name).put("size",item.size)
            .put("addedTime",item.time).put("sequence",item.sequence).put("group",item.group).put("tags",JSONArray().apply{item.tags.forEach {put(it)}})
            .put("note",item.note).put("hasFile",item.hasFile).put("favorite",item.favorite).put("revision",item.revision).put("sourceMissing",item.sourceMissing)
            .put("source",item.source).put("sourceDevice",item.sourceDevice).put("offlineText",item.offlineText).put("textComplete",item.textComplete).put("sourceRevision",item.sourceRevision)
        fun parseItem(value:JSONObject):MaterialItem=Proto.parseItemAdded(value).toMaterialItem().also {require(it.id.length in 1..256&&it.name.length<=100000&&it.offlineText.length<=64000)}
    }
}
