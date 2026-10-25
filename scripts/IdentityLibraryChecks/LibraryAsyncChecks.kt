package com.liuzhuan.checks

import com.liuzhuan.app.MaterialLibrary
import com.liuzhuan.app.core.*
import com.liuzhuan.app.data.MaterialItem
import com.liuzhuan.app.net.LanClient
import kotlinx.coroutines.*
import kotlinx.coroutines.channels.Channel
import org.json.JSONObject
import org.json.JSONArray
import java.io.File
import java.util.concurrent.atomic.AtomicReference
import java.util.concurrent.atomic.AtomicInteger

fun main(args:Array<String>)=runBlocking {
    var checks=0
    fun verify(ok:Boolean,label:String){check(ok){label};checks++;println("PASS $label")}
    val device="aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";val library="bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";val target="$device:$library"
    val base=MaterialItem("async","Text","Async row",0,1000,group="base",note="base")
    val records=FileRecords(File(args[0],"async-workspace"));var w=LibraryWorkspace(records,target)
    fun response(sent:LibraryDraft,item:MaterialItem,code:String="saved")=JSONObject().put("results",JSONArray().put(JSONObject().put("id",sent.id).put("code",code).put("item",LibraryWorkspace.itemJson(item))))
    w.stage(listOf(base to LibraryMetadata.of(base).copy(group="first")));var old=w.pending().single()
    w.stage(listOf(base to old.desired.copy(group="second")),setOf(base.id));val latestToken=w.pending().single().token
    w.acknowledge(listOf(old),response(old,old.desired.apply(base)))
    w=LibraryWorkspace(records,target)
    verify(w.pending().single().token==latestToken&&w.pending().single().base.group=="first"&&w.pending().single().desired.group=="second","late saved ACK advances only exactly confirmed same-origin baseline and durably preserves newer token and same-field intent")
    w.discard(base.id);w.stage(listOf(base to LibraryMetadata.of(base).copy(favorite=true)));old=w.pending().single()
    w.stage(listOf(base to LibraryMetadata.of(base)),setOf(base.id))
    verify(w.pending().size==1,"reverting to the original metadata while an old write is in flight remains a durable intent")
    w.acknowledge(listOf(old),response(old,old.desired.apply(base)))
    verify(w.pending().single().base.favorite&&!w.pending().single().desired.favorite,"late ACK cannot erase a newer reversal to the original value")
    w.discard(base.id);w.stage(listOf(base to LibraryMetadata.of(base).copy(group="first")));old=w.pending().single()
    w.stage(listOf(base to old.desired.copy(note="new local note")),setOf(base.id));w.acknowledge(listOf(old),response(old,old.desired.apply(base)))
    verify(w.pending().single().base.group=="first"&&w.pending().single().desired.note=="new local note","newer different-field edit inherits the confirmed old write without losing latest local metadata")
    w.discard(base.id);w.stage(listOf(base to LibraryMetadata.of(base).copy(group="first")));old=w.pending().single()
    w.stage(listOf(base to old.desired.copy(group="second")),setOf(base.id));w.acknowledge(listOf(old),response(old,old.desired.apply(base).copy(note="independent desktop note")))
    verify(w.pending().single().base.group=="first"&&w.pending().single().base.note=="base"&&w.pending().single().desired.group=="second","ACK advances only its proven local field while independent remote metadata retains the original conflict baseline")
    val before=w.pending().single();w.acknowledge(listOf(old),response(old,base.copy(group="desktop"),"conflict"))
    verify(w.pending().single()==before,"late conflict from an older token cannot mark or overwrite a newer local draft")
    w.discard(base.id);w.stage(listOf(base to LibraryMetadata.of(base).copy(group="first")));old=w.pending().single()
    w.stage(listOf(base to LibraryMetadata.of(base))) // No in-process flight is needed after an uncertain prior attempt.
    w=LibraryWorkspace(records,target);val reversalDraft=w.pending().single()
    w.acknowledge(listOf(reversalDraft),response(reversalDraft,base.copy(group="desktop",note="independent desktop note")))
    verify(w.pending().single().issue.isNotBlank()&&w.pending().single().desired.group=="base"&&w.pending().single().revertedFields==setOf("group"),"uncertain write result across restart cannot turn a durable reversal into a silently acknowledged no-op")
    w.rebase(base.id)
    verify(w.pending().single().base.group=="desktop"&&w.pending().single().desired.group=="base"&&w.pending().single().desired.note=="independent desktop note","explicit review keeps the reversal field while preserving unrelated remote metadata")
    w.discard(base.id);w.stage(listOf(base to LibraryMetadata.of(base).copy(tags=listOf("first"))));old=w.pending().single()
    w.stage(listOf(base to old.desired.copy(tags=listOf("second"))));w.acknowledge(listOf(old),response(old,old.desired.apply(base).copy(tags=listOf("FIRST"))))
    verify(w.pending().single().base.tags==listOf("FIRST")&&w.pending().single().desired.tags==listOf("second"),"acknowledged tag fields use the server's ordinal case-insensitive equality without losing the newer tag edit")

    w.discard(base.id);w.stage(listOf(base to LibraryMetadata.of(base).copy(group="phone-one")),editedFields=mapOf(base.id to setOf("group")))
    val remote=base.copy(note="PC-new-note",tags=listOf("PC-tag"));val shown=w.overlay(remote)
    w.stage(listOf(shown to LibraryMetadata.of(shown).copy(group="phone-two")),editedFields=mapOf(base.id to setOf("group")))
    verify(!w.pending().single().changes("note")&&!w.pending().single().changes("tags")&&w.pending().single().base.note==base.note,
        "re-editing only group never promotes displayed newer PC note or tags into local intents and preserves original baseline")
    verify(w.overlay(remote.copy(note="PC-newer-note")).note=="PC-newer-note"&&w.overlay(remote).group=="phone-two",
        "newer unedited remote fields continue to flow through beside the durable group draft")
    w=LibraryWorkspace(records,target)
    verify(!w.pending().single().changes("note")&&w.pending().single().desired.note==base.note&&w.overlay(remote).note=="PC-new-note",
        "explicit edited-field semantics survive durable reload without freezing unrelated remote values")
    val displayed=w.overlay(remote)
    w.stage(listOf(displayed to LibraryMetadata.of(displayed).copy(note="phone-note")),editedFields=mapOf(base.id to setOf("note")))
    verify(w.pending().single().changes("note")&&w.pending().single().desired.group=="phone-two"&&w.pending().single().base.note==base.note,
        "explicit new-field edit adds only that intent while keeping older group intent and conflict baseline")
    val stagedBefore=w.pending().single()
    verify(runCatching{w.stage(listOf(displayed to LibraryMetadata.of(displayed)),editedFields=mapOf("wrong-item" to setOf("note")))}.isFailure&&w.pending().single()==stagedBefore,
        "edited-field maps cannot apply to another item or mutate drafts when rejected")
    w.discard(base.id);w.stage(listOf(base to LibraryMetadata.of(base).copy(group="first")),editedFields=mapOf(base.id to setOf("group")))
    w.stage(listOf(base to LibraryMetadata.of(base)),editedFields=mapOf(base.id to setOf("group")))
    val reversed=w.overlay(remote)
    w.stage(listOf(reversed to LibraryMetadata.of(reversed).copy(note="phone-note")),editedFields=mapOf(base.id to setOf("note")))
    verify(w.pending().single().revertedFields==setOf("group")&&w.pending().single().desired.group==base.group,
        "editing another field preserves the explicit reversal marker after an uncertain earlier write")
    val unchanged=w.pending().single()
    w.stage(listOf(w.overlay(remote) to LibraryMetadata.of(w.overlay(remote))),editedFields=mapOf(base.id to emptySet()))
    verify(w.pending().single().base==unchanged.base&&w.pending().single().desired==unchanged.desired&&w.pending().single().revertedFields==unchanged.revertedFields,
        "saving a form with no edited fields preserves existing intents instead of accepting all displayed values")

    val disk=FileRecords(File(args[0],"async-controller"));val nextGate=AtomicReference<CompletableDeferred<Unit>?>(null)
    val entered=Channel<Unit>(Channel.UNLIMITED);val active=AtomicInteger();val peak=AtomicInteger();val calls=AtomicInteger();val mutations=AtomicInteger()
    var row=base;var loseAck=false
    val c=LanClient(library){q->
        if(q.optString("action")!="merge_batch")JSONObject().put("libraryId",library).put("sequence",row.revision).put("total",1).put("nextOffset",1).put("items",JSONArray().put(LibraryWorkspace.itemJson(row)))
        else {
            calls.incrementAndGet();val now=active.incrementAndGet();peak.updateAndGet {maxOf(it,now)}
            try {
                val gate=nextGate.getAndSet(null);entered.send(Unit);gate?.await()
                val edits=q.getJSONArray("edits");val results=JSONArray()
                for(i in 0 until edits.length()) {
                    val edit=edits.getJSONObject(i);val b=LibraryMetadata.parse(edit.getJSONObject("base"));val d=LibraryMetadata.parse(edit.getJSONObject("desired"));val r=LibraryMetadata.of(row)
                    val conflict=(b.favorite!=d.favorite&&r.favorite!=b.favorite&&r.favorite!=d.favorite)||(b.group!=d.group&&r.group!=b.group&&r.group!=d.group)||(b.tags!=d.tags&&r.tags!=b.tags&&r.tags!=d.tags)||(b.note!=d.note&&r.note!=b.note&&r.note!=d.note)
                    if(conflict)results.put(JSONObject().put("id",row.id).put("code","conflict").put("item",LibraryWorkspace.itemJson(row)))
                    else {
                        val desired=LibraryMetadata(if(b.favorite!=d.favorite)d.favorite else r.favorite,if(b.group!=d.group)d.group else r.group,if(b.tags!=d.tags)d.tags else r.tags,if(b.note!=d.note)d.note else r.note)
                        if(desired!=r){mutations.incrementAndGet();row=desired.apply(row).copy(revision=row.revision+1)}
                        results.put(JSONObject().put("id",row.id).put("code","saved").put("item",LibraryWorkspace.itemJson(row)))
                    }
                }
                if(loseAck){loseAck=false;throw java.io.IOException("injected lost ACK")}
                JSONObject().put("libraryId",library).put("results",results)
            }finally {active.decrementAndGet()}
        }
    }
    suspend fun settled(test:()->Boolean){withTimeout(12000){while(!test())delay(20)}}
    suspend fun stage(desired:LibraryMetadata){withTimeout(1500){MaterialLibrary.stage(listOf(row to desired),MaterialLibrary.state.value.epoch)}}
    suspend fun nextCall(){withTimeout(12000){entered.receive()}}
    MaterialLibrary.attachRecords(disk);delay(80);MaterialLibrary.connected(c,"$target:run1")
    settled{MaterialLibrary.state.value.canEditOffline&&!MaterialLibrary.state.value.loading}
    val gate=CompletableDeferred<Unit>();nextGate.set(gate)
    stage(LibraryMetadata.of(row).copy(group="one"));nextCall()
    verify(!gate.isCompleted&&LibraryWorkspace(disk,target).pending().single().desired.group=="one","stage returns only after durable local write but before a blocked network ACK")
    verify(MaterialLibrary.state.value.syncing&&!MaterialLibrary.state.value.workBusy&&MaterialLibrary.state.value.editStatus.contains("已保存到手机"),"network synchronization has separate busy status and leaves local editing available")
    stage(LibraryMetadata.of(row).copy(group="two"))
    verify(!gate.isCompleted&&LibraryWorkspace(disk,target).pending().single().desired.group=="two","another edit persists and returns while the previous network request remains blocked")
    val retries=(0 until 20).map {async {MaterialLibrary.syncPending()}};delay(50);retries.first().cancel();gate.complete(Unit)
    retries.drop(1).forEach {withTimeout(12000){it.await()}}
    settled{row.group=="two"&&MaterialLibrary.state.value.drafts.isEmpty()&&!MaterialLibrary.state.value.syncing}
    verify(calls.get()==2&&peak.get()==1,"repeated sync clicks are conflated into one serial follow-up and cancelled UI waiter cannot cancel the process worker")
    while(entered.tryReceive().isSuccess){}
    val reverse=CompletableDeferred<Unit>();nextGate.set(reverse);val oldFavorite=row.favorite
    stage(LibraryMetadata.of(row).copy(favorite=!oldFavorite));nextCall();stage(LibraryMetadata.of(row).copy(favorite=oldFavorite));reverse.complete(Unit)
    settled{row.favorite==oldFavorite&&MaterialLibrary.state.value.drafts.isEmpty()&&!MaterialLibrary.state.value.syncing}
    verify(calls.get()==4,"in-flight reversal is sent after the old ACK and restores the requested original value")
    while(entered.tryReceive().isSuccess){}
    val changing=CompletableDeferred<Unit>();nextGate.set(changing);stage(LibraryMetadata.of(row).copy(group="three"));nextCall()
    val otherLibrary="dddddddd-dddd-dddd-dddd-dddddddddddd";var foreignEdits=0
    val other=LanClient(otherLibrary){q->if(q.optString("action")=="merge_batch")foreignEdits++;JSONObject().put("libraryId",otherLibrary).put("sequence",0).put("total",0).put("nextOffset",0).put("items",JSONArray())}
    MaterialLibrary.connected(other,"cccccccc-cccc-cccc-cccc-cccccccccccc:$otherLibrary:run2");settled{MaterialLibrary.state.value.canEditOffline}
    changing.complete(Unit);settled{active.get()==0};delay(100)
    verify(foreignEdits==0&&MaterialLibrary.state.value.drafts.isEmpty()&&LibraryWorkspace(disk,target).pending().single().desired.group=="three","changing computer during a blocked request cannot misroute edits or drop the original library's durable draft")
    MaterialLibrary.connected(c,"$target:run3");settled{MaterialLibrary.state.value.canEditOffline&&MaterialLibrary.state.value.drafts.isEmpty()&&!MaterialLibrary.state.value.syncing}
    verify(row.group=="three","reconnecting original computer idempotently acknowledges a write completed after disconnect")
    loseAck=true;stage(LibraryMetadata.of(row).copy(group="four"));settled{row.group=="four"&&!MaterialLibrary.state.value.syncing&&MaterialLibrary.state.value.drafts.size==1}
    val failedCalls=calls.get();val applied=mutations.get();delay(550)
    verify(calls.get()==failedCalls&&MaterialLibrary.state.value.drafts.size==1,"failed ACK remains durable and causes no idle retry polling")
    MaterialLibrary.syncPending();settled{MaterialLibrary.state.value.drafts.isEmpty()&&!MaterialLibrary.state.value.syncing}
    verify(mutations.get()==applied,"explicit retry after lost ACK confirms without duplicate server mutation")
    val idle=calls.get();delay(450);verify(calls.get()==idle,"serial worker sleeps when no user or connection event requests synchronization")
    println("PASS $checks asynchronous durable library checks; production coordinator/workspace, gated substitute network and record IO")
}
