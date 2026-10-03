package android.util

object Log { fun d(tag: String, message: String): Int = 0 }
object Base64 {
    const val DEFAULT = 0
    var decodes = 0
    fun decode(value: String, mode: Int): ByteArray { decodes++; return java.util.Base64.getDecoder().decode(value) }
}
