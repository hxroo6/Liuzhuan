package com.liuzhuan.checks

import com.liuzhuan.app.MaterialLibrary
import com.liuzhuan.app.core.*
import com.liuzhuan.app.data.MaterialItem
import com.liuzhuan.app.data.MaterialRepository
import com.liuzhuan.app.net.LanClient
import kotlinx.coroutines.*
import org.json.JSONObject
import org.json.JSONArray
import java.io.File
import java.util.concurrent.atomic.AtomicInteger

fun main(args:Array<String>)=runBlocking {
    val disk=FileRecords(File(args[0],"timeouts"));val device="aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
    val library="bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";val target="$device:$library"
    val queryCalls=AtomicInteger();val syncCalls=AtomicInteger();var row=MaterialItem("timeout","Text","Timeout row",0,1000)
    val c=LanClient(library){q->
        if(q.optString("action")=="merge_batch") {
            if(syncCalls.incrementAndGet()==1)withTimeout(40){delay(10000)}
            val edit=q.getJSONArray("edits").getJSONObject(0);row=LibraryMetadata.parse(edit.getJSONObject("desired")).apply(row).copy(revision=row.revision+1)
            JSONObject().put("libraryId",library).put("results",JSONArray().put(JSONObject().put("id",row.id).put("code","saved").put("item",LibraryWorkspace.itemJson(row))))
        }else {
            if(queryCalls.incrementAndGet()==1)withTimeout(40){delay(10000)}
            JSONObject().put("libraryId",library).put("sequence",row.revision).put("total",1).put("nextOffset",1).put("items",JSONArray().put(LibraryWorkspace.itemJson(row)))
        }
    }
    var checks=0;fun verify(ok:Boolean,label:String){check(ok){label};checks++;println("PASS $label")}
    suspend fun settled(test:()->Boolean){withTimeout(8000){while(!test())delay(15)}}
    MaterialLibrary.attachRecords(disk);delay(80);MaterialLibrary.connected(c,"$target:run1")
    settled{MaterialLibrary.state.value.canEditOffline&&!MaterialLibrary.state.value.loading&&MaterialLibrary.state.value.error.contains("超时")}
    verify(queryCalls.get()==1,"internal query withTimeout reports a visible recoverable error and releases loading")
    MaterialLibrary.invalidated();settled{!MaterialLibrary.state.value.loading&&MaterialRepository.materials.value.size==1}
    verify(queryCalls.get()==2&&MaterialLibrary.state.value.error.isEmpty(),"refresh succeeds after TimeoutCancellationException without recreating the page actor")
    MaterialLibrary.stage(listOf(row to LibraryMetadata.of(row).copy(favorite=true)),MaterialLibrary.state.value.epoch)
    settled{syncCalls.get()==1&&!MaterialLibrary.state.value.syncing&&MaterialLibrary.state.value.error.contains("同步请求超时")}
    verify(LibraryWorkspace(disk,target).pending().size==1&&!row.favorite,"internal sync withTimeout leaves the durable draft and visible retry hint")
    withTimeout(3000){MaterialLibrary.syncPending()}
    settled{MaterialLibrary.state.value.drafts.isEmpty()&&!MaterialLibrary.state.value.syncing}
    verify(syncCalls.get()==2&&row.favorite,"explicit sync retry completes after TimeoutCancellationException and its Deferred never hangs")
    val idle=syncCalls.get();delay(400);verify(syncCalls.get()==idle,"timeout recovery keeps event-driven sleeping behavior without automatic polling")
    println("PASS $checks long-lived library actor timeout checks; real nested withTimeout cancellation with substitute network/record IO")
}
