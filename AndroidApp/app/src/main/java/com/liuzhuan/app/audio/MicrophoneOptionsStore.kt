package com.liuzhuan.app.audio

import android.content.Context
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.intPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.preferencesDataStore
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.*

private val Context.microphoneOptionsDataStore by preferencesDataStore(name = "microphone_options")

data class MicrophoneOptions(val suppressSilence: Boolean = true, val thresholdDb: Int = -42) {
    val thresholdLabel: String get() = when (thresholdDb) { -48 -> "灵敏"; -36 -> "抗噪"; else -> "标准" }
    fun normalized() = copy(thresholdDb = thresholdDb.takeIf { it in listOf(-48, -42, -36) } ?: -42)
}

/** Persistent preferences only. Never stores capture consent, connection state or invitations. */
class MicrophoneOptionsStore(context: Context) {
    companion object {
        private val writes = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        private val writeGate = Any()
        private var lastWrite: Deferred<Unit>? = null
    }
    private val store = context.applicationContext.microphoneOptionsDataStore
    private val suppressionKey = booleanPreferencesKey("suppress_silence")
    private val thresholdKey = intPreferencesKey("threshold_db")
    val options = store.data.map { prefs ->
        MicrophoneOptions(prefs[suppressionKey] ?: true, prefs[thresholdKey] ?: -42).normalized()
    }
    fun save(options: MicrophoneOptions): Deferred<Unit> = synchronized(writeGate) {
        val value = options.normalized()
        val previous = lastWrite
        writes.async {
            previous?.join()
            store.edit { prefs -> prefs[suppressionKey] = value.suppressSilence; prefs[thresholdKey] = value.thresholdDb }
            Unit
        }.also { lastWrite = it }
    }
    suspend fun current(): MicrophoneOptions {
        // A quick dismiss/reconnect must not race the user's most recent save.
        synchronized(writeGate) { lastWrite }?.await()
        return options.first()
    }
}
