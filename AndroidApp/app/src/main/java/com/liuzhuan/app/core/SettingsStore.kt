package com.liuzhuan.app.core

import android.content.Context
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.map

/** 配置存储（DataStore Preferences） */
class SettingsStore(private val context: Context) {

    private val Context.dataStore by preferencesDataStore(name = "settings")

    companion object {
        private val KEY_IP = stringPreferencesKey("server_ip")
        private val KEY_PORT = stringPreferencesKey("server_port")
        private val KEY_PASSWORD = stringPreferencesKey("password")
        private val KEY_DEVICE_NAME = stringPreferencesKey("device_name")
        private val KEY_AUTO_SEND = booleanPreferencesKey("auto_send_clipboard")
    }

    data class Settings(
        val serverIp: String = "",
        val serverPort: String = "8899",
        val password: String = "",
        val deviceName: String = android.os.Build.MODEL,
        val autoSendClipboard: Boolean = true
    )

    val settings: Flow<Settings> = context.dataStore.data.map { prefs ->
        Settings(
            serverIp = prefs[KEY_IP] ?: "",
            serverPort = prefs[KEY_PORT] ?: "8899",
            password = prefs[KEY_PASSWORD] ?: "",
            deviceName = prefs[KEY_DEVICE_NAME] ?: android.os.Build.MODEL,
            autoSendClipboard = prefs[KEY_AUTO_SEND] ?: true
        )
    }

    suspend fun save(s: Settings) {
        context.dataStore.edit { prefs ->
            prefs[KEY_IP] = s.serverIp
            prefs[KEY_PORT] = s.serverPort
            prefs[KEY_PASSWORD] = s.password
            prefs[KEY_DEVICE_NAME] = s.deviceName
            prefs[KEY_AUTO_SEND] = s.autoSendClipboard
        }
    }
}
