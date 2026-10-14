package com.liuzhuan.app.core

import org.json.JSONArray
import org.json.JSONObject
import java.util.Locale

data class ConnectionProfile(val host: String, val port: Int, val lastConnected: Long, val sealedPassword: String) {
    val id: String get() = "${host.trim().lowercase(Locale.ROOT)}:$port"
}

object ConnectionProfiles {
    const val LIMIT = 20
    fun validate(profile: ConnectionProfile) {
        require(profile.host.isNotBlank() && profile.host.length <= 253 && profile.host == profile.host.trim())
        require(profile.port in 1..65534 && profile.lastConnected > 0)
        require(profile.sealedPassword.startsWith("v1:") && profile.sealedPassword.length <= 8192)
    }
    fun remember(old: List<ConnectionProfile>, profile: ConnectionProfile): List<ConnectionProfile> {
        validate(profile)
        return (listOf(profile) + old.filter { it.id != profile.id }).sortedByDescending { it.lastConnected }.take(LIMIT)
    }
    fun decode(raw: String): List<ConnectionProfile> {
        if (raw.isBlank()) return emptyList()
        require(raw.length <= 200_000)
        val root = JSONObject(raw)
        require(root.optInt("version") == 1)
        val entries = root.optJSONArray("entries") ?: error("History entries missing")
        require(entries.length() <= LIMIT)
        val profiles = (0 until entries.length()).map { index ->
            val item = entries.optJSONObject(index) ?: error("Invalid history entry")
            ConnectionProfile(item.optString("host"), item.optInt("port"), item.optLong("lastConnected"), item.optString("sealedPassword"))
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
                .put("lastConnected", profile.lastConnected).put("sealedPassword", profile.sealedPassword))
        }
        return JSONObject().put("version", 1).put("entries", entries).toString()
    }
}
