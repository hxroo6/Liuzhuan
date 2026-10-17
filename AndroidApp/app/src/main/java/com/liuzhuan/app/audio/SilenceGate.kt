package com.liuzhuan.app.audio

/** Small energy gate, not speech recognition. Single capture-thread owner, bounded volatile PCM pre-roll. */
class SilenceGate(private val thresholdDb: Float = -42f) {
    data class Frame(val pcm: ByteArray, val sampleOffset: Long, val captureUs: Long)
    private val preRoll = ArrayDeque<Frame>()
    private var loudFrames = 0
    private var remaining = 0
    var quiet = true
        private set
    fun reset() { preRoll.clear(); loudFrames = 0; remaining = 0; quiet = true }
    fun accept(pcm: ByteArray, sampleOffset: Long, captureUs: Long, dbfs: Float): List<Frame> {
        val frame = Frame(pcm.copyOf(), sampleOffset, captureUs)
        if (quiet) {
            preRoll.addLast(frame)
            while (preRoll.size > 10) preRoll.removeFirst() // 200ms, never sent across a control boundary.
            loudFrames = if (dbfs >= thresholdDb) loudFrames + 1 else 0
            if (loudFrames < 2) return emptyList() // 40ms attack rejects isolated clicks.
            quiet = false; remaining = 25; loudFrames = 0
            return preRoll.toList().also { preRoll.clear() }
        }
        // A lower close threshold plus 500ms hangover preserves word tails and avoids chatter.
        if (dbfs >= thresholdDb - 6f) remaining = 25 else remaining--
        if (remaining <= 0) { quiet = true; loudFrames = 0 }
        return listOf(frame)
    }
}
