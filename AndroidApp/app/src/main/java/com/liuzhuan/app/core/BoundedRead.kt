package com.liuzhuan.app.core

import java.io.ByteArrayOutputStream
import java.io.InputStream

/** 最多消费 limit+1 字节，超限不继续读；调用者负责关闭流。 */
fun InputStream.readWithinLimit(limit: Int): ByteArray? {
    require(limit >= 0 && limit < Int.MAX_VALUE)
    val output = ByteArrayOutputStream(minOf(limit, 16 * 1024))
    val buffer = ByteArray(minOf(limit + 1, 16 * 1024))
    var remaining = limit + 1
    while (remaining > 0) {
        val count = read(buffer, 0, minOf(buffer.size, remaining))
        if (count < 0) break
        if (count == 0) {
            val byte = read()
            if (byte < 0) break
            output.write(byte)
            remaining--
        } else {
            output.write(buffer, 0, count)
            remaining -= count
        }
    }
    return if (output.size() > limit) null else output.toByteArray()
}
