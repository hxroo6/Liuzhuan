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
    private val onListData: (List<Proto.ItemSummary>) -> Unit = {},
    private val onItemAdded: (Proto.ItemSummary) -> Unit = {},
    private val onItemData: (Proto.ItemData) -> Unit = {}
) {
    sealed class State {
        data object Idle : State()
        data object Connecting : State()
        data object Connected : State()
        data class AuthFailed(val reason: String) : State()
        data object Disconnected : State()
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
            onLog("请先填写电脑 IP 和口令")
            return
        }
        manuallyClosed = false
        reconnectJob?.cancel()
        doConnect(settings)
    }

    fun disconnect() {
        manuallyClosed = true
        reconnectJob?.cancel()
        stopHeartbeat()
        ws?.close(1000, "bye")
        ws = null
        setState(State.Disconnected)
        onLog("已断开连接")
    }

    fun sendText(content: String) {
        if (state != State.Connected) {
            onLog("未连接，无法发送")
            return
        }
        ws?.send(Proto.buildSyncText(content))
        onLog("已发送文字（${content.length} 字）")
    }

    /** 推送剪贴板内容，返回是否已发送（供 Toast 提示） */
    fun pushClipboard(content: String, app: String): Boolean {
        if (state != State.Connected) return false
        ws?.send(Proto.buildClipboardPush(content, app))
        onLog("剪贴板已推送（${content.length} 字）")
        return true
    }

    /** 请求素材详情（文字全文 / 文件下载地址） */
    fun requestItem(id: String) {
        if (state != State.Connected) return
        ws?.send(Proto.buildGetItem(id))
    }

    private fun doConnect(s: SettingsStore.Settings) {
        setState(State.Connecting)
        val url = "ws://${s.serverIp}:${s.serverPort}/ws"
        val request = Request.Builder().url(url).build()
        onLog("正在连接 $url ...")
        ws = client.newWebSocket(request, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                // 握手消息
                webSocket.send(Proto.buildHello(Proto.sha256Hex(s.password)))
                startHeartbeat(webSocket)
            }

            override fun onMessage(webSocket: WebSocket, text: String) {
                val (type, data) = Proto.parse(text)
                when (type) {
                    "welcome" -> {
                        setState(State.Connected)
                        onLog("✅ 已连接电脑端流转")
                        // 连接成功后拉取最近素材列表（接收页初始化）
                        webSocket.send(Proto.buildListSync())
                    }
                    "auth_fail" -> {
                        setState(State.AuthFailed(data.optString("reason", "口令错误")))
                        onLog("❌ 认证失败: ${data.optString("reason")}")
                        stopHeartbeat()
                        webSocket.close(1000, "auth_fail")
                    }
                    "heartbeat" -> { /* 心跳回执，无需处理 */ }
                    "list_data" -> onListData(Proto.parseListData(data))
                    "item_added" -> onItemAdded(Proto.parseItemAdded(data))
                    "item_data" -> onItemData(Proto.parseItemData(data))
                }
            }

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                stopHeartbeat()
                ws = null
                if (!manuallyClosed) {
                    setState(State.Disconnected)
                    onLog("连接断开（$code），准备重连...")
                    scheduleReconnect()
                }
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                stopHeartbeat()
                ws = null
                if (!manuallyClosed) {
                    setState(State.Disconnected)
                    onLog("连接失败: ${t.message ?: "未知错误"}")
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
                delay(minOf(1000L shl attempt, 30_000L))
                attempt++
                if (manuallyClosed) break
                onLog("第 $attempt 次重连...")
                val s = lastSettings ?: break
                doConnect(s)
            }
        }
    }

    private fun setState(new: State) {
        state = new
        onState(new)
    }
}
