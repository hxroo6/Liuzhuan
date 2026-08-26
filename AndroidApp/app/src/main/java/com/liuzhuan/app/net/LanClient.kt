package com.liuzhuan.app.net

import com.liuzhuan.app.core.SettingsStore
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import java.util.concurrent.TimeUnit

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
    private val onItemDeleted: (String) -> Unit = {},
    private val onItemCleared: () -> Unit = {},
    private val onItemData: (Proto.ItemData) -> Unit = {}
) {
    sealed class State {
        data object Idle : State()
        data object Connecting : State()
        data object Connected : State()
        data class AuthFailed(val reason: String) : State()
        data object Disconnected : State()
        data object Paused : State()
    }

    private var ws: WebSocket? = null
    private var heartbeatJob: Job? = null
    private var reconnectJob: Job? = null
    private var manuallyClosed = false
    private var lastSettings: SettingsStore.Settings? = null
    private val client = OkHttpClient.Builder()
        .pingInterval(30, TimeUnit.SECONDS)
        .connectTimeout(5, TimeUnit.SECONDS)
        .build()

    @Volatile
    var state: State = State.Idle
        private set

    fun connect(settings: SettingsStore.Settings) {
        lastSettings = settings
        if (settings.serverIp.isBlank() || settings.password.isBlank()) {
            log("请先填写电脑 IP 和口令")
            return
        }
        manuallyClosed = false
        reconnectJob?.cancel()
        // 关闭旧连接（防止多次 connect 叠加 WebSocket → 重复日志/握手）
        ws?.close(1000, "reconnect")
        ws = null
        stopHeartbeat()
        doConnect(settings)
    }

    fun disconnect() {
        manuallyClosed = true
        reconnectJob?.cancel()
        stopHeartbeat()
        ws?.close(1000, "bye")
        ws = null
        setState(State.Disconnected)
        log("已断开连接")
    }

    /** 暂停自动重连（阻止死循环；下次 connect() 可恢复） */
    fun pause() {
        manuallyClosed = true
        reconnectJob?.cancel()
        stopHeartbeat()
        ws?.close(1000, "paused")
        ws = null
        setState(State.Paused)
        log("⏸️ 已暂停重连（点「连接」可恢复）")
    }

    fun sendText(content: String) {
        if (state != State.Connected) {
            log("未连接，无法发送")
            return
        }
        ws?.send(Proto.buildSyncText(content))
        log("已发送文字（${content.length} 字）")
    }

    /** 推送剪贴板内容，返回是否已发送（供 Toast 提示） */
    fun pushClipboard(content: String, app: String): Boolean {
        if (state != State.Connected) return false
        ws?.send(Proto.buildClipboardPush(content, app))
        log("剪贴板已推送（${content.length} 字）")
        return true
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

    private fun doConnect(s: SettingsStore.Settings) {
        // 关闭残留 WebSocket（防重连时叠加）
        ws?.close(1000, "reconnect")
        ws = null
        setState(State.Connecting)
        val url = "ws://${s.serverIp}:${s.serverPort}/ws"
        val request = Request.Builder().url(url).build()
        log("正在连接 $url ...")
        ws = client.newWebSocket(request, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                log("TCP 已连通，发送握手...")
                webSocket.send(Proto.buildHello(Proto.sha256Hex(s.password)))
                startHeartbeat(webSocket)
            }

            override fun onMessage(webSocket: WebSocket, text: String) {
                val (type, data) = Proto.parse(text)
                when (type) {
                    "welcome" -> {
                        setState(State.Connected)
                        log("✅ 已连接电脑端流转")
                        log("[SYNC] request snapshot")
                        // 连接成功后拉取最近素材全量快照（接收页初始化）
                        webSocket.send(Proto.buildListSync())
                    }
                    "auth_fail" -> {
                        setState(State.AuthFailed(data.optString("reason", "口令错误")))
                        log("❌ 认证失败: ${data.optString("reason")}")
                        stopHeartbeat()
                        webSocket.close(1000, "auth_fail")
                    }
                    "heartbeat" -> { /* 心跳回执，无需处理 */ }
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
                        log("[SYNC] material deleted id=${id.take(8)} sequence=${Proto.parseSequence(data)}")
                        onItemDeleted(id)
                    }
                    "item_cleared" -> {
                        log("[SYNC] materials cleared")
                        onItemCleared()
                    }
                    "item_data" -> onItemData(Proto.parseItemData(data))
                }
            }

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                // 只处理「当前」WS 的回调（被替换的旧 WS 关闭不触发重连，防死循环）
                if (webSocket !== ws) return
                stopHeartbeat()
                ws = null
                if (!manuallyClosed) {
                    setState(State.Disconnected)
                    log("连接断开（code=$code reason=$reason），准备重连...")
                    scheduleReconnect()
                }
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                // 只处理「当前」WS 的回调（被替换的旧 WS 失败不触发重连，防死循环）
                if (webSocket !== ws) return
                stopHeartbeat()
                ws = null
                if (!manuallyClosed) {
                    setState(State.Disconnected)
                    val http = response?.let { " HTTP${it.code}" } ?: ""
                    log("连接失败${http}: ${t.javaClass.simpleName}: ${t.message ?: "未知错误"}")
                    scheduleReconnect()
                }
            }
        })
    }

    /** 心跳：每 30s 一次（M3 再做空闲升档到 5min） */
    private fun startHeartbeat(webSocket: WebSocket) {
        stopHeartbeat()
        heartbeatJob = scope.launch {
            while (true) {
                delay(30_000)
                if (state == State.Connected) {
                    webSocket.send(Proto.buildHeartbeat())
                }
            }
        }
    }

    private fun stopHeartbeat() {
        heartbeatJob?.cancel()
        heartbeatJob = null
    }

    /** 指数退避重连：1s→2s→4s→8s→…→上限 30s */
    private fun scheduleReconnect() {
        reconnectJob?.cancel()
        reconnectJob = scope.launch {
            var attempt = 0
            while (!manuallyClosed && state != State.Connected) {
                val waitMs = minOf(1000L shl attempt, 30_000L)
                log("等待 ${waitMs / 1000}s 后重连...")
                delay(waitMs)
                attempt++
                if (manuallyClosed) break
                if (state == State.Connected) break // 已连上就不重连
                log("第 $attempt 次重连...")
                val s = lastSettings ?: break
                doConnect(s)
                // 等连接结果再进入下轮（防 doConnect 后立即循环 → 重复创建 WS）
                delay(8000)
            }
        }
    }

    private fun setState(new: State) {
        state = new
        onState(new)
    }

    /** 统一日志：带时间戳 */
    private fun log(msg: String) {
        val ts = java.text.SimpleDateFormat("HH:mm:ss", java.util.Locale.US).format(java.util.Date())
        onLog("[$ts] $msg")
    }
}
