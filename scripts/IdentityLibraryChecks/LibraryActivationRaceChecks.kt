package com.liuzhuan.checks

import com.liuzhuan.app.MaterialLibrary
import com.liuzhuan.app.core.*
import com.liuzhuan.app.data.MaterialItem
import com.liuzhuan.app.data.MaterialRepository
import com.liuzhuan.app.net.LanClient
import kotlinx.coroutines.*
import org.json.JSONObject
import org.json.JSONArray
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CountDownLatch

fun main()=runBlocking {
    val a="aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa:11111111-1111-1111-1111-111111111111"
    val b="bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb:22222222-2222-2222-2222-222222222222"
    val values=ConcurrentHashMap<String,String>();var beforeRead:(String)->Unit={}
    val disk=object:LibraryRecords {override fun read(name:String):String?{beforeRead(name);return values[name]};override fun write(name:String,text:String){values[name]=text};override fun delete(name:String){values.remove(name)};override fun names()=values.keys.toList()}
    val downloadEntered=CompletableDeferred<Unit>();val downloadGate=CompletableDeferred<Unit>()
    fun connection(t:String)=LanClient(t.substringAfter(':')) {q->
        if(q.optBoolean("includeText")){downloadEntered.complete(Unit);downloadGate.await()}
        if(q.optString("action")=="merge_batch")error("offline changes must not be sent")
        JSONObject().put("libraryId",t.substringAfter(':')).put("sequence",0).put("total",1).put("nextOffset",1).put("items",JSONArray().put(LibraryWorkspace.itemJson(MaterialItem("row-${t.take(1)}","Text","row",0,1000))))
    }
    suspend fun settled(test:()->Boolean){withTimeout(4000){while(!test())delay(10)}}
    var count=0;fun verify(ok:Boolean,text:String){check(ok){text};count++;println("PASS $text")}
    MaterialLibrary.attachRecords(disk);delay(100)
    val entered=CompletableDeferred<Unit>();val release=CountDownLatch(1)
    beforeRead={name->if(name=="library-r05-active"){beforeRead={};entered.complete(Unit);release.await()}}
    MaterialLibrary.connected(connection(a),"$a:runA");withTimeout(3000){entered.await()}
    val cb=connection(b);MaterialLibrary.connected(cb,"$b:runB");settled{MaterialRepository.materials.value.singleOrNull()?.id=="row-b"}
    MaterialLibrary.disconnected();release.countDown();MaterialLibrary.savedWorkspaces();delay(80)
    verify(MaterialLibrary.state.value.epoch=="$b:runB"&&!MaterialLibrary.state.value.canEditOffline,"late A activation cannot publish A workspace under disconnected B identity")
    val row=MaterialItem("row-b","Text","B",0,1000)
    verify(runCatching{MaterialLibrary.stage(listOf(row to LibraryMetadata.of(row).copy(group="wrong")),"$b:runB")}.isFailure,"offline write rejects an epoch with no matching owned workspace")
    verify(MaterialLibrary.savedWorkspaces().all {it.pending==0},"rejected B edit cannot write a draft into A records")
    MaterialLibrary.connected(cb,"$b:runC");settled{MaterialLibrary.state.value.canEditOffline&&!MaterialLibrary.state.value.loading}
    val epoch=MaterialLibrary.state.value.epoch
    val save=launch {MaterialLibrary.saveOfflineIndex(epoch)};withTimeout(3000){downloadEntered.await()}
    withTimeout(700){MaterialLibrary.stage(listOf(row to LibraryMetadata.of(row).copy(group="local")),epoch)}
    verify(MaterialLibrary.state.value.drafts.single().desired.group=="local"&&!downloadGate.isCompleted,"blocked full-index network request does not block durable local staging")
    save.cancelAndJoin()
    verify(!MaterialLibrary.state.value.workBusy&&MaterialLibrary.state.value.drafts.single().desired.group=="local","cancelling unpublished index pages preserves drafts and resets busy state")
    println("PASS $count gated production activation and index-save checks; transport and record IO substituted")
}
