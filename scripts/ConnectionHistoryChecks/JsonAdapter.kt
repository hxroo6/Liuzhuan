package org.json

import groovy.json.JsonOutput
import groovy.json.JsonSlurper

/** Only the Android JSON API is adapted; production profiles and AES-GCM run unchanged. */
class JSONObject {
    internal val values: MutableMap<String,Any?>
    constructor() { values=linkedMapOf() }
    constructor(raw:String) {
        @Suppress("UNCHECKED_CAST")
        values=(JsonSlurper().parseText(raw) as Map<String,Any?>).toMutableMap()
    }
    internal constructor(raw:Map<*,*>) { values=raw.entries.associate { it.key.toString() to it.value }.toMutableMap() }
    fun put(key:String,value:Any?)=apply { values[key]=when(value) { is JSONObject->value.values;is JSONArray->value.values;else->value } }
    fun optString(key:String,default:String="")=values[key]?.toString() ?: default
    fun optInt(key:String,default:Int=0)=(values[key] as? Number)?.toInt() ?: values[key]?.toString()?.toIntOrNull() ?: default
    fun optLong(key:String,default:Long=0)=(values[key] as? Number)?.toLong() ?: values[key]?.toString()?.toLongOrNull() ?: default
    fun optJSONArray(key:String)=(values[key] as? List<*>)?.let { JSONArray(it) }
    override fun toString():String=JsonOutput.toJson(values)
}
class JSONArray {
    internal val values: MutableList<Any?>
    constructor() { values=mutableListOf() }
    constructor(raw:List<*>) { values=raw.toMutableList() }
    fun put(value:Any?)=apply { values.add(if(value is JSONObject) value.values else value) }
    fun length()=values.size
    fun optJSONObject(index:Int)=(values.getOrNull(index) as? Map<*,*>)?.let { JSONObject(it) }
}
