package com.liuzhuan.app.audio

import java.nio.ByteBuffer
import java.nio.ByteOrder

/** Mirror of PC AudioProtocol.cs. PCM payload stays little endian, header is big endian. */
object AudioProtocol {
    const val VERSION = 1
    const val KIND = "liuzhuan-mic"
    const val RATE = 16000
    const val SAMPLES = 320
    const val PAYLOAD = SAMPLES * 2
    const val HEADER = 44
    const val FRAME = HEADER + PAYLOAD
    fun frame(pcm: ByteArray, generation: Long, sequence: Long, sampleOffset: Long, captureUs: Long): ByteArray =
        ByteBuffer.allocate(FRAME).order(ByteOrder.BIG_ENDIAN)
            .putShort(VERSION.toShort()).putShort(1) // estimated monotonic capture time
            .putLong(generation).putLong(sequence).putLong(sampleOffset).putLong(captureUs)
            .putInt(SAMPLES).putInt(PAYLOAD).put(pcm).array()
}
