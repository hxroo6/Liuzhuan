package org.json

import groovy.json.JsonOutput
import groovy.json.JsonSlurper

/** JVM 检查仅适配 Android org.json API；JSON 编解码使用本机已有 Groovy 实现。 */
class JSONObject {
    private val values: MutableMap<String, Any?>
    constructor() { values = linkedMapOf() }
    constructor(raw: String) {
        @Suppress("UNCHECKED_CAST")
        values = (JsonSlurper().parseText(raw) as Map<String, Any?>).toMutableMap()
    }
    internal constructor(raw: Map<*, *>) { values = raw.entries.associate { it.key.toString() to it.value }.toMutableMap() }
    fun put(key: String, value: Any?): JSONObject { values[key] = when(value){is JSONObject->value.values;is JSONArray->value.raw();else->value}; return this }
    fun optString(key: String, default: String = ""): String = values[key]?.toString() ?: default
    fun getString(key: String): String = values[key] as? String ?: error("Missing string: $key")
    fun isNull(key:String)=values[key]==null
    fun getLong(key:String):Long=(values[key] as? Number)?.toLong() ?: error("Missing long: $key")
    fun getBoolean(key:String):Boolean=values[key] as? Boolean ?: error("Missing boolean: $key")
    fun getJSONArray(key:String):JSONArray=optJSONArray(key) ?: error("Missing array: $key")
    fun getJSONObject(key:String):JSONObject=optJSONObject(key) ?: error("Missing object: $key")
    fun optInt(key:String,default:Int=0):Int=(values[key] as? Number)?.toInt() ?: default
    fun getInt(key: String): Int = (values[key] as? Number)?.toInt() ?: error("Missing integer: $key")
    fun optBoolean(key: String, default: Boolean = false): Boolean = values[key] as? Boolean ?: default
    fun optLong(key: String, default: Long = 0): Long = (values[key] as? Number)?.toLong() ?: values[key]?.toString()?.toLongOrNull() ?: default
    fun optJSONObject(key: String): JSONObject? = (values[key] as? Map<*, *>)?.let { JSONObject(it) }
    fun optJSONArray(key: String): JSONArray? = (values[key] as? List<*>)?.let { JSONArray(it) }
    override fun toString(): String = JsonOutput.toJson(values)
}
class JSONArray {
    private val values:MutableList<Any?>
    constructor(){values=mutableListOf()}
    constructor(raw:List<*>){values=raw.toMutableList()}
    constructor(raw:String){values=(JsonSlurper().parseText(raw) as List<*>).toMutableList()}
    fun raw():List<Any?> = values
    fun put(value:Any?):JSONArray {values.add(if(value is JSONObject) JsonSlurper().parseText(value.toString()) else value);return this}
    fun length()=values.size
    fun optJSONObject(index:Int):JSONObject?=(values.getOrNull(index) as? Map<*,*>)?.let {JSONObject(it)}
    fun getJSONObject(index:Int):JSONObject=optJSONObject(index) ?: error("Missing object")
    fun optString(index:Int):String=values.getOrNull(index)?.toString() ?: ""
    fun getString(index:Int):String=values[index] as String
    override fun toString():String=JsonOutput.toJson(values)
}
