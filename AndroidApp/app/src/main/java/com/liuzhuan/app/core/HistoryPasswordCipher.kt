package com.liuzhuan.app.core

import java.util.Base64
import javax.crypto.Cipher
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/** 新历史记录的口令单独加密；随机 IV + GCM 标签防止密文被悄悄改写。 */
class HistoryPasswordCipher(private val key: () -> SecretKey) {
    fun seal(password: String): String {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, key())
        val encrypted = cipher.doFinal(password.toByteArray(Charsets.UTF_8))
        return "v1:" + Base64.getEncoder().encodeToString(cipher.iv + encrypted)
    }

    fun open(sealed: String): String {
        require(sealed.startsWith("v1:")) { "Unsupported history credential" }
        val bytes = Base64.getDecoder().decode(sealed.substring(3))
        require(bytes.size >= 28) { "Invalid history credential" }
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, bytes.copyOfRange(0, 12)))
        return String(cipher.doFinal(bytes.copyOfRange(12, bytes.size)), Charsets.UTF_8)
    }
}
