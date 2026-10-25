package com.liuzhuan.checks

import com.liuzhuan.app.core.*
import com.liuzhuan.app.data.MaterialItem
import kotlinx.coroutines.*
import org.json.JSONArray
import org.json.JSONObject
import java.io.File

fun main(args:Array<String>)=runBlocking {
    val disk=FileRecords(File(args[0],"incremental"));val run="cccccccc-cccc-cccc-cccc-cccccccccccc"
    val w=LibraryWorkspace(disk,"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa:bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")
    var count=0;fun verify(ok:Boolean,text:String){check(ok){text};count++;println("PASS $text")}
    val old=MaterialItem("old","Text","Old",0,1,offlineText="keep body",textComplete=true)
    val keep=old.copy(id="keep",name="Keep",time=2)
    val first=w.prepareDownload(fetch={_,_->JSONObject().put("sequence",2).put("serverRun",run).put("total",2).put("nextOffset",2).put("items",JSONArray().put(LibraryWorkspace.itemJson(keep)).put(LibraryWorkspace.itemJson(old)))})
    w.commitPrepared(first);w.stage(listOf(keep to LibraryMetadata.of(keep).copy(group="pending")))
    val b=w.beginChanges()!!
    fun event(seq:Long,kind:String,id:String,item:MaterialItem?=null)=JSONObject().put("sequence",seq).put("kind",kind).put("id",id).apply{item?.let{put("item",LibraryWorkspace.itemJson(it))}}
    val updated=keep.copy(note="desktop");val new=old.copy(id="new",time=3,name="New")
    var page=0
    val p=w.prepareChanges(b,fetch={since,server,until->check(server==run);page++
        val changes=if(page==1)JSONArray().put(event(3,"delete","old")).put(event(4,"upsert","keep",updated))else JSONArray().put(event(5,"upsert","new",new))
        check(since==if(page==1)2L else 4L);check(until==if(page==1)-1L else 5L)
        JSONObject().put("serverRun",run).put("sequence",5).put("nextSequence",if(page==1)4 else 5).put("hasMore",page==1).put("total",2).put("changes",changes)
    })!!
    verify(w.query("","All",false,"","",0).rows.map{it.id}==listOf("keep","old"),"prepared journal pages do not mutate live offline index")
    w.commitPrepared(p);w.endChanges(b)
    val rows=w.query("","All",false,"","",0).rows
    verify(rows.map{it.id}==listOf("new","keep")&&rows.last().note=="desktop"&&rows.last().group=="pending"&&w.pending().size==1,"incremental add/update/delete preserves authoritative content and independent durable draft overlay")
    verify(w.info().sequence==5L&&w.info().serverRun==run&&w.info().complete,"published delta cursor and run survive with complete index")
    val stale=w.beginChanges()!!;val declined=w.prepareChanges(stale,fetch={_,_,_->JSONObject().put("requiresFull",true)})
    verify(declined==null&&w.info().sequence==5L,"expired or old-run cursor explicitly falls back without mutating saved index");w.endChanges(stale)
    val malformed=w.beginChanges()!!
    verify(runCatching{w.prepareChanges(malformed,fetch={_,_,_->JSONObject().put("serverRun",run).put("sequence",7).put("nextSequence",7).put("hasMore",false).put("total",1).put("changes",JSONArray().put(event(7,"delete","new")))})}.isFailure&&w.info().sequence==5L,"gapped delta journal cannot publish a partial index");w.endChanges(malformed)
    val clear=w.beginChanges()!!;val cleared=w.prepareChanges(clear,fetch={_,_,_->JSONObject().put("serverRun",run).put("sequence",7).put("nextSequence",7).put("hasMore",false).put("total",1).put("changes",JSONArray().put(event(6,"clear","")).put(event(7,"upsert","new",new)))})!!
    w.commitPrepared(cleared);w.endChanges(clear)
    verify(w.query("","All",false,"","",0).rows.single().id=="new"&&w.pending().size==1,"clear followed by upsert replaces only saved index while retaining pending metadata")
    val staged=w.prepareDownload(fetch={_,_->JSONObject().put("sequence",8).put("serverRun",run).put("total",1).put("nextOffset",1).put("items",JSONArray().put(LibraryWorkspace.itemJson(new)))})
    w.clearIndex()
    verify(runCatching{w.commitPrepared(staged)}.isFailure&&w.info().count==0,"explicit index changes invalidate an unpublished snapshot instead of being overwritten");w.discardPrepared(staged)
    println("PASS $count incremental offline merge checks; bounded synthetic journal and private records")
}
