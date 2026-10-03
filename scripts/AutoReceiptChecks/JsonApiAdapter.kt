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
    fun put(key: String, value: Any?): JSONObject { values[key] = if (value is JSONObject) value.values else value; return this }
    fun optString(key: String, default: String = ""): String = values[key]?.toString() ?: default
    fun optLong(key: String, default: Long = 0): Long = (values[key] as? Number)?.toLong() ?: values[key]?.toString()?.toLongOrNull() ?: default
    fun optJSONObject(key: String): JSONObject? = (values[key] as? Map<*, *>)?.let { JSONObject(it) }
    fun optJSONArray(key: String): JSONArray? = (values[key] as? List<*>)?.let { JSONArray(it) }
    override fun toString(): String = JsonOutput.toJson(values)
}
class JSONArray(private val values: List<*>) {
    fun length() = values.size
    fun optJSONObject(index: Int): JSONObject? = (values.getOrNull(index) as? Map<*, *>)?.let { JSONObject(it) }
    fun optString(index: Int): String = values.getOrNull(index)?.toString() ?: ""
}
