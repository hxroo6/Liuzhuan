package com.liuzhuan.app.core

fun ByteArray.hexString(): String {
    val digits = "0123456789abcdef"
    val chars = CharArray(size * 2)
    forEachIndexed { index, byte ->
        val value = byte.toInt() and 255
        chars[index * 2] = digits[value ushr 4]
        chars[index * 2 + 1] = digits[value and 15]
    }
    return String(chars)
}
