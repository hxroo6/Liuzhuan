package com.liuzhuan.app.audio

import kotlin.math.abs
import kotlin.math.log10
import kotlin.math.sqrt

data class PcmLevel(val rms: Float = 0f, val peak: Float = 0f, val dbfs: Float = -96f)

/** Measures existing PCM16LE samples. Does not record, modify, retain or upload audio. */
object PcmLevelMeter {
    fun measure(pcm: ByteArray): PcmLevel {
        if (pcm.size < 2) return PcmLevel()
        var squares = 0.0
        var peak = 0
        var count = 0
        for (i in 0 until pcm.size - 1 step 2) {
            val value = ((pcm[i].toInt() and 255) or (pcm[i + 1].toInt() shl 8)).toShort().toInt()
            val amplitude = abs(value)
            peak = maxOf(peak, amplitude)
            squares += value.toDouble() * value
            count++
        }
        val rms = (sqrt(squares / count) / 32768.0).toFloat().coerceIn(0f, 1f)
        return PcmLevel(rms, peak / 32768f, if (rms > 0f) (20 * log10(rms.toDouble())).toFloat().coerceAtLeast(-96f) else -96f)
    }
}
