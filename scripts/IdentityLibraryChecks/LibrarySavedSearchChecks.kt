package com.liuzhuan.checks

import com.liuzhuan.app.MaterialLibrary
import com.liuzhuan.app.core.*
import com.liuzhuan.app.net.LanClient
import kotlinx.coroutines.*
import org.json.JSONObject
import org.json.JSONArray
import java.io.File

fun main(args:Array<String>)=runBlocking {
    val target="aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa:bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"
    val other="cccccccc-cccc-cccc-cccc-cccccccccccc:dddddddd-dddd-dddd-dddd-dddddddddddd"
    val disk=FileRecords(File(args[0],"saved-searches"));var store=LibrarySavedSearches(disk,target)
    var count=0;fun verify(ok:Boolean,label:String){check(ok){label};count++;println("PASS $label")}
    if(args[1]=="seed") {
        val first=store.save("  Daily  ","needle","Text",true,"Group","tag")
        verify(first.name=="Daily"&&first.favorite&&first.group=="Group"&&first.tag=="tag","explicit save records the complete normalized query condition")
        verify(runCatching{store.save("daily","","All",false,"","")}.isFailure&&store.items().size==1,"duplicate preset names are rejected without overwriting the original")
        val invalid=listOf("name","search","type","group","tag")
        invalid.forEach {field->verify(runCatching {store.save(if(field=="name")"x".repeat(41)else "Invalid",if(field=="search")"x".repeat(257)else "",if(field=="type")"Unknown" else "All",false,if(field=="group")"x".repeat(49)else "",if(field=="tag")"x".repeat(33)else "")}.isFailure&&store.items().size==1,"invalid $field is rejected before persistence")}
        val original=disk.names().associateWith {disk.read(it)};disk.fail="-searches"
        verify(runCatching{store.save("Failed","","All",false,"","")}.isFailure&&store.items().size==1&&disk.names().associateWith{disk.read(it)}==original,"failed persistent save never publishes success or mutates memory/disk")
        disk.fail="";repeat(9){store.save("Extra $it","$it","All",false,"","")}
        verify(runCatching{store.save("Eleventh","","All",false,"","")}.isFailure&&store.items().size==10,"per-library preset budget is ten")
        store.save("Updated","changed","Image",false,"NewGroup","newtag",first.id)
        verify(store.items().first().id==first.id&&store.items().first().search=="changed","explicit update preserves preset identity and replaces its condition")
        LibrarySavedSearches(disk,other).save("Other computer","other","All",false,"","")
        verify(store.items().none {it.name=="Other computer"},"different authenticated libraries keep independent local preferences")
    }else {
        verify(store.items().size==10&&store.items().first().name=="Updated"&&store.items().first().tag=="newtag","fresh process restores named query settings")
        LibraryWorkspace(disk,target).clearIndex()
        verify(LibrarySavedSearches(disk,target).items().size==10,"clearing offline material index preserves named query preferences")
        store.delete(store.items().last().id);verify(LibrarySavedSearches(disk,target).items().size==9,"explicit deletion persists without touching the other library")
        val key=disk.names().first {it.endsWith("-searches")&&JSONObject(disk.read(it)!!).getString("target")==target}
        disk.write(key,"broken preference fixture")
        verify(runCatching{LibrarySavedSearches(disk,target)}.isFailure&&disk.read(key)=="broken preference fixture","corrupt saved-search record is rejected and preserved instead of being reset")
        LibrarySavedSearches.remove(disk,target)
        verify(LibrarySavedSearches(disk,other).items().single().name=="Other computer"&&key !in disk.names(),"explicit workspace removal deletes only its own named searches")
    }

    val controller=FileRecords(File(args[0],"saved-search-controller"));val library=target.substringAfter(':');var requests=0
    val c=LanClient(library){requests++;JSONObject().put("libraryId",library).put("sequence",0).put("total",0).put("nextOffset",0).put("items",JSONArray())}
    suspend fun settled(test:()->Boolean){withTimeout(10000){while(!test())delay(20)}}
    MaterialLibrary.attachRecords(controller)
    val q=MaterialLibrary.Query("needle","Text",true,"Group","tag")
    if(args[1]=="seed") {
        delay(80);MaterialLibrary.connected(c,"$target:run1");settled{MaterialLibrary.state.value.canSaveSearches&&!MaterialLibrary.state.value.loading}
        var writes=0;controller.beforeWrite={if(it.endsWith("-searches"))writes++}
        MaterialLibrary.query(q);MaterialLibrary.saveSearch("Phone query",MaterialLibrary.state.value.epoch)
        verify(MaterialLibrary.state.value.savedSearches.single().search=="needle"&&writes==1,"coordinator publishes a named query only after its explicit persistent save")
        controller.fail="-searches"
        verify(runCatching{MaterialLibrary.saveSearch("Failed",MaterialLibrary.state.value.epoch)}.isFailure&&MaterialLibrary.state.value.savedSearches.size==1,"coordinator does not advertise a failed save")
        controller.fail="";val before=writes
        repeat(5){MaterialLibrary.query(q.copy(search="typing-$it"))};delay(400)
        verify(writes==before,"typing and applying live filters never collect or persist query history")
        MaterialLibrary.disconnected();val network=requests
        MaterialLibrary.applySearch(MaterialLibrary.state.value.savedSearches.single().id,MaterialLibrary.state.value.epoch)
        verify(MaterialLibrary.state.value.query==q&&requests==network,"saved filters can be applied offline without network traffic")
    }else {
        settled{MaterialLibrary.state.value.canSaveSearches&&MaterialLibrary.state.value.savedSearches.size==1}
        val saved=MaterialLibrary.state.value.savedSearches.single();val epoch=MaterialLibrary.state.value.epoch
        MaterialLibrary.applySearch(saved.id,epoch)
        verify(MaterialLibrary.state.value.query==q&&requests==0,"fresh offline process can restore and apply a saved full query")
        val foreign=LanClient(other.substringAfter(':')){JSONObject().put("libraryId",other.substringAfter(':')).put("sequence",0).put("total",0).put("nextOffset",0).put("items",JSONArray())}
        MaterialLibrary.connected(foreign,"$other:run2");settled{MaterialLibrary.state.value.canSaveSearches&&!MaterialLibrary.state.value.loading}
        verify(MaterialLibrary.state.value.savedSearches.isEmpty()&&runCatching{MaterialLibrary.applySearch(saved.id,epoch)}.isFailure,"stale preset action cannot apply original-library conditions to another computer")
        val key=controller.names().first {it.endsWith("-searches")};controller.write(key,"damaged")
        MaterialLibrary.connected(c,"$target:run3");settled{MaterialLibrary.state.value.canEditOffline&&!MaterialLibrary.state.value.loading&&MaterialLibrary.state.value.savedSearchError.isNotEmpty()}
        verify(!MaterialLibrary.state.value.canSaveSearches&&runCatching{MaterialLibrary.saveSearch("Overwrite",MaterialLibrary.state.value.epoch)}.isFailure&&controller.read(key)=="damaged","corrupt local preferences disable only preset writes while the material library remains usable")
    }
    println("PASS $count saved-library-search checks (${args[1]}); explicit preferences, substitute record IO and transport")
}
