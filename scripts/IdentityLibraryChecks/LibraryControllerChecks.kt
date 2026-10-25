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

fun main(args:Array<String>)=runBlocking {
    val disk=FileRecords(File(args[0],"controller"))
    val device="aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";val library="bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";val target="$device:$library"
    val first=MaterialItem("controller-0","Text","Controller row",0,9000,group="Original",note="base",offlineText="saved controller text",textComplete=true)
    var row=first;var mutations=0;var loseAck=false;var calls=0;var count=0
    fun verify(ok:Boolean,label:String){check(ok){label};println("PASS $label");count++}
    suspend fun settled(test:()->Boolean){withTimeout(12000){while(!test())delay(30)}}
    val c=LanClient(library){request->
        calls++;check(request.optString("libraryId")==library);check(request.optString("expectedDeviceId")==device)
        if(request.optString("action")=="merge_batch") {
            val edits=request.getJSONArray("edits");val results=JSONArray()
            for(i in 0 until edits.length()) {
                val edit=edits.getJSONObject(i);val base=LibraryMetadata.parse(edit.getJSONObject("base"));val desired=LibraryMetadata.parse(edit.getJSONObject("desired"));val now=LibraryMetadata.of(row)
                val conflict=(base.group!=desired.group&&now.group!=base.group&&now.group!=desired.group)
                if(conflict)results.put(JSONObject().put("id",row.id).put("code","conflict").put("item",LibraryWorkspace.itemJson(row)))
                else {
                    val result=LibraryMetadata(if(base.favorite!=desired.favorite)desired.favorite else now.favorite,if(base.group!=desired.group)desired.group else now.group,
                        if(base.tags!=desired.tags)desired.tags else now.tags,if(base.note!=desired.note)desired.note else now.note)
                    if(result!=now){mutations++;row=result.apply(row).copy(revision=row.revision+1)}
                    results.put(JSONObject().put("id",row.id).put("code","saved").put("item",LibraryWorkspace.itemJson(row)))
                }
            }
            if(loseAck){loseAck=false;throw java.io.IOException("injected lost acknowledgement")}
            JSONObject().put("libraryId",library).put("results",results)
        }else JSONObject().put("libraryId",library).put("sequence",row.revision).put("total",1).put("nextOffset",1).put("items",JSONArray().put(LibraryWorkspace.itemJson(row)))
    }
    MaterialLibrary.attachRecords(disk)
    if(args[1]=="seed") {
        delay(80);MaterialLibrary.connected(c,"$target:run1")
        settled{MaterialLibrary.state.value.canEditOffline&&!MaterialLibrary.state.value.loading&&MaterialLibrary.state.value.offlineCount==1}
        MaterialLibrary.saveOfflineIndex(MaterialLibrary.state.value.epoch)
        MaterialLibrary.disconnected();settled{!MaterialLibrary.state.value.connected}
        MaterialLibrary.stage(listOf(first to LibraryMetadata.of(first).copy(group="Pending")),MaterialLibrary.state.value.epoch)
        settled{MaterialLibrary.state.value.drafts.size==1}
        MaterialLibrary.query(MaterialLibrary.Query(group="Pending"));settled{!MaterialLibrary.state.value.loading&&MaterialRepository.materials.value.size==1}
        verify(MaterialRepository.materials.value.single().group=="Pending","production coordinator filters staged metadata while disconnected")
        verify(MaterialLibrary.state.value.offlineComplete,"explicit offline download produces complete durable snapshot")
        verify(mutations==0,"offline staging does not contact or mutate server")
    }else {
        settled{MaterialLibrary.state.value.canEditOffline&&MaterialLibrary.state.value.drafts.size==1&&!MaterialLibrary.state.value.loading&&MaterialRepository.materials.value.size==1}
        verify(MaterialRepository.materials.value.single().group=="Pending","fresh process coordinator restores pending overlay and offline rows")
        verify(MaterialRepository.materials.value.single().offlineText=="saved controller text","offline complete text survives process restart")
        val otherLibrary="dddddddd-dddd-dddd-dddd-dddddddddddd";var foreignEdits=0
        val other=LanClient(otherLibrary){q->if(q.optString("action")=="merge_batch")foreignEdits++;JSONObject().put("libraryId",otherLibrary).put("sequence",0).put("total",0).put("nextOffset",0).put("items",JSONArray())}
        MaterialLibrary.connected(other,"cccccccc-cccc-cccc-cccc-cccccccccccc:$otherLibrary:run2")
        settled{MaterialLibrary.state.value.canEditOffline&&!MaterialLibrary.state.value.loading};delay(300)
        verify(MaterialLibrary.state.value.drafts.isEmpty()&&foreignEdits==0,"other authenticated computer never receives original offline edits")
        row=row.copy(note="desktop unrelated")
        MaterialLibrary.connected(c,"$target:run3")
        settled{MaterialLibrary.state.value.canEditOffline&&MaterialLibrary.state.value.drafts.isEmpty()&&row.group=="Pending"}
        verify(row.note=="desktop unrelated"&&mutations==1,"reconnect original library auto-merges unrelated remote change")
        val epoch=MaterialLibrary.state.value.epoch
        loseAck=true
        MaterialLibrary.stage(listOf(row to LibraryMetadata.of(row).copy(favorite=true)),epoch)
        settled{row.favorite&&!MaterialLibrary.state.value.syncing}
        verify(MaterialLibrary.state.value.drafts.size==1&&row.favorite,"lost response keeps durable pending edit")
        val applied=mutations;MaterialLibrary.syncPending()
        verify(MaterialLibrary.state.value.drafts.isEmpty()&&mutations==applied,"coordinator retries acknowledgement without duplicate mutation")
        MaterialLibrary.disconnected();val base=row
        MaterialLibrary.stage(listOf(base to LibraryMetadata.of(base).copy(group="Mobile")),epoch)
        row=row.copy(group="Desktop")
        MaterialLibrary.connected(c,"$target:run4")
        settled{MaterialLibrary.state.value.drafts.firstOrNull()?.issue?.isNotEmpty()==true}
        verify(row.group=="Desktop"&&MaterialLibrary.state.value.drafts.single().desired.group=="Mobile","same-field conflict retains both states without overwriting desktop")
        MaterialLibrary.resolveDraft(row.id,true,MaterialLibrary.state.value.epoch)
        settled{row.group=="Mobile"&&MaterialLibrary.state.value.drafts.isEmpty()}
        verify(row.group=="Mobile"&&MaterialLibrary.state.value.drafts.isEmpty(),"explicit conflict rebase submits through checked merge")
        verify(runCatching{MaterialLibrary.stage(listOf(row to LibraryMetadata.of(row).copy(group="wrong epoch")),epoch)}.isFailure,"stale editor epoch cannot stage into reconnected session")
        settled{!MaterialLibrary.state.value.loading};val idle=calls;delay(600)
        verify(calls==idle,"offline persistence adds no idle polling")
    }
    println("PASS $count production offline coordinator checks (${args[1]}); substitute transport and record IO")
}
