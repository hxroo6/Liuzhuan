package com.liuzhuan.app.net

import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec
import java.security.MessageDigest

/** A discovery hint is not trust. Verify a fresh pairing-secret proof before following a changed address. */
object IdentityProof {
    fun verify(auth:String,nonce:String,id:String,port:Int,name:String,proof:String):Boolean {
        if(nonce.length!=32 || proof.length!=64)return false
        return runCatching {
            val mac=Mac.getInstance("HmacSHA256").apply {init(SecretKeySpec(auth.toByteArray(Charsets.UTF_8),"HmacSHA256"))}
            val expected=mac.doFinal("LZ-ID-1|$nonce|$id|$port|$name".toByteArray(Charsets.UTF_8))
            val received=proof.chunked(2).map {it.toInt(16).toByte()}.toByteArray()
            MessageDigest.isEqual(expected,received)
        }.getOrDefault(false)
    }
}
