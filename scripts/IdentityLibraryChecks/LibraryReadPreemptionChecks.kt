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
import java.util.concurrent.CopyOnWriteArrayList

fun main(args:Array<String>)=runBlocking {
    val target="aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa:bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";val library=target.substringAfter(':')
    val disk=FileRecords(File(args[0],"preemption"));val oldEntered=CompletableDeferred<Unit>();val oldGate=CompletableDeferred<Unit>();val oldCancelled=CompletableDeferred<Unit>()
    val writeEntered=CompletableDeferred<Unit>();val writeGate=CompletableDeferred<Unit>();var writeCancelled=false;var favorite=false
    val words=CopyOnWriteArrayList<String>();val c=LanClient(library){q->
        if(q.optString("action")=="merge_batch") {
            writeEntered.complete(Unit)
            try {writeGate.await()}catch(ex:CancellationException){writeCancelled=true;throw ex}
            val edit=q.getJSONArray("edits").getJSONObject(0);favorite=edit.getJSONObject("desired").getBoolean("favorite")
            JSONObject().put("libraryId",library).put("results",JSONArray().put(JSONObject().put("id","item").put("code","saved").put("item",LibraryWorkspace.itemJson(MaterialItem("item","Text","item",0,1000,favorite=favorite)))))
        }else {
            val word=q.optString("search");words+=word
            if(word=="old")try{oldEntered.complete(Unit);oldGate.await()}catch(ex:CancellationException){oldCancelled.complete(Unit);throw ex}
            JSONObject().put("libraryId",library).put("sequence",0).put("total",1).put("nextOffset",1).put("items",JSONArray().put(LibraryWorkspace.itemJson(MaterialItem("item","Text",word.ifBlank{"initial"},0,1000,favorite=favorite))))
        }
    }
    var checks=0;fun verify(ok:Boolean,label:String){check(ok){label};checks++;println("PASS $label")}
    suspend fun settled(test:()->Boolean){withTimeout(10000){while(!test())delay(15)}}
    MaterialLibrary.attachRecords(disk);delay(80);MaterialLibrary.connected(c,"$target:run1");settled{MaterialLibrary.state.value.canSaveSearches&&!MaterialLibrary.state.value.loading}
    MaterialLibrary.query(MaterialLibrary.Query(search="old"));withTimeout(3000){oldEntered.await()}
    val started=System.nanoTime();MaterialLibrary.query(MaterialLibrary.Query(search="new"))
    withTimeout(1500){settled{!MaterialLibrary.state.value.loading&&MaterialRepository.materials.value.singleOrNull()?.name=="new"}}
    verify(oldCancelled.isCompleted&&!oldGate.isCompleted&&MaterialLibrary.state.value.error.isEmpty(),"new search cancels only the obsolete read and completes before the old server response")
    println("NEW_SEARCH_AFTER_BLOCKED_READ ms=${(System.nanoTime()-started)/1e6}")
    val start=words.size
    listOf("f","fi","fin","fina","final").forEach{MaterialLibrary.query(MaterialLibrary.Query(search=it));delay(80)}
    settled{!MaterialLibrary.state.value.loading&&MaterialRepository.materials.value.singleOrNull()?.name=="final"}
    verify(words.drop(start)==listOf("final"),"search uses trailing-edge input coalescing rather than requesting intermediate prefixes")
    val row=MaterialRepository.materials.value.single();MaterialLibrary.stage(listOf(row to LibraryMetadata.of(row).copy(favorite=true)),MaterialLibrary.state.value.epoch)
    withTimeout(3000){writeEntered.await()}
    MaterialLibrary.query(MaterialLibrary.Query(search="while-write"));delay(300);MaterialLibrary.query(MaterialLibrary.Query(search="after-write"));delay(300)
    verify(!writeCancelled&&!writeGate.isCompleted&&MaterialLibrary.state.value.drafts.size==1,"superseding read jobs cannot cancel an in-flight metadata write or discard its durable draft")
    writeGate.complete(Unit);settled{favorite&&MaterialLibrary.state.value.drafts.isEmpty()&&!MaterialLibrary.state.value.loading&&MaterialRepository.materials.value.singleOrNull()?.name=="after-write"}
    verify(!writeCancelled&&favorite,"metadata writer finishes independently and latest search receives the confirmed current state")
    val idle=words.size;delay(450);verify(words.size==idle,"read preemption adds no idle polling")
    println("PASS $checks read-only query preemption checks; gated substitute network, production state machine")
}
