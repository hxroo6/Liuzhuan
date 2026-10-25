package com.liuzhuan.app.core

import org.json.JSONArray
import org.json.JSONObject
import java.security.MessageDigest
import java.util.UUID

data class SavedLibrarySearch(val id:String,val name:String,val search:String="",val type:String="All",val favorite:Boolean=false,val group:String="",val tag:String="")

/** Explicit local preferences only: no keystroke history, material IDs or network writes. IO owner serializes access. */
class LibrarySavedSearches(private val records:LibraryRecords,val target:String) {
    private val key=recordName(target)
    private var saved=read()
    fun items():List<SavedLibrarySearch> = saved.toList()
    private fun normalize(value:SavedLibrarySearch):SavedLibrarySearch {
        require(runCatching {UUID.fromString(value.id).toString()==value.id}.getOrDefault(false)){"常用搜索身份无效"}
        val name=value.name.trim();val group=value.group.trim();val tag=value.tag.trim()
        require(name.isNotEmpty()&&name.length<=40&&name.none {it.isISOControl()}){"名称需为1–40字，不能包含控制字符"}
        require(value.search.length<=256){"搜索词最多256字"}
        require(value.type in setOf("All","Image","Text","Video","Audio","Other")){"素材类型无效"}
        require(group.length<=48&&group.none {it.isISOControl()}&&tag.length<=32&&tag.none {it.isISOControl()}){"分组或标签超出范围"}
        return value.copy(name=name,group=group,tag=tag)
    }
    private fun read():List<SavedLibrarySearch> {
        val raw=records.read(key)?:return emptyList();require(raw.toByteArray().size<=128*1024){"常用搜索记录过大，原记录保留"}
        val value=JSONObject(raw);require(value.getInt("schema")==1&&value.getString("target")==target){"常用搜索记录版本或素材库不符"}
        val a=value.getJSONArray("items");require(a.length()<=10){"每个素材库最多10个常用搜索"}
        val result=(0 until a.length()).map {i->val v=a.getJSONObject(i);normalize(SavedLibrarySearch(v.getString("id"),v.getString("name"),v.getString("search"),v.getString("type"),v.getBoolean("favorite"),v.getString("group"),v.getString("tag")))}
        require(result.map {it.id}.distinct().size==result.size&&result.indices.all {i->result.take(i).none {it.name.equals(result[i].name,true)}}){"常用搜索名称或身份重复，原记录保留"}
        return result
    }
    private fun commit(next:List<SavedLibrarySearch>) {
        val a=JSONArray().apply {next.forEach {put(JSONObject().put("id",it.id).put("name",it.name).put("search",it.search).put("type",it.type).put("favorite",it.favorite).put("group",it.group).put("tag",it.tag))}}
        records.write(key,JSONObject().put("schema",1).put("target",target).put("items",a).toString());saved=next
    }
    fun save(name:String,search:String,type:String,favorite:Boolean,group:String,tag:String,id:String?=null):SavedLibrarySearch {
        if(id!=null)check(saved.any {it.id==id}){"此常用搜索已被移除，请刷新"}else check(saved.size<10){"每个素材库最多10个常用搜索，请先移除一项"}
        val value=normalize(SavedLibrarySearch(id?:UUID.randomUUID().toString(),name,search,type,favorite,group,tag))
        check(saved.none {it.id!=value.id&&it.name.equals(value.name,true)}){"此名称已存在，请换一个名称或更新原项"}
        commit(if(id==null)saved+value else saved.map {if(it.id==id)value else it});return value
    }
    fun delete(id:String) {check(saved.any {it.id==id}){"此常用搜索已被移除"};commit(saved.filterNot {it.id==id})}
    companion object {
        private fun recordName(target:String):String {
            val parts=target.split(':');require(parts.size==2&&parts.all {runCatching {UUID.fromString(it).toString()==it}.getOrDefault(false)}){"需要已鉴权的电脑和素材库身份"}
            return "library-r05-"+MessageDigest.getInstance("SHA-256").digest(target.toByteArray()).joinToString(""){"%02x".format(it)}+"-searches"
        }
        fun remove(records:LibraryRecords,target:String)=records.delete(recordName(target))
    }
}
