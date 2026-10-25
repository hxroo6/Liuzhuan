package com.liuzhuan.app.audio

import android.app.*
import android.content.*
import android.content.pm.ServiceInfo
import android.media.*
import android.os.*
import android.util.Log
import androidx.core.app.NotificationCompat
import com.liuzhuan.app.MainActivity
import kotlinx.coroutines.*
import okhttp3.*
import okio.ByteString
import okio.ByteString.Companion.toByteString
import org.json.JSONObject
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

/** Foreground-only local consent entry; remote requests can only gate an already active capture. */
class MicrophoneService : Service() {
    companion object {
        const val START = "com.liuzhuan.app.audio.START"
        const val START_LOCAL = "com.liuzhuan.app.audio.START_LOCAL"
        const val END = "com.liuzhuan.app.audio.END"
        const val PAUSE = "com.liuzhuan.app.audio.PAUSE"
        const val RESUME = "com.liuzhuan.app.audio.RESUME"
        const val ECONOMY = "com.liuzhuan.app.audio.ECONOMY"
        private const val CHANNEL = "phone_microphone"
        private const val NOTIFICATION = 8902
        // Remains held if native cleanup fails, preventing a second capture instance.
        private val occupied = AtomicBoolean(false)
    }
    private val executor = Executors.newSingleThreadExecutor()
    private val dispatcher = executor.asCoroutineDispatcher()
    private val scope = CoroutineScope(SupervisorJob() + dispatcher)
    private val frameGate = Any()
    private val ending = AtomicBoolean(false)
    private var ownsCapture = false
    @Volatile private var running = false
    @Volatile private var captureReady = false
    private var recorder: AudioRecord? = null
    private var reader: Thread? = null
    private var client: OkHttpClient? = null
    private var socket: WebSocket? = null
    private var connected = false
    private var generation = 0L
    @Volatile private var forwarding = false
    private var paused = false
    private var sequence = 0L
    private var frames = 0L
    @Volatile private var capturedFrames = 0L
    @Volatile private var readCalls = 0L
    private var captureStarted = 0L
    private var energySaving = false
    private var localOnly = false
    private var lifetime: MicrophoneLifetime? = null
    private var lastControl = 0L
    private var lastDemand = 0L
    private var routeId = 0
    private var lastMessage = "正在准备麦克风"
    private var mutedReceiver: BroadcastReceiver? = null
    private var recordingCallback: AudioManager.AudioRecordingCallback? = null
    private var audioManager: AudioManager? = null
    private var wakeLock: PowerManager.WakeLock? = null
    private var notificationText = ""
    private var suppressSilence = true
    private var thresholdDb = -42
    @Volatile private var suppressionSupported = false
    @Volatile private var silenceQuiet = false
    private var consideredFrames = 0L
    private var silenceGate = SilenceGate()

    override fun onCreate() {
        super.onCreate()
        val manager = getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(NotificationChannel(CHANNEL, "手机麦克风", NotificationManager.IMPORTANCE_LOW))
        audioManager = getSystemService(AudioManager::class.java)
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if ((intent?.action == PAUSE || intent?.action == RESUME) && (!ownsCapture || ending.get())) {
            stopSelf(); return START_NOT_STICKY
        }
        when (intent?.action) {
            ECONOMY -> { energySaving = true; finishSession("节能连接 · 已释放麦克风，素材同步继续") }
            RESUME -> scope.launch {
                if (!ending.get()) {
                    synchronized(frameGate) { paused = false }
                    lastMessage = "正在采音，等待电脑请求转发"; sendStatus(); publish()
                }
            }
            END -> finishSession("麦克风会话已结束")
            PAUSE -> {
                synchronized(frameGate) { forwarding = false; paused = true; silenceGate.reset(); silenceQuiet = false }
                scope.launch { send("paused"); lastMessage = "已暂停传输，仍在采音待命"; publish() }
            }
            START, START_LOCAL -> {
                if (ownsCapture || ending.get()) return START_NOT_STICKY
                if (!occupied.compareAndSet(false, true)) {
                    stopSelf(); return START_NOT_STICKY
                }
                ownsCapture = true
                localOnly = intent.action == START_LOCAL
                suppressSilence = intent.getBooleanExtra("suppressSilence", true)
                thresholdDb = MicrophoneOptions(thresholdDb = intent.getIntExtra("thresholdDb", -42)).normalized().thresholdDb
                silenceGate = SilenceGate(thresholdDb.toFloat())
                try {
                    startForeground(NOTIFICATION, notification("正在准备麦克风"), ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE)
                    MicrophoneState.update(MicStatus(active = true, message = "正在准备麦克风"))
                    val payload = intent.getStringExtra("payload").orEmpty()
                    scope.launch { try { begin(payload) } catch (ex: Exception) {
                        Log.w("MicAudio", "START_FAILED ${ex.javaClass.simpleName}")
                        finishSession("无法启动麦克风", if (ex is SecurityException) MicIssue.PERMISSION else MicIssue.CAPTURE)
                    } }
                } catch (_: Exception) { finishSession("麦克风服务未能启动，请回到流转授权后重试", MicIssue.PERMISSION) }
            }
            else -> stopSelf()
        }
        return START_NOT_STICKY
    }

    @Suppress("MissingPermission")
    private fun begin(payload: String) {
        val json = if (localOnly) null else JSONObject(payload)
        if (json != null) require(json.getString("kind") == AudioProtocol.KIND && json.getInt("v") == AudioProtocol.VERSION)
        val host = json?.getString("ip").orEmpty()
        val port = json?.getInt("port") ?: 0
        val token = json?.getString("token").orEmpty()
        if (!localOnly) {
            require(AudioPairing.isPrivateV4(host) && port in 1024..65535)
            require(token.matches(Regex("[A-F0-9]{48}")))
        }
        MicrophoneLevels.reset()
        if (audioManager?.isMicrophoneMute == true) { finishSession("系统麦克风已静音", MicIssue.SYSTEM_BLOCKED); return }
        val min = AudioRecord.getMinBufferSize(AudioProtocol.RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT)
        require(min > 0)
        val record = AudioRecord.Builder()
            .setAudioSource(MediaRecorder.AudioSource.VOICE_RECOGNITION)
            .setAudioFormat(AudioFormat.Builder().setSampleRate(AudioProtocol.RATE)
                .setChannelMask(AudioFormat.CHANNEL_IN_MONO).setEncoding(AudioFormat.ENCODING_PCM_16BIT).build())
            .setBufferSizeInBytes(maxOf(min * 2, AudioProtocol.PAYLOAD * 4)).build()
        recorder = record
        require(record.state == AudioRecord.STATE_INITIALIZED)
        val builtin = audioManager!!.getDevices(AudioManager.GET_DEVICES_INPUTS).firstOrNull { it.type == AudioDeviceInfo.TYPE_BUILTIN_MIC }
            ?: error("未发现内置麦克风")
        require(record.setPreferredDevice(builtin))
        recordingCallback = object : AudioManager.AudioRecordingCallback() {
            override fun onRecordingConfigChanged(configs: MutableList<AudioRecordingConfiguration>) {
                if (configs.any { it.clientAudioSessionId == record.audioSessionId && it.isClientSilenced })
                    finishSession("系统已阻断麦克风", MicIssue.SYSTEM_BLOCKED)
            }
        }.also { audioManager!!.registerAudioRecordingCallback(it, Handler(Looper.getMainLooper())) }
        mutedReceiver = object : BroadcastReceiver() {
            override fun onReceive(context: Context?, intent: Intent?) {
                if (audioManager?.isMicrophoneMute == true) finishSession("系统麦克风已静音", MicIssue.SYSTEM_BLOCKED)
            }
        }.also { registerReceiver(it, IntentFilter(AudioManager.ACTION_MICROPHONE_MUTE_CHANGED), RECEIVER_NOT_EXPORTED) }
        val now = SystemClock.elapsedRealtime()
        lifetime = MicrophoneLifetime(localOnly, now)
        lastControl = now; lastDemand = now
        wakeLock = getSystemService(PowerManager::class.java).newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "Liuzhuan:Microphone").also {
            it.setReferenceCounted(false)
            it.acquire(if (localOnly) MicrophoneLifetime.LOCAL_WAKE_MS else MicrophoneLifetime.REMOTE_WAKE_MS)
        }
        record.startRecording()
        captureStarted = SystemClock.elapsedRealtime()
        require(record.recordingState == AudioRecord.RECORDSTATE_RECORDING)
        running = true
        Log.i("MicAudio", "CAPTURE_START localOnly=$localOnly rate=${AudioProtocol.RATE} suppressionRequested=$suppressSilence thresholdDb=$thresholdDb")
        lastMessage = if (localOnly) "本机试音：只显示波形，不连接电脑" else "正在连接电脑音频服务"
        reader = Thread({ capture(record) }, "liuzhuan-mic-capture").also { it.start() }
        if (!localOnly) {
        client = OkHttpClient.Builder().connectTimeout(5, TimeUnit.SECONDS).readTimeout(0, TimeUnit.SECONDS)
            .pingInterval(5, TimeUnit.SECONDS).retryOnConnectionFailure(false).build()
        val request = Request.Builder().url("ws://$host:$port/audio").build()
        socket = client!!.newWebSocket(request, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                scope.launch {
                    if (ending.get()) { webSocket.cancel(); return@launch }
                    synchronized(frameGate) { socket = webSocket }
                    webSocket.send(JSONObject().put("v", AudioProtocol.VERSION).put("type", "hello")
                        .put("token", token).put("device", Build.MODEL).toString())
                }
            }
            override fun onMessage(webSocket: WebSocket, text: String) {
                if (text.length > 8192) { finishSession("音频控制消息过大，会话已结束"); return }
                scope.launch { try { control(JSONObject(text)) } catch (_: Exception) { finishSession("音频协议不兼容", MicIssue.PROTOCOL) } }
            }
            override fun onMessage(webSocket: WebSocket, bytes: ByteString) { finishSession("电脑发送了无效的音频控制数据") }
            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) { finishSession("电脑音频连接已结束", MicIssue.NETWORK) }
            override fun onClosing(webSocket: WebSocket, code: Int, reason: String) { finishSession("电脑音频连接已结束", MicIssue.NETWORK) }
            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                Log.w("MicAudio", "CONNECTION_FAILED ${t.javaClass.simpleName}")
                finishSession("音频连接未能继续", if (t is java.net.ConnectException) MicIssue.DRIVER else MicIssue.NETWORK)
            }
        })
        }
        scope.launch {
            while (isActive && !ending.get()) {
                delay(1000)
                val time = SystemClock.elapsedRealtime()
                if (lifetime?.expired(time) == true) { finishSession("本机试音已结束"); break }
                if (!localOnly && time - lastControl > 5000) { finishSession("电脑音频心跳超时", MicIssue.NETWORK); break }
                val active = synchronized(frameGate) { forwarding }
                if (active && time - lastDemand > 5000) { finishSession("电脑录音需求已过期，会话已结束"); break }
                // User-authorized remote session stays armed until explicitly ended or interrupted.
                // Renew a non-reference-counted bounded lock, never stack acquires or use an unbounded lock.
                if (lifetime?.shouldRenew(time) == true && !ending.get()) {
                    try { wakeLock?.acquire(MicrophoneLifetime.REMOTE_WAKE_MS); lifetime?.renewed(time) }
                    catch (_: Exception) { finishSession("系统未允许继续保持麦克风，会话已结束"); break }
                }
                sendStatus(); publish()
            }
        }
    }

    private fun control(json: JSONObject) {
        if (ending.get()) return
        require(json.getInt("v") == AudioProtocol.VERSION)
        val type = json.getString("type")
        val gen = json.getLong("generation"); require(gen >= 0)
        lastControl = SystemClock.elapsedRealtime()
        when (type) {
            "welcome" -> {
                connected = true
                suppressionSupported = suppressSilence && json.optJSONObject("data")?.optBoolean("silenceSuppression", false) == true
                Log.i("MicAudio", "AUDIO_AUTHENTICATED silenceSuppression=$suppressionSupported")
                lastMessage = "正在采音，暂未转发"; sendStatus()
            }
            "start_forward" -> synchronized(frameGate) {
                if (!connected || !captureReady || paused || gen <= generation) return@synchronized
                generation = gen; sequence = 0; frames = 0
                silenceGate.reset(); consideredFrames = 0; silenceQuiet = suppressionSupported
                lastDemand = SystemClock.elapsedRealtime()
                check(send("forward_started"))
                forwarding = true; lastMessage = if (suppressionSupported) "按音量转发已开启 · 安静时仅保留心跳" else "正在向电脑发送麦克风音频"
                sendStatus()
            }
            "stop_forward" -> synchronized(frameGate) {
                if (gen < generation) return@synchronized
                forwarding = false; generation = gen
                silenceGate.reset(); silenceQuiet = false
                lastMessage = "正在采音，暂未转发"; send("forward_stopped")
            }
            "lease" -> {
                val wanted = json.getJSONObject("data").getBoolean("forwarding")
                synchronized(frameGate) {
                    if (gen == generation && wanted && forwarding) lastDemand = SystemClock.elapsedRealtime()
                    if (!wanted && gen >= generation) { generation = gen; forwarding = false; silenceGate.reset(); silenceQuiet = false }
                }
                // The one-second status tick is enough; avoid a second reply for every lease.
            }
            "end_session" -> finishSession("电脑已结束麦克风会话")
            else -> error("未知消息")
        }
        publish()
    }

    private fun capture(record: AudioRecord) {
        try {
            val pcm = ByteArray(AudioProtocol.PAYLOAD)
            val batch = ByteArray(AudioProtocol.PAYLOAD * 5)
            var offset = 0L
            while (running) {
                val batchGeneration = synchronized(frameGate) { generation }
                val batchSize = pcm.size * MicrophonePowerPolicy.batchFrames(silenceQuiet, MicrophoneLevels.observed, forwarding)
                var size = 0
                while (size < batchSize && running) {
                    val count = record.read(batch, size, batchSize - size, AudioRecord.READ_BLOCKING)
                    readCalls++
                    if (count <= 0) error("AudioRecord read failed")
                    size += count
                }
                if (!running) break
                val route = record.routedDevice
                if (route == null) { offset += batchSize / 2; continue }
                if (route.type != AudioDeviceInfo.TYPE_BUILTIN_MIC) error("麦克风路由已改变")
                if (routeId == 0) routeId = route.id else if (routeId != route.id) error("麦克风路由已改变")
                captureReady = true
                val capturedUs = SystemClock.elapsedRealtimeNanos() / 1000
                for (position in 0 until batchSize step pcm.size) {
                batch.copyInto(pcm, 0, position, position + pcm.size)
                capturedFrames++
                synchronized(frameGate) {
                    val needsMeter = MicrophoneLevels.observed || forwarding && suppressionSupported
                    val level = if (needsMeter) PcmLevelMeter.measure(pcm) else null
                    if (MicrophoneLevels.observed && level != null) MicrophoneLevels.accept(level, SystemClock.elapsedRealtime())
                    if (forwarding && generation == batchGeneration && !ending.get()) {
                        consideredFrames++
                        val ws = socket ?: throw java.io.IOException("Audio disconnected")
                        val timestamp = capturedUs - (batchSize - position - pcm.size) / 2 * 1_000_000L / AudioProtocol.RATE
                        val outgoing = if (suppressionSupported) silenceGate.accept(pcm, offset, timestamp, level!!.dbfs)
                            else listOf(SilenceGate.Frame(pcm, offset, timestamp))
                        for (frame in outgoing) {
                            if (ws.queueSize() > AudioProtocol.FRAME * 15) throw java.io.IOException("Audio queue full")
                            val bytes = AudioProtocol.frame(frame.pcm, generation, sequence++, frame.sampleOffset, frame.captureUs)
                            if (!ws.send(bytes.toByteString())) throw java.io.IOException("Audio send failed")
                            frames++
                        }
                        val nowQuiet = suppressionSupported && silenceGate.quiet
                        if (silenceQuiet != nowQuiet) { silenceQuiet = nowQuiet; sendStatus() }
                    }
                }
                offset += AudioProtocol.SAMPLES
                }
            }
        } catch (ex: Exception) { if (running) {
            Log.w("MicAudio", "CAPTURE_FAILED ${ex.javaClass.simpleName}")
            finishSession("麦克风或网络不可用，会话已结束", when (ex) {
                is java.io.IOException -> MicIssue.NETWORK
                is SecurityException -> MicIssue.PERMISSION
                else -> MicIssue.CAPTURE
            })
        } }
        finally { captureReady = false }
    }

    private fun send(type: String): Boolean = synchronized(frameGate) {
        val ws = socket ?: return@synchronized false
        if (ws.queueSize() > AudioProtocol.FRAME * 6) { ws.cancel(); return@synchronized false }
        ws.send(JSONObject().put("v", AudioProtocol.VERSION).put("type", type).put("generation", generation).toString())
    }
    private fun sendStatus() = synchronized(frameGate) {
        if (connected) socket?.send(JSONObject().put("v", AudioProtocol.VERSION).put("type", "status").put("generation", generation)
            .put("ready", captureReady && !paused && !ending.get())
            .put("quiet", forwarding && silenceQuiet).toString())
    }
    private fun publish() {
        if (ending.get()) return
        val state = synchronized(frameGate) { MicStatus(true, connected, forwarding, lastMessage,
            lifetime?.secondsLeft(SystemClock.elapsedRealtime()) ?: 0, frames, localOnly, capturedFrames,
            suppressionSupported, forwarding && silenceQuiet, (consideredFrames - frames).coerceAtLeast(0), paused, thresholdDb,
            readCalls = readCalls, captureMs = if (captureStarted > 0) SystemClock.elapsedRealtime() - captureStarted else 0, wakeHeld = wakeLock?.isHeld == true) }
        MicrophoneState.update(state)
        if (notificationText != state.message) {
            notificationText = state.message
            getSystemService(NotificationManager::class.java).notify(NOTIFICATION, notification(state.message))
        }
    }
    private fun notification(message: String): Notification {
        val open = PendingIntent.getActivity(this, 8902, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val end = PendingIntent.getService(this, 8903, Intent(this, MicrophoneService::class.java).setAction(END), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        return NotificationCompat.Builder(this, CHANNEL).setSmallIcon(android.R.drawable.ic_btn_speak_now)
            .setContentTitle("流转 · 手机麦克风").setContentText(message).setContentIntent(open).setOngoing(true)
            .setSilent(true).setOnlyAlertOnce(true).addAction(0, "结束会话", end).build()
    }
    private fun finishSession(message: String, issue: MicIssue = MicIssue.NONE) {
        if (!ending.compareAndSet(false, true)) return
        Log.i("MicAudio", "SESSION_END localOnly=$localOnly captured=$capturedFrames sent=$frames")
        MicrophoneLevels.reset()
        synchronized(frameGate) { forwarding = false; running = false; silenceGate.reset(); silenceQuiet = false; socket?.cancel() }
        if (ownsCapture) MicrophoneState.update(MicStatus(active = true, message = "正在释放麦克风…"))
        scope.launch {
            var released = false
            try {
                withContext(Dispatchers.IO) {
                    runCatching { recorder?.stop() }
                    reader?.join(1500)
                    if (reader?.isAlive != true) {
                        released = runCatching { recorder?.release(); recorder = null }.isSuccess
                    }
                }
            } finally {
                mutedReceiver?.let { runCatching { unregisterReceiver(it) } }
                recordingCallback?.let { runCatching { audioManager?.unregisterAudioRecordingCallback(it) } }
                runCatching { if (wakeLock?.isHeld == true) wakeLock?.release() }; wakeLock = null
                client?.connectionPool?.evictAll(); client?.dispatcher?.executorService?.shutdown()
                if (ownsCapture) {
                    if (released) occupied.set(false)
                    MicrophoneState.update(MicStatus(active = !released, message = if (released) message else "麦克风释放异常，请重启流转后重试",
                        issue = if (released) issue else MicIssue.CAPTURE, capturedFrames = capturedFrames, frames = frames, readCalls = readCalls,
                        captureMs = if (captureStarted > 0) SystemClock.elapsedRealtime() - captureStarted else 0, energySaving = energySaving))
                }
                withContext(Dispatchers.Main) { stopForeground(STOP_FOREGROUND_REMOVE); stopSelf() }
                scope.cancel(); dispatcher.close()
            }
        }
    }
    override fun onDestroy() { finishSession("麦克风会话已结束"); super.onDestroy() }
    override fun onBind(intent: Intent?): IBinder? = null
}
