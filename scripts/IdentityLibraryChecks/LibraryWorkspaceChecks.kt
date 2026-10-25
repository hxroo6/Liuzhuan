package com.liuzhuan.checks

import com.liuzhuan.app.core.*
import com.liuzhuan.app.data.MaterialItem
import kotlinx.coroutines.runBlocking
import org.json.JSONObject
import org.json.JSONArray
import java.io.File

internal class FileRecords(val root:File):LibraryRecords {
    var fail:String=""
    var pageReads=0
    var beforeWrite:(String)->Unit={}
    override fun read(name:String):String? {if("-page-" in name)pageReads++;return File(root,name).takeIf{it.exists()}?.readText()}
    override fun write(name:String,text:String){beforeWrite(name);if(fail.isNotEmpty()&&name.endsWith(fail))error("injected IO failure");root.mkdirs();val temp=File(root,"$name.tmp");temp.writeText(text);java.nio.file.Files.move(temp.toPath(),File(root,name).toPath(),java.nio.file.StandardCopyOption.REPLACE_EXISTING)}
    override fun delete(name:String){File(root,name).delete()}
    override fun names()=root.listFiles()?.map {it.name}?:emptyList()
}
fun main(args:Array<String>)=runBlocking {
    val records=FileRecords(File(args[0],"workspace"));val target="11111111-1111-1111-1111-111111111111:22222222-2222-2222-2222-222222222222"
    val w=LibraryWorkspace(records,target);var count=0
    fun checkIt(ok:Boolean,label:String){check(ok){label};println("PASS $label");count++}
    val first=MaterialItem("item-0","Text","Original",0,1000,group="Original",tags=listOf("keep"),note="base",offlineText="full body",textComplete=true)
    fun desired(item:MaterialItem)=LibraryMetadata.of(item).copy(group="Offline",favorite=true)
    if(args[1]=="seed") {
        w.download(fetch={offset,sequence->
            check(sequence==-1L||sequence==3L)
            val end=minOf(offset+100,1300)
            JSONObject().put("sequence",3).put("total",1300).put("nextOffset",end).put("items",JSONArray().apply{(offset until end).forEach {i->put(LibraryWorkspace.itemJson(first.copy(id="item-$i",name="Row $i")))}})
        })
        checkIt(w.info().complete&&w.info().count==1300,"full disk snapshot exceeds resident window without truncating")
        w.query("","All",false,"","",0);records.pageReads=0
        checkIt(w.query("","All",false,"","",100).rows.size==100&&records.pageReads==1,"same-query next page reads one encrypted page instead of the whole index")
        records.pageReads=0;w.query("Row 1299","All",false,"","",0)
        checkIt(records.pageReads==13,"new search invalidates the bounded positions cache and scans all pages")
        checkIt(w.query("Row 1299","All",false,"","",0).rows.single().id=="item-1299","offline search reaches older disk pages")
        checkIt(runCatching{w.query("","All",false,"","",0,current={false})}.isFailure&&w.info().count==1300,"superseded offline search stops before scanning further pages without mutating index")
        w.query("","All",true,"Offline","",0) // Warm the exact query before changing drafts.
        w.stage(listOf(first to desired(first)))
        records.pageReads=0
        checkIt(w.query("","All",true,"Offline","",0).rows.single().id==first.id,"offline facets see local pending metadata")
        checkIt(records.pageReads==1,"durable draft changes invalidate query positions while compact facets overlay the latest draft without rescanning every page")
        checkIt(runCatching{w.remove()}.isFailure&&w.info().count==1300,"offline library with pending drafts cannot be removed")
        records.fail="-drafts"
        checkIt(runCatching{w.stage(listOf(first.copy(id="fail") to desired(first)))}.isFailure&&w.pending().size==1,"failed draft persistence never reports staged or mutates memory")
        records.fail=""
        val oldInfo=w.info()
        checkIt(runCatching{w.download(fetch={offset,_->if(offset>0)JSONObject().put("stale",true)else JSONObject().put("sequence",4).put("total",200).put("nextOffset",100).put("items",JSONArray().apply{repeat(100){put(LibraryWorkspace.itemJson(first.copy(id="new-$it")))}})})}.isFailure&&w.info()==oldInfo,"interrupted generation keeps prior committed full snapshot")
        val other=LibraryWorkspace(records,"33333333-3333-3333-3333-333333333333:44444444-4444-4444-4444-444444444444")
        checkIt(other.info().count==0&&other.pending().isEmpty(),"different computer and library cannot see index or queued edits")
        checkIt(runCatching{w.stage(listOf(first to desired(first).copy(tags=(0..20).map{"tag$it"})))}.isFailure,"invalid batch metadata rejected before durable change")
        w.stage(listOf(first to desired(first).copy(note="edited again")))
        checkIt(w.pending().single().base.note=="base","re-edit retains original baseline instead of accepting newer remote state")
        val preserved=w.info();records.fail="-meta"
        checkIt(runCatching{w.download(fetch={_,_->JSONObject().put("sequence",10).put("total",1).put("nextOffset",1).put("items",JSONArray().put(LibraryWorkspace.itemJson(first)))})}.isFailure&&w.info()==preserved,"failed manifest commit preserves old generation")
        records.fail=""
        checkIt(runCatching{w.download(fetch={_,_->JSONObject().put("sequence",10).put("total",50001).put("nextOffset",0).put("items",JSONArray())})}.isFailure&&w.info()==preserved,"oversized full library rejected without truncating existing snapshot")
    }else {
        checkIt(w.info().complete&&w.query("Row 1299","All",false,"","",0).rows.size==1,"fresh JVM restores full offline index")
        checkIt(w.pending().single().desired.note=="edited again","fresh JVM restores durable edit with baseline")
        val sent=w.pending();val remote=first.copy(group="Desktop",note="remote unrelated")
        w.acknowledge(sent,JSONObject().put("results",JSONArray().put(JSONObject().put("id",first.id).put("code","conflict").put("item",LibraryWorkspace.itemJson(remote)))))
        checkIt(w.pending().single().remote?.group=="Desktop"&&w.pending().single().desired.group=="Offline","conflict preserves both remote metadata and local draft")
        w.rebase(first.id)
        checkIt(w.pending().single().base.group=="Desktop"&&w.pending().single().desired.group=="Offline","explicit rebase updates baseline for another checked attempt")
        val ready=w.pending();val saved=ready.single().desired.apply(remote)
        records.fail="-drafts"
        checkIt(runCatching{w.acknowledge(ready,JSONObject().put("results",JSONArray().put(JSONObject().put("id",first.id).put("code","saved").put("item",LibraryWorkspace.itemJson(saved)))))}.isFailure&&w.pending().isNotEmpty(),"failed acknowledgement persistence leaves edit retryable")
        records.fail=""
        w.acknowledge(ready,JSONObject().put("results",JSONArray().put(JSONObject().put("id",first.id).put("code","saved").put("item",LibraryWorkspace.itemJson(saved)))))
        checkIt(w.pending().isEmpty()&&w.query("","All",false,"Offline","",0).rows.first().group=="Offline","saved acknowledgement patches disk snapshot before dropping draft")
        checkIt(w.query("full body","All",false,"","",0).total==1300,"offline full-text search and text retained across metadata patch")
        w.stage(listOf(first to desired(first)))
        val orphan=w.info().pages.first().substringBefore("-page-")+"-page-0123456789abcdef0123456789abcdef-999"
        records.write(orphan,"[]");w.clearIndex()
        checkIt(w.info().count==0&&w.pending().size==1&&orphan !in records.names()&&w.query("full body","All",false,"","",0).total==0,"explicit index removal clears cached positions and abandoned own pages while preserving pending drafts")
        w.discard(first.id);checkIt(w.pending().isEmpty(),"explicit discard removes only chosen local draft")
        val bulk=(0 until 100).map {i->first.copy(id="batch-$i") to desired(first)};w.stage(bulk)
        checkIt(w.pending().size==100,"hundred-item batch stages durably as one local change set")
        val older=w.pending().first();w.stage(listOf(older.base to older.desired.copy(note="newer local draft")))
        w.acknowledge(listOf(older),JSONObject().put("results",JSONArray().put(JSONObject().put("id",older.id).put("code","saved").put("item",LibraryWorkspace.itemJson(older.desired.apply(older.base))))))
        checkIt(w.pending().first{it.id==older.id}.desired.note=="newer local draft","late acknowledgement cannot remove a newer local edit token")
        val deleted=w.pending().first();w.acknowledge(listOf(deleted),JSONObject().put("results",JSONArray().put(JSONObject().put("id",deleted.id).put("code","deleted"))))
        checkIt(w.pending().first{it.id==deleted.id}.issue.isNotBlank(),"deleted remote item keeps local draft for explicit recovery")
    }
    println("PASS $count production workspace checks (${args[1]}); substitute record IO, no device encryption claim")
}
