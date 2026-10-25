package com.liuzhuan.checks

import com.liuzhuan.app.core.*
import com.liuzhuan.app.data.MaterialItem
import kotlinx.coroutines.runBlocking
import org.json.JSONObject
import org.json.JSONArray

/** Synthetic, memory-backed page IO. Counts work; does not claim device/KeyStore timings. */
fun main()=runBlocking {
    val values=linkedMapOf<String,String>();var reads=0;var bytes=0L
    val records=object:LibraryRecords {
        override fun read(name:String):String? {val value=values[name];if(value!=null&&"-page-" in name){reads++;bytes+=value.toByteArray().size};return value}
        override fun write(name:String,text:String){values[name]=text}
        override fun delete(name:String){values.remove(name)}
        override fun names()=values.keys.toList()
    }
    val w=LibraryWorkspace(records,"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa:bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")
    val row=MaterialItem("r","Text","name",0,1000,offlineText="T".repeat(800),textComplete=true)
    w.download(fetch={offset,_->val end=minOf(50000,offset+100)
        JSONObject().put("sequence",1).put("total",50000).put("nextOffset",end).put("items",JSONArray().apply{(offset until end).forEach{put(LibraryWorkspace.itemJson(row.copy(id="row-$it",name="Name $it")))}})
    })
    var started=System.nanoTime();w.query("","All",false,"","",0)
    println("COLD_QUERY ms=${(System.nanoTime()-started)/1e6} pageReads=$reads bytes=$bytes")
    check(reads==500)
    reads=0;bytes=0;started=System.nanoTime()
    repeat(10){page->val result=w.query("","All",false,"","",page*100);check(result.total==50000&&result.rows.first().id=="row-${page*100}"&&result.rows.size==100)}
    println("TEN_CACHED_PAGES ms=${(System.nanoTime()-started)/1e6} pageReads=$reads bytes=$bytes")
    check(reads==10)
    w.stage(listOf(row.copy(id="row-0") to LibraryMetadata.of(row).copy(favorite=true)))
    reads=0;check(w.query("","All",false,"","",0).rows.first().favorite&&reads==1)
    println("PASS 50000-row query cache preserves page order, bounds reads, and invalidates after draft changes; memory IO only")
}
