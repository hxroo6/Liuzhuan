package com.liuzhuan.app.core

import android.content.Context
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.map

/** 配置存储（DataStore Preferences） */
private val Context.settingsDataStore by preferencesDataStore(name = "settings")

class SettingsStore(context: Context) {
    private val context = context.applicationContext

    companion object {
        private val KEY_IP = stringPreferencesKey("server_ip")
        private val KEY_PORT = stringPreferencesKey("server_port")
        private val KEY_PASSWORD = stringPreferencesKey("password")
        private val KEY_DEVICE_NAME = stringPreferencesKey("device_name")
        private val KEY_AUTO_SEND = booleanPreferencesKey("auto_send_clipboard")
        private val KEY_BALANCED_POWER = booleanPreferencesKey("balanced_power")
    }

    data class Settings(
        val serverIp: String = "",
        val serverPort: String = "8899",
        val password: String = "",
        val deviceName: String = android.os.Build.MODEL,
        val autoSendClipboard: Boolean = true,
        val balancedPower: Boolean = false
    )

    val settings: Flow<Settings> = context.settingsDataStore.data.map { prefs ->
        Settings(
            serverIp = prefs[KEY_IP] ?: "",
            serverPort = prefs[KEY_PORT] ?: "8899",
            password = prefs[KEY_PASSWORD] ?: "",
            deviceName = prefs[KEY_DEVICE_NAME] ?: android.os.Build.MODEL,
            autoSendClipboard = prefs[KEY_AUTO_SEND] ?: true,
            balancedPower = prefs[KEY_BALANCED_POWER] ?: false
        )
    }

    suspend fun save(s: Settings) {
        context.settingsDataStore.edit { prefs ->
            prefs[KEY_IP] = s.serverIp
            prefs[KEY_PORT] = s.serverPort
            prefs[KEY_PASSWORD] = s.password
            prefs[KEY_DEVICE_NAME] = s.deviceName
            prefs[KEY_AUTO_SEND] = s.autoSendClipboard
            prefs[KEY_BALANCED_POWER] = s.balancedPower
        }
    }
}
