package com.liuzhuan.app

import android.content.Context
import android.content.Intent
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.TetheringInterface
import android.net.TetheringManager
import android.os.Handler
import android.os.Looper
import androidx.core.content.ContextCompat
import com.liuzhuan.app.core.SettingsStore
import com.liuzhuan.app.core.LanRouteState
import com.liuzhuan.app.data.MaterialRepository
import com.liuzhuan.app.data.toMaterialItem
import com.liuzhuan.app.net.LanClient
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

/** 连接和回调归进程所有，Activity 重建只重新订阅。 */
object LanController {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private val mutableState = MutableStateFlow<LanClient.State>(LanClient.State.Idle)
    val state = mutableState.asStateFlow()
    private val mutableLogs = MutableStateFlow<List<String>>(emptyList())
    val logs = mutableLogs.asStateFlow()
    private val mutableBalanced = MutableStateFlow(false)
    val balanced = mutableBalanced.asStateFlow()
    private var initialized = false

    @Synchronized fun init(context: Context): LanClient {
        LanHub.client?.let { if (initialized) return it }
        val app = context.applicationContext
        // 启动前快照只包含旧进程缓存，异步清理不会删除随后新建的任务文件。
        val oldCache = app.cacheDir.listFiles()?.filter {
            it.isFile && (it.name.startsWith("lz_clip_") || it.name.startsWith("lz_upload_"))
        }.orEmpty()
        scope.launch(Dispatchers.IO) { oldCache.forEach { runCatching { it.delete() } } }
        val main = Handler(Looper.getMainLooper())
        fun ui(block: () -> Unit) { if (Looper.myLooper() == Looper.getMainLooper()) block() else main.post(block) }
        val client = LanClient(
            onState = { next ->
                mutableState.value = next
                if (next == LanClient.State.Connected) {
                    // 在 WS 请求快照之前建立同步边界，不能等 UI Handler 再重置。
                    MaterialRepository.markSyncing()
                    com.liuzhuan.app.clipboard.ClipboardMonitorCoordinator.onWsConnected()
                }
                ui {
                if (mutableState.value != next) return@ui
                if (next == LanClient.State.Connected) {
                    try { ContextCompat.startForegroundService(app, Intent(app, ForegroundService::class.java)) }
                    catch (_: Exception) { appendLog("后台连接服务未能启动，请回到流转恢复连接") }
                } else if (next is LanClient.State.AuthFailed || next == LanClient.State.Paused) {
                    app.stopService(Intent(app, ForegroundService::class.java))
                }
            } },
            onLog = { text -> ui { appendLog(text) } },
            onListData = { items, seq -> MaterialRepository.replaceAll(items.map { it.toMaterialItem() }, seq) },
            onItemAdded = { MaterialRepository.add(it.toMaterialItem()) },
            onItemDeleted = { id, seq -> MaterialRepository.delete(id, seq) },
            onItemCleared = { MaterialRepository.clear(it) },
            onItemData = { data -> ui {
                if (data.error.isNotEmpty()) toast(app, "获取素材失败")
                else if (data.content.isNotEmpty()) { copyToClipboard(app, data.content); toast(app, "已复制到剪贴板") }
                else if (data.downloadUrl.isNotEmpty()) receiveFile(app, data)
            } }
        )
        LanHub.client = client
        initialized = true
        MaterialRepository.onSequenceGap = { client.requestSnapshot() }
        app.registerComponentCallbacks(object : android.content.ComponentCallbacks2 {
            override fun onConfigurationChanged(configuration: android.content.res.Configuration) {}
            override fun onLowMemory() { clearThumbnailCache() }
            override fun onTrimMemory(level: Int) {
                if (level == android.content.ComponentCallbacks2.TRIM_MEMORY_RUNNING_LOW ||
                    level == android.content.ComponentCallbacks2.TRIM_MEMORY_RUNNING_CRITICAL ||
                    level >= android.content.ComponentCallbacks2.TRIM_MEMORY_BACKGROUND) clearThumbnailCache()
            }
        })
        scope.launch {
            SettingsStore(app).settings.collect { settings ->
                mutableBalanced.value = settings.balancedPower
            }
        }
        val manager = app.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
        val routes = LanRouteState<Network> { client.setNetworkAvailable(it) }
        fun isLan(caps: NetworkCapabilities) =
            caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) ||
                caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET) ||
                caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN)
        try {
            // 初始查询一次；后续使用回调的能力数据，避免 onLost 后查询到旧网络。
            manager.allNetworks.forEach { network -> manager.getNetworkCapabilities(network)?.let { routes.updateNetwork(network,isLan(it)) } }
            manager.registerNetworkCallback(NetworkRequest.Builder().clearCapabilities().build(),
                object : ConnectivityManager.NetworkCallback() {
                    override fun onLost(network: Network) { routes.removeNetwork(network) }
                    override fun onCapabilitiesChanged(network: Network, caps: NetworkCapabilities) {
                        routes.updateNetwork(network,isLan(caps))
                    }
                })
            routes.publish()
        } catch (_: Exception) { appendLog("网络状态监听不可用，将使用有上限的自动重连") }
        try {
            // Android 16 公共 API，注册后立即回调已有热点；热点不是 TRANSPORT_WIFI 网络。
            app.getSystemService(TetheringManager::class.java).registerTetheringEventCallback(
                ContextCompat.getMainExecutor(app), object : TetheringManager.TetheringEventCallback {
                    override fun onTetheredInterfacesChanged(interfaces: Set<TetheringInterface>) {
                        routes.updateWifiHotspot(interfaces.any { it.type == TetheringManager.TETHERING_WIFI })
                    }
                })
        } catch (_: Exception) { appendLog("热点状态监听不可用，请使用普通 Wi-Fi 连接并查看系统权限") }
        return client
    }

    @Synchronized fun appendLog(text: String) {
        mutableLogs.value = (mutableLogs.value + text).takeLast(50)
    }
}
