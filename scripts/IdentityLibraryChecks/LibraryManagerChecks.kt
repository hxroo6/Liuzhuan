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
    val disk=FileRecords(File(args[0],"manager"));val device="aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
    fun library(i:Int)="00000000-0000-0000-0000-%012d".format(i)
    fun target(i:Int)="$device:${library(i)}"
    var calls=0;var checks=0
    fun verify(ok:Boolean,label:String){check(ok){label};checks++;println("PASS $label")}
    suspend fun settled(test:suspend ()->Boolean){withTimeout(12000){while(!test())delay(25)}}
    fun connection(i:Int)=LanClient(library(i)){q->calls++;JSONObject().put("libraryId",library(i)).put("sequence",0).put("total",0).put("nextOffset",0).put("items",JSONArray())}
    MaterialLibrary.attachRecords(disk);delay(80)
    for(i in 1..5){MaterialLibrary.connected(connection(i),"${target(i)}:run$i");settled{MaterialLibrary.state.value.canEditOffline&&!MaterialLibrary.state.value.loading};settled{MaterialLibrary.savedWorkspaces().size==i}}
    MaterialLibrary.connected(connection(6),"${target(6)}:run6");settled{!MaterialLibrary.state.value.loading};delay(100)
    verify(!MaterialLibrary.state.value.canEditOffline&&MaterialLibrary.savedWorkspaces().size==5,"sixth connected library can list five old offline libraries even when workspace activation fails")
    verify(runCatching{MaterialLibrary.selectOfflineWorkspace(target(1))}.isFailure,"offline switching cannot replace a live authenticated connection")
    MaterialLibrary.disconnected();MaterialLibrary.selectOfflineWorkspace(target(1));settled{!MaterialLibrary.state.value.loading}
    val row=MaterialItem("pending","Text","Pending metadata",0,0,group="old")
    MaterialLibrary.stage(listOf(row to LibraryMetadata.of(row).copy(group="draft")),MaterialLibrary.state.value.epoch)
    verify(runCatching{MaterialLibrary.removeSavedWorkspace(target(1))}.isFailure&&MaterialLibrary.savedWorkspaces().first{it.target==target(1)}.pending==1,"offline manager protects drafts belonging to an unavailable computer")
    MaterialLibrary.connected(connection(6),"${target(6)}:run6b");settled{!MaterialLibrary.state.value.loading};MaterialLibrary.removeSavedWorkspace(target(5))
    settled{MaterialLibrary.state.value.canEditOffline}
    val after=MaterialLibrary.savedWorkspaces()
    verify(after.size==5&&after.any{it.target==target(6)}&&after.none{it.target==target(5)},"removing an old unavailable cache frees a slot and activates current computer without reconnecting")
    MaterialLibrary.disconnected();val before=calls;MaterialLibrary.selectOfflineWorkspace(target(1));settled{!MaterialLibrary.state.value.loading}
    verify(MaterialLibrary.state.value.drafts.single().desired.group=="draft"&&calls==before,"old computer offline workspace and its drafts open without any network request")
    MaterialLibrary.resolveDraft(row.id,false,MaterialLibrary.state.value.epoch);MaterialLibrary.removeSavedWorkspace(target(1))
    verify(MaterialLibrary.savedWorkspaces().size==4&&MaterialLibrary.state.value.drafts.isEmpty()&&MaterialRepository.materials.value.isEmpty(),"explicit draft discard permits safe removal of active offline cache")
    val meta=disk.names().first{it.endsWith("-meta")&&JSONObject(disk.read(it)!!).getString("target")==target(2)}
    disk.write(meta.removeSuffix("-meta")+"-drafts","not json")
    verify(MaterialLibrary.savedWorkspaces().first{it.target==target(2)}.error.isNotBlank(),"unreadable draft record remains visible with an error instead of being counted as empty")
    verify(runCatching{MaterialLibrary.removeSavedWorkspace(target(2))}.isFailure&&MaterialLibrary.savedWorkspaces().size==4,"unreadable offline records fail closed and are never automatically removed")
    disk.beforeWrite={name->if(name=="library-r05-active"){disk.beforeWrite={};MaterialLibrary.connected(connection(6),"${target(6)}:during-select")}}
    verify(runCatching{MaterialLibrary.selectOfflineWorkspace(target(3))}.isFailure&&MaterialLibrary.state.value.connected&&MaterialLibrary.state.value.epoch=="${target(6)}:during-select","connection arriving during offline registry IO cannot be overwritten by a stale offline selection")
    settled{MaterialLibrary.state.value.canEditOffline&&!MaterialLibrary.state.value.loading}
    println("PASS $checks independent offline library management checks; substitute transport and record IO")
}
