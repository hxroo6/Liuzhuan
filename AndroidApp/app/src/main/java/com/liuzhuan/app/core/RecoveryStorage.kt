package com.liuzhuan.app.core

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.AtomicFile
import java.io.File
import java.security.KeyStore
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey

/** Private, non-backup recovery data. Initialization happens after onCreate, on an IO dispatcher. */
class RecoveryStorage(context: Context, name: String) {
    private val file = AtomicFile(File(context.noBackupFilesDir, "recovery/$name.enc"))
    private val cipher = HistoryPasswordCipher { key() }
    @Synchronized fun read(): String? {
        if (!file.baseFile.exists() && !File(file.baseFile.path + ".bak").exists()) return null
        require(file.baseFile.length() <= 32L * 1024 * 1024) { "恢复记录过大" }
        return cipher.open(file.openRead().bufferedReader().use { it.readText() })
    }
    @Synchronized fun write(text: String) {
        require(text.toByteArray().size <= 16 * 1024 * 1024) { "恢复记录容量已满" }
        file.baseFile.parentFile!!.mkdirs()
        val bytes = cipher.seal(text).toByteArray()
        val output = file.startWrite()
        try { output.write(bytes); file.finishWrite(output) }
        catch (ex: Exception) { file.failWrite(output); throw ex }
    }
    @Synchronized fun delete()=file.delete()
    companion object {
        private var cachedKey:SecretKey?=null
        @Synchronized private fun key(): SecretKey {
            cachedKey?.let {return it}
            val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
            return (store.getKey("liuzhuan_recovery_v1", null) as? SecretKey
                ?: KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore").apply {
                    init(KeyGenParameterSpec.Builder("liuzhuan_recovery_v1", KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                        .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                        .setKeySize(256).build())
                }.generateKey()).also {cachedKey=it}
        }
    }
}
