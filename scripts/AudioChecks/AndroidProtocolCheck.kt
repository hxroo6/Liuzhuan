import com.liuzhuan.app.audio.AudioProtocol
import com.liuzhuan.app.audio.PcmLevelMeter
import com.liuzhuan.app.audio.SilenceGate
import com.liuzhuan.app.audio.MicrophoneLifetime
import java.io.File
import java.nio.ByteBuffer
import java.nio.ByteOrder

fun main(args: Array<String>) {
    val pcm = ByteArray(AudioProtocol.PAYLOAD) { 23 }
    val encoded = AudioProtocol.frame(pcm, 7, 0, 1234, 5678)
    val header = ByteBuffer.wrap(encoded).order(ByteOrder.BIG_ENDIAN)
    check(encoded.size == 684)
    check(header.short.toInt() == 1 && header.short.toInt() == 1)
    check(header.long == 7L && header.long == 0L && header.long == 1234L && header.long == 5678L)
    check(header.int == 320 && header.int == 640)
    check(encoded.copyOfRange(44, encoded.size).contentEquals(pcm))
    File(args[0]).writeBytes(encoded)
    println("PASS: Android production encoder, byte order, timestamps, frame size and unmodified PCM")
    check(PcmLevelMeter.measure(ByteArray(640)).let { it.rms == 0f && it.peak == 0f && it.dbfs == -96f })
    val half = PcmLevelMeter.measure(byteArrayOf(0, 64, 0, -64))
    check(kotlin.math.abs(half.rms - 0.5f) < 0.00001f && half.peak == 0.5f && kotlin.math.abs(half.dbfs + 6.0206f) < 0.01f)
    check(PcmLevelMeter.measure(byteArrayOf(0, -128)).peak == 1f)
    println("PASS: Android meter silence, signed PCM16LE, RMS, peak and dBFS; no synthetic waveform")
    val gate = SilenceGate()
    val silence = ByteArray(AudioProtocol.PAYLOAD)
    val loud = ByteArray(AudioProtocol.PAYLOAD).also { bytes -> for (i in bytes.indices step 2) { bytes[i] = 0; bytes[i+1] = 8 } }
    var offset = 0L
    fun feed(bytes: ByteArray): List<SilenceGate.Frame> {
        val result = gate.accept(bytes, offset, offset * 1_000_000 / AudioProtocol.RATE, PcmLevelMeter.measure(bytes).dbfs)
        offset += AudioProtocol.SAMPLES
        return result
    }
    repeat(500) { check(feed(silence).isEmpty()) }
    check(gate.quiet)
    check(feed(loud).isEmpty()); check(feed(silence).isEmpty())
    check(gate.quiet) // isolated click cannot open the gate
    check(feed(loud).isEmpty())
    val attack = feed(loud)
    check(attack.size == 10 && !gate.quiet && attack.zipWithNext().all { (a,b) -> b.sampleOffset - a.sampleOffset == 320L })
    check(attack.last().pcm.contentEquals(loud))
    loud.fill(0); check(attack.last().pcm.any { it.toInt() != 0 }) // input array reuse must not corrupt pre-roll
    repeat(25) { check(feed(silence).size == 1) }
    check(gate.quiet && feed(silence).isEmpty())
    gate.reset(); check(gate.quiet)
    val speech = ByteArray(AudioProtocol.PAYLOAD).also { bytes -> for (i in bytes.indices step 2) bytes[i+1] = 8 }
    feed(speech); val fresh = feed(speech)
    check(fresh.size == 2) // no pre-roll survives pause/generation boundary
    println("PASS: silence sends no PCM; isolated-click rejection, bounded 200ms pre-roll, 500ms hangover, buffer ownership and reset boundaries")
    val remote = MicrophoneLifetime(false, 1234)
    listOf(60_001L, 16 * 60_000L, 24 * 60 * 60_000L).forEach { check(!remote.expired(1234 + it)) }
    check(!remote.shouldRenew(1234 + 19 * 60_000L) && remote.shouldRenew(1234 + 20 * 60_000L))
    remote.renewed(1234 + 20 * 60_000L)
    check(!remote.shouldRenew(1234 + 21 * 60_000L) && remote.shouldRenew(1234 + 40 * 60_000L))
    val local = MicrophoneLifetime(true, 1234)
    check(!local.expired(1234 + 59_999) && local.expired(1234 + 60_000) && local.secondsLeft(1234) == 60 && !local.shouldRenew(1234 + 60 * 60_000))
    println("PASS: remote standby has no idle/15-minute/day expiry; bounded wake renewal; local test still ends at 60 seconds")
}
