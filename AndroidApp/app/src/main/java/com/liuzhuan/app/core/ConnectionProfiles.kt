package com.liuzhuan.app.core

import org.json.JSONArray
import org.json.JSONObject
import java.util.Locale

data class ConnectionProfile(val host: String, val port: Int, val lastConnected: Long, val sealedPassword: String,
    val deviceId:String="", val name:String="", val alias:String="",val addresses:List<String> = emptyList()) {
    val endpoint: String get() = "${host.trim().lowercase(Locale.ROOT)}:$port"
    val id: String get() = deviceId.ifBlank { endpoint }
    val title:String get() = alias.ifBlank {name}.ifBlank {endpoint}
}

object ConnectionProfiles {
    const val LIMIT = 20
    fun validate(profile: ConnectionProfile) {
        require(profile.host.isNotBlank() && profile.host.length <= 253 && profile.host == profile.host.trim())
        require(profile.port in 1..65534 && profile.lastConnected > 0)
        require(profile.sealedPassword.startsWith("v1:") && profile.sealedPassword.length <= 8192)
        require(profile.deviceId.isEmpty() || runCatching { java.util.UUID.fromString(profile.deviceId).toString()==profile.deviceId }.getOrDefault(false))
        require(profile.name.length<=64 && profile.alias.length<=64 && !(profile.name+profile.alias).any {it.isISOControl()})
        require(profile.addresses.size<=5 && profile.addresses.all {it.length<=260 && !it.any(Char::isISOControl)})
    }
    fun remember(old: List<ConnectionProfile>, profile: ConnectionProfile): List<ConnectionProfile> {
        validate(profile)
        val previous=old.firstOrNull {it.id==profile.id} ?: old.firstOrNull {it.deviceId.isEmpty() && it.endpoint==profile.endpoint}
        val merged=profile.copy(alias=profile.alias.ifBlank {previous?.alias.orEmpty()},
            addresses=(listOf(profile.endpoint)+previous?.addresses.orEmpty()+listOfNotNull(previous?.endpoint)).distinct().take(5))
        return (listOf(merged) + old.filter { it.id != profile.id && !(it.deviceId.isEmpty() && it.endpoint==profile.endpoint) }).sortedByDescending { it.lastConnected }.take(LIMIT)
    }
    fun decode(raw: String): List<ConnectionProfile> {
        if (raw.isBlank()) return emptyList()
        require(raw.length <= 200_000)
        val root = JSONObject(raw)
        require(root.optInt("version") in 1..2)
        val entries = root.optJSONArray("entries") ?: error("History entries missing")
        require(entries.length() <= LIMIT)
        val profiles = (0 until entries.length()).map { index ->
            val item = entries.optJSONObject(index) ?: error("Invalid history entry")
            ConnectionProfile(item.optString("host"), item.optInt("port"), item.optLong("lastConnected"), item.optString("sealedPassword"),
                item.optString("deviceId"),item.optString("name"),item.optString("alias"),
                item.optJSONArray("addresses")?.let {arr -> (0 until arr.length()).map {arr.optString(it)}}?:emptyList())
                .also(::validate)
        }
        require(profiles.map { it.id }.distinct().size == profiles.size)
        return profiles.sortedByDescending { it.lastConnected }
    }
    fun encode(profiles: List<ConnectionProfile>): String {
        require(profiles.size <= LIMIT)
        val entries = JSONArray()
        profiles.forEach { profile ->
            validate(profile)
            entries.put(JSONObject().put("host", profile.host).put("port", profile.port)
                .put("lastConnected", profile.lastConnected).put("sealedPassword", profile.sealedPassword)
                .put("deviceId",profile.deviceId).put("name",profile.name).put("alias",profile.alias).put("addresses",JSONArray(profile.addresses)))
        }
        return JSONObject().put("version", 2).put("entries", entries).toString()
    }
}
