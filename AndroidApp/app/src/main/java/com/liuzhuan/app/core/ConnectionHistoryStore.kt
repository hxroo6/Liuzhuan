package com.liuzhuan.app.core

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.catch
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.withContext
import java.security.KeyStore
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey

private val Context.connectionHistoryDataStore by preferencesDataStore(name = "connection_history")

private object HistoryKeys {
    @Synchronized fun get(): SecretKey {
        val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        return (store.getKey("liuzhuan_connection_history_v1", null) as? SecretKey) ?: KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore").apply {
            init(KeyGenParameterSpec.Builder("liuzhuan_connection_history_v1", KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256).build())
        }.generateKey()
    }
}

class ConnectionHistoryStore(context: Context) {
    private val app = context.applicationContext
    data class State(val entries: List<ConnectionProfile> = emptyList(), val unavailable: Boolean = false)
    private val key = stringPreferencesKey("profiles")
    private val cipher = HistoryPasswordCipher { HistoryKeys.get() }
    val state: Flow<State> = app.connectionHistoryDataStore.data.map { prefs ->
        runCatching { State(ConnectionProfiles.decode(prefs[key].orEmpty())) }.getOrElse { State(unavailable = true) }
    }.catch { emit(State(unavailable = true)) }

    suspend fun remember(settings: SettingsStore.Settings) = withContext(Dispatchers.IO) {
        val profile = ConnectionProfile(settings.serverIp.trim(), settings.serverPort.toInt(), System.currentTimeMillis(), cipher.seal(settings.password))
        app.connectionHistoryDataStore.edit { prefs ->
            prefs[key] = ConnectionProfiles.encode(ConnectionProfiles.remember(ConnectionProfiles.decode(prefs[key].orEmpty()), profile))
        }
    }

    suspend fun recall(id: String): SettingsStore.Settings = withContext(Dispatchers.IO) {
        val entries = ConnectionProfiles.decode(app.connectionHistoryDataStore.data.first()[key].orEmpty())
        val profile = entries.firstOrNull { it.id == id } ?: error("History entry no longer exists")
        val password = cipher.open(profile.sealedPassword)
        SettingsStore(app).settings.first().copy(serverIp = profile.host, serverPort = profile.port.toString(), password = password)
    }

    suspend fun remove(id: String) = withContext(Dispatchers.IO) {
        app.connectionHistoryDataStore.edit { prefs ->
            prefs[key] = ConnectionProfiles.encode(ConnectionProfiles.decode(prefs[key].orEmpty()).filter { it.id != id })
        }
    }

    suspend fun clear() = withContext(Dispatchers.IO) { app.connectionHistoryDataStore.edit { it.remove(key) } }
}
