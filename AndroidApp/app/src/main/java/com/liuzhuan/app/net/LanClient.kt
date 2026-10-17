package com.liuzhuan.app.net

import com.liuzhuan.app.core.SettingsStore
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.CoroutineStart
import okhttp3.Call
import okhttp3.Callback
import okhttp3.MediaType.Companion.toMediaTypeOrNull
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.RequestBody.Companion.asRequestBody
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import java.io.IOException
import java.net.URLEncoder

/**
 * 局域网客户端 — 连接电脑端流转（M1：握手/心跳/发文字 + 接收页列表同步）
 * 状态回调 onState / onLog 供 UI 使用；onListData / onItemAdded / onItemData 供接收页
 */
class LanClient(
    private val scope: CoroutineScope = CoroutineScope(SupervisorJob() + Dispatchers.IO),
    private val onState: (State) -> Unit = {},
    private val onLog: (String) -> Unit = {},
    private val onListData: (List<Proto.ItemSummary>, Long) -> Unit = { _, _ -> },
    private val onItemAdded: (Proto.ItemSummary) -> Unit = {},
    private val onItemDeleted: (String, Long) -> Unit = { _, _ -> },
    private val onItemCleared: (Long) -> Unit = {},
    private val onItemData: (Proto.ItemData) -> Unit = {},
    private val onConnected: (SettingsStore.Settings) -> Unit = {}
) {
    sealed class State {
        data object Idle : State()
        data object Connecting : State()
        data object Connected : State()
        data class AuthFailed(val reason: String) : State()
        data object Disconnected : State()
        data object Paused : State()
    }

    @Volatile private var ws: WebSocket? = null
    private var reconnectJob: Job? = null
    @Volatile private var manuallyClosed = true
    @Volatile private var lastSettings: SettingsStore.Settings? = null
    private var networkAvailable = true
    private val backoff = RetryBackoff()
    private var reconnectAttempt = 0
    private var handshakeJob: Job? = null
    private val receiptJobs = java.util.concurrent.ConcurrentHashMap<String, Job>()
    @Volatile private var clipboardReceipts = false
    @Volatile private var cachedIdentity = ""
    @Volatile private var cachedAuth = ""
    private data class AudioLink(val socket: WebSocket, val host: String, val port: String, val supported: Boolean)
    private data class AudioRequest(val id: String, val socket: WebSocket, val result: kotlinx.coroutines.CompletableDeferred<String>)
    private val audioLink = kotlinx.coroutines.flow.MutableStateFlow<AudioLink?>(null)
    private var audioRequest: AudioRequest? = null
    private fun clearAudioLink() {
        audioLink.value = null
        audioRequest?.result?.completeExceptionally(IOException("电脑连接已变化，请重新连接麦克风")); audioRequest = null
    }

    /** An explicit foreground UI action requests one invitation over the authenticated material socket. */
    class MicrophoneInvitation internal constructor(val payload: String, private val current: () -> Boolean) {
        fun isCurrent() = current()
    }
    suspend fun requestMicrophoneInvitation(expectedHost: String, expectedPort: String): MicrophoneInvitation = kotlinx.coroutines.withTimeout(10_000) {
        val link = audioLink.first { it != null && it.host == expectedHost && it.port == expectedPort }!!
        if (!link.supported) throw IOException("电脑版本不支持一次扫码启用麦克风，请同时更新电脑端")
        val request = synchronized(this@LanClient) {
            if (ws !== link.socket || state != State.Connected) throw IOException("电脑连接已变化，请重试")
            if (audioRequest != null) throw IOException("正在连接麦克风，请稍候")
            AudioRequest(java.util.UUID.randomUUID().toString(), link.socket, kotlinx.coroutines.CompletableDeferred()).also {
                audioRequest = it
                if (!link.socket.send(Proto.buildMicrophoneInvite(it.id))) {
                    audioRequest = null; throw IOException("无法提交麦克风连接请求")
                }
            }
        }
        try {
            val payload = request.result.await()
            MicrophoneInvitation(payload) { synchronized(this@LanClient) { ws === link.socket && state == State.Connected && audioLink.value === link } }
        }
        finally { synchronized(this@LanClient) { if (audioRequest === request) audioRequest = null } }
    }
    private fun buildIdentity(s: SettingsStore.Settings): String =
        "${s.serverIp.trim().lowercase()}:${s.serverPort.toIntOrNull() ?: 8899}:${Proto.sha256Hex(s.password)}:${Proto.sha256Hex(s.deviceName)}:${Proto.sha256Hex(Proto.deviceLabel)}"
    private fun connectionIdentity(s: SettingsStore.Settings): String = if (s === lastSettings) cachedIdentity else buildIdentity(s)
    private val client = HttpClients.socket

    @Volatile
    var state: State = State.Idle
        private set

    @Synchronized fun connect(settings: SettingsStore.Settings) {
        if (settings.serverIp.isBlank() || settings.password.isBlank()) {
            log("请先填写电脑 IP 和口令")
            return
        }
        manuallyClosed = false
        clearAudioLink()
        reconnectJob?.cancel()
        reconnectJob = null
        handshakeJob?.cancel()
        backoff.reset()
        reconnectAttempt = 0
        // 关闭旧连接（防止多次 connect 叠加 WebSocket → 重复日志/握手）
        val previous = ws
        ws = null
        previous?.close(1000, "reconnect")
        clipboardReceipts = false
        cachedIdentity = buildIdentity(settings)
        cachedAuth = Proto.sha256Hex(settings.password)
        lastSettings = settings
        com.liuzhuan.app.clipboard.ClipboardMonitorCoordinator.delivery.changeTarget(connectionIdentity(settings))
        if (networkAvailable) doConnect(settings)
        else { setState(State.Disconnected); log("等待局域网恢复后连接") }
    }

    @Synchronized fun disconnect() {
        clearAudioLink()
        manuallyClosed = true
        reconnectJob?.cancel()
        reconnectJob = null
        handshakeJob?.cancel()
        val previous = ws
        ws = null
        previous?.cancel()
        // 用户主动断开 → Paused（不重连），与「意外断开 Disconnected（自动重连中）」区分。
        // 否则 UI 层会把主动断开也当成 Disconnected，进而误停前台服务/保活锁。
        setState(State.Paused)
        log("已断开连接")
    }

    /** 暂停自动重连（阻止死循环；下次 connect() 可恢复） */
    @Synchronized fun pause() {
        clearAudioLink()
        manuallyClosed = true
        reconnectJob?.cancel()
        reconnectJob = null
        handshakeJob?.cancel()
        val previous = ws
        ws = null
        previous?.cancel()
        setState(State.Paused)
        log("⏸️ 已暂停重连（点「连接」可恢复）")
    }

    fun sendText(content: String, taskId:String=java.util.UUID.randomUUID().toString()): Boolean {
        com.liuzhuan.app.PowerSignals.touch()
        com.liuzhuan.app.TransferTasks.start(content.take(40),"手机 → 电脑",taskId) {
            com.liuzhuan.app.LanHub.client?.sendText(content,taskId)
                ?: com.liuzhuan.app.TransferTasks.finish(taskId,false,"请先连接电脑，再重试")
        }
        if (state != State.Connected) {
            log("未连接，无法发送")
            com.liuzhuan.app.TransferTasks.finish(taskId,false,"未连接电脑，请连接后重试")
            return false
        }
        val accepted = ws?.send(Proto.buildSyncText(content,requestId=taskId)) == true
        if(!accepted) com.liuzhuan.app.TransferTasks.finish(taskId,false,"发送未成功，请检查连接")
        else {
            receiptJobs.remove(taskId)?.cancel()
            val job = scope.launch(start = CoroutineStart.LAZY) {
            delay(20_000)
            if(com.liuzhuan.app.TransferTasks.running(taskId)) com.liuzhuan.app.TransferTasks.finish(taskId,false,"未收到电脑确认；请先检查电脑再重试，避免重复。旧版电脑需升级。")
            receiptJobs.remove(taskId)
            }
            receiptJobs[taskId] = job
            job.start()
            if (!com.liuzhuan.app.TransferTasks.running(taskId)) receiptJobs.remove(taskId)?.cancel()
        }
        log(if (accepted) "已提交文字（${content.length} 字）" else "发送未成功，请检查连接")
        return accepted
    }

    /** 显式选中文本兼容入口：只返回网络是否接受发送，不能表示电脑已登记。 */
    fun pushClipboard(content: String, app: String): Boolean {
        if (state != State.Connected) return false
        com.liuzhuan.app.PowerSignals.touch()
        val accepted = ws?.send(Proto.buildClipboardPush(content, app)) == true
        log(if (accepted) "剪贴板文字已提交，等待电脑确认（${content.length} 字）" else "剪贴板文字未提交")
        return accepted
    }

    internal fun automaticClipboardTransport(): com.liuzhuan.app.clipboard.AutomaticClipboardDelivery.Transport? {
        if (state != State.Connected) return null
        val socket = ws ?: return null
        val settings = lastSettings ?: return null
        val identity = connectionIdentity(settings)
        val supports = clipboardReceipts
        return object : com.liuzhuan.app.clipboard.AutomaticClipboardDelivery.Transport {
            override val identity = identity
            override val supportsReceipts = supports
            override fun send(event: com.liuzhuan.app.clipboard.ClipboardEvent): Boolean {
                if (state != State.Connected || ws !== socket || lastSettings?.let(::connectionIdentity) != identity) return false
                com.liuzhuan.app.PowerSignals.touch()
                return socket.send(Proto.buildClipboardPush(event.text ?: return false, event.sourcePackage ?: "android",
                    event.requestId, automatic = true))
            }
        }
    }

    /** 上传文件/图片到电脑（HTTP POST 到 /upload 端点，复用 PC 端 FileHttpServer） */
    fun pushFile(bytes: ByteArray, name: String, mime: String, taskId:String=java.util.UUID.randomUUID().toString(),
                 automaticEvent: com.liuzhuan.app.clipboard.ClipboardEvent? = null) {
        if (automaticEvent == null) pushFileAccepted(bytes, name, mime, taskId, null)
        else if (!com.liuzhuan.app.clipboard.ClipboardPrivacy.runIfAllowed(automaticEvent) {
                pushFileAccepted(bytes, name, mime, taskId, automaticEvent)
            }) {
            com.liuzhuan.app.TransferTasks.finish(taskId, false, "隐私规则已拦截这次自动复制，请重新复制或手动选择文件")
        }
    }

    /** Hook 文件以本应用缓存为重试来源，不在失败记录里长期保留 ByteArray。 */
    fun pushFileCached(file: java.io.File, name: String, mime: String,
        taskId: String = java.util.UUID.randomUUID().toString(),
        automaticEvent: com.liuzhuan.app.clipboard.ClipboardEvent) {
        if (com.liuzhuan.app.TransferTasks.running(taskId)) return
        com.liuzhuan.app.TransferTasks.start(name, "手机 → 电脑", taskId,
            discard = { file.delete() }, cancel = { com.liuzhuan.app.TransferWork.cancel(taskId) }, retry = {
                com.liuzhuan.app.LanHub.client?.pushFileCached(file, name, mime, taskId, automaticEvent)
                    ?: com.liuzhuan.app.TransferTasks.finish(taskId, false, "请先连接电脑")
            })
        com.liuzhuan.app.TransferWork.launch(taskId) {
            try {
                if (!file.isFile) throw IOException("原复制文件已过期，请重新复制")
                val s = lastSettings ?: throw IOException("请先连接电脑")
                val url = "http://${s.serverIp}:${(s.serverPort.toIntOrNull() ?: 8899)+1}/upload?name=${URLEncoder.encode(name,"UTF-8")}&auth=${Proto.sha256Hex(s.password)}"
                val request = Request.Builder().url(url).post(file.asRequestBody(mime.toMediaTypeOrNull())).build()
                com.liuzhuan.app.TransferWork.withCall(taskId, HttpClients.transfer.newCall(request)) { call ->
                    executeAllowed(call, automaticEvent).use { response ->
                        com.liuzhuan.app.TransferTasks.finish(taskId, response.isSuccessful,
                            if (response.isSuccessful) "电脑已接收" else "HTTP ${response.code}")
                    }
                }
            } catch (ex: Exception) {
                if (ex is kotlinx.coroutines.CancellationException) throw ex
                com.liuzhuan.app.TransferTasks.finish(taskId, false, "${ex.message}；重试前请检查电脑是否已收到")
            }
        }
    }

    private fun pushFileAccepted(bytes: ByteArray, name: String, mime: String, taskId: String,
                                 automaticEvent: com.liuzhuan.app.clipboard.ClipboardEvent?) {
        com.liuzhuan.app.TransferTasks.start(name,"手机 → 电脑",taskId,cancel={com.liuzhuan.app.TransferWork.cancel(taskId)},retry={
            com.liuzhuan.app.LanHub.client?.pushFile(bytes,name,mime,taskId,automaticEvent)
                ?: com.liuzhuan.app.TransferTasks.finish(taskId,false,"请先连接电脑，再重试")
        })
        val s = lastSettings ?: run {
            log("未连接，无法上传文件")
            com.liuzhuan.app.TransferTasks.finish(taskId,false,"请先连接电脑，再重试")
            return
        }
        val filePort = (s.serverPort.toIntOrNull() ?: 8899) + 1
        val auth = Proto.sha256Hex(s.password)
        val encodedName = URLEncoder.encode(name, "UTF-8")
        val url = "http://${s.serverIp}:$filePort/upload?name=$encodedName&auth=$auth"
        val body = bytes.toRequestBody(mime.toMediaTypeOrNull())
        val req = Request.Builder().url(url).post(body).build()
        com.liuzhuan.app.TransferTasks.progress(taskId,0,bytes.size.toLong())
        com.liuzhuan.app.TransferWork.launch(taskId) {
            try {
                if (automaticEvent != null && com.liuzhuan.app.clipboard.ClipboardPrivacy.blockReason(automaticEvent) != null)
                    throw IOException("隐私规则已更新，请重新复制")
                com.liuzhuan.app.TransferWork.withCall(taskId, HttpClients.transfer.newCall(req)) { call ->
                executeAllowed(call, automaticEvent).use { response ->
                    com.liuzhuan.app.TransferTasks.progress(taskId,bytes.size.toLong(),bytes.size.toLong())
                    com.liuzhuan.app.TransferTasks.finish(taskId,response.isSuccessful,if(response.isSuccessful) "电脑已接收" else "HTTP ${response.code}")
                    log(if (response.isSuccessful) "文件上传成功（${bytes.size} 字节）" else "文件上传失败 HTTP ${response.code}")
                }
                }
            } catch (ex: Exception) {
                if (ex is kotlinx.coroutines.CancellationException) throw ex
                com.liuzhuan.app.TransferTasks.finish(taskId,false,"${ex.message}；重试前请检查电脑是否已收到")
            }
        }
    }

    /** 隐私判断与真正网络入队原子化；排队等待传输槽期间的旧事件也重查。 */
    private fun executeAllowed(call: Call, event: com.liuzhuan.app.clipboard.ClipboardEvent?): Response {
        if (event == null) return call.execute()
        val result = java.util.concurrent.CompletableFuture<Response>()
        val accepted = com.liuzhuan.app.clipboard.ClipboardPrivacy.runIfAllowed(event) {
            call.enqueue(object : Callback {
                override fun onFailure(call: Call, e: IOException) { result.completeExceptionally(e) }
                override fun onResponse(call: Call, response: Response) {
                    if (call.isCanceled()) { response.close(); result.completeExceptionally(IOException("已取消")) }
                    else result.complete(response)
                }
            })
        }
        if (!accepted) throw IOException("隐私规则已更新，请重新复制")
        return try { result.get() } catch (ex: java.util.concurrent.ExecutionException) { throw IOException(ex.cause?.message ?: "上传失败", ex.cause) }
    }

    fun thumbnailUrl(id:String):String? {
        val s=lastSettings ?: return null
        if(state!=State.Connected) return null
        val port=(s.serverPort.toIntOrNull() ?: 8899)+1
        return "http://${s.serverIp}:$port/thumbnail/${URLEncoder.encode(id,"UTF-8")}?auth=$cachedAuth"
    }

    /** 请求素材详情（文字全文 / 文件下载地址） */
    fun requestItem(id: String) {
        if (state != State.Connected) return
        ws?.send(Proto.buildGetItem(id))
    }

    /** 请求重新全量快照（sequence gap 检测触发 resync 时调用） */
    fun requestSnapshot() {
        if (state != State.Connected) return
        log("[SYNC] resync requested")
        ws?.send(Proto.buildListSync())
    }

    @Synchronized private fun doConnect(s: SettingsStore.Settings) {
        if (manuallyClosed || !networkAvailable) return
        clearAudioLink()
        handshakeJob?.cancel()
        clipboardReceipts = false
        // 关闭残留 WebSocket（防重连时叠加）
        ws?.cancel()
        ws = null
        setState(State.Connecting)
        val url = "ws://${s.serverIp}:${s.serverPort}/ws"
        val request = Request.Builder().url(url).build()
        log("正在连接 $url ...")
        ws = client.newWebSocket(request, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                synchronized(this@LanClient) {
                if (webSocket !== ws || manuallyClosed) { webSocket.cancel(); return }
                log("TCP 已连通，发送握手...")
                webSocket.send(Proto.buildHello(Proto.sha256Hex(s.password)))
                }
            }

            override fun onMessage(webSocket: WebSocket, text: String) {
                synchronized(this@LanClient) {
                if(webSocket !== ws) return
                val (type, data) = Proto.parse(text)
                when (type) {
                    "ack" -> {
                        com.liuzhuan.app.clipboard.ClipboardMonitorCoordinator.delivery.acknowledge(
                            connectionIdentity(s), data.optString("requestId"), data.optString("status"))
                        val requestId = data.optString("requestId")
                        receiptJobs.remove(requestId)?.cancel()
                        when (data.optString("status")) {
                            "stored" -> com.liuzhuan.app.TransferTasks.finish(requestId, true, "电脑已接收")
                            "rejected" -> com.liuzhuan.app.TransferTasks.finish(requestId, false, "电脑拒绝登记，请检查电脑后重新发送")
                        }
                    }
                    "welcome" -> {
                        handshakeJob?.cancel()
                        backoff.reset()
                        reconnectAttempt = 0
                        reconnectJob?.cancel()
                        reconnectJob = null
                        val features = data.optJSONArray("features")
                        clipboardReceipts = features != null && (0 until features.length()).any {
                            features.optString(it) == Proto.CLIPBOARD_RECEIPTS
                        }
                        setState(State.Connected)
                        audioLink.value = AudioLink(webSocket, s.serverIp, s.serverPort, features != null &&
                            (0 until features.length()).any { features.optString(it) == Proto.MICROPHONE_PAIRING })
                        onConnected(s)
                        log("✅ 已连接电脑端流转")
                        log("[SYNC] request snapshot")
                        // 连接成功后拉取最近素材全量快照（接收页初始化）
                        webSocket.send(Proto.buildListSync())
                    }
                    "auth_fail" -> {
                        clearAudioLink()
                        manuallyClosed = true
                        handshakeJob?.cancel()
                        reconnectJob?.cancel()
                        reconnectJob = null
                        setState(State.AuthFailed(data.optString("reason", "口令错误")))
                        log("❌ 认证失败: ${data.optString("reason")}")
                        webSocket.close(1000, "auth_fail")
                    }
                    "heartbeat" -> { /* 心跳回执，无需处理 */ }
                    Proto.MICROPHONE_INVITE_RESULT -> {
                        val pending = audioRequest
                        if (pending != null && pending.socket === webSocket && pending.id == data.optString("requestId")) {
                            if (data.optString("status") == "ready") {
                                try {
                                    val payload = org.json.JSONObject().put("kind", data.getString("kind")).put("v", data.getInt("v"))
                                        .put("ip", s.serverIp).put("port", data.getInt("port")).put("token", data.getString("token")).toString()
                                    pending.result.complete(com.liuzhuan.app.audio.AudioPairing.microphone(payload)
                                        ?: throw IOException("电脑没有提供有效的麦克风邀请"))
                                } catch (_: Exception) { pending.result.completeExceptionally(IOException("麦克风邀请无效，请同时更新两端")) }
                            } else pending.result.completeExceptionally(IOException(when (data.optString("status")) {
                                "setup_required" -> "素材已连接，请先在电脑设置里选择虚拟音频设备；之后点左上音波图连接即可，无需重扫"
                                "busy" -> "已有麦克风会话，请先结束原会话后重试"
                                "diagnostic_only" -> "电脑当前仅诊断收音，请在电脑关闭诊断模式后连接"
                                "retry_later" -> "请求太快，请稍候两秒再试"
                                else -> "素材已连接，电脑音频服务暂不可用，请检查虚拟设备和端口"
                            }))
                        }
                    }
                    "list_data" -> {
                        val seq = Proto.parseSequence(data)
                        val items = Proto.parseListData(data)
                        log("[SYNC] snapshot received count=${items.size} sequence=$seq")
                        onListData(items, seq)
                    }
                    "item_added" -> {
                        val item = Proto.parseItemAdded(data)
                        log("[SYNC] material added id=${item.id.take(8)} sequence=${item.sequence}")
                        onItemAdded(item)
                    }
                    "item_deleted" -> {
                        val id = Proto.parseItemDeletedId(data)
                        val sequence = Proto.parseSequence(data)
                        log("[SYNC] material deleted id=${id.take(8)} sequence=$sequence")
                        onItemDeleted(id, sequence)
                    }
                    "item_cleared" -> {
                        val sequence = Proto.parseSequence(data)
                        log("[SYNC] materials cleared sequence=$sequence")
                        onItemCleared(sequence)
                    }
                    "item_data" -> onItemData(Proto.parseItemData(data))
                }
                }
            }

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                // 只处理「当前」WS 的回调（被替换的旧 WS 关闭不触发重连，防死循环）
                connectionFailed(webSocket, "连接断开，准备重连")
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                // 只处理「当前」WS 的回调（被替换的旧 WS 失败不触发重连，防死循环）
                connectionFailed(webSocket, "连接失败：${t.javaClass.simpleName}，将自动重试")
            }
        })
        val socket = ws
        handshakeJob = scope.launch {
            delay(12_000)
            synchronized(this@LanClient) {
                if (socket != null && ws === socket && state == State.Connecting) {
                    socket.cancel()
                    connectionFailed(socket, "握手超时，将自动重试")
                }
            }
        }
    }

    /** 指数退避重连：1s→2s→4s→8s→…→上限 30s */
    @Synchronized private fun scheduleReconnect(immediate: Boolean = false) {
        if (reconnectJob?.isActive == true || manuallyClosed || !networkAvailable || state == State.Connected) return
        val job = scope.launch(start = CoroutineStart.LAZY) {
                val waitMs = if (immediate) 0L else backoff.nextDelayMs().let { base ->
                    (base + kotlin.random.Random.nextLong(0, minOf(base / 5 + 1, 1_001))).coerceAtMost(30_000)
                }
                log("等待 ${waitMs / 1000}s 后重连...")
                delay(waitMs)
                synchronized(this@LanClient) {
                    reconnectJob = null
                    if (manuallyClosed || !networkAvailable || state == State.Connected) return@synchronized
                    val s = lastSettings ?: return@synchronized
                    reconnectAttempt = (reconnectAttempt + 1).coerceAtMost(Int.MAX_VALUE - 1)
                    log("第 $reconnectAttempt 次重连...")
                    doConnect(s)
                }
        }
        reconnectJob = job
        job.start()
    }

    @Synchronized private fun connectionFailed(socket: WebSocket, message: String) {
        if (socket !== ws) return
        clearAudioLink()
        ws = null
        handshakeJob?.cancel()
        if (!manuallyClosed) { setState(State.Disconnected); log(message); scheduleReconnect() }
    }

    @Synchronized fun setNetworkAvailable(available: Boolean) {
        if (networkAvailable == available) return
        networkAvailable = available
        if (manuallyClosed || lastSettings == null) return
        if (!available) {
            reconnectJob?.cancel(); reconnectJob = null
            handshakeJob?.cancel()
            val previous = ws; ws = null; previous?.cancel()
            setState(State.Disconnected)
            log("暂无可用局域网，等待网络恢复")
        } else if (state != State.Connected) scheduleReconnect(immediate = true)
    }

    private fun setState(new: State) {
        state = new
        onState(new)
        com.liuzhuan.app.clipboard.ClipboardMonitorCoordinator.delivery.wake()
    }

    /** 统一日志：带时间戳 */
    private fun log(msg: String) {
        val ts = java.text.SimpleDateFormat("HH:mm:ss", java.util.Locale.US).format(java.util.Date())
        onLog("[$ts] $msg")
    }
}
