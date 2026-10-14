package com.liuzhuan.app

import android.content.ClipData
import android.content.ClipboardManager
import android.content.ContentValues
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.Bundle
import android.provider.MediaStore
import android.provider.Settings
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.horizontalScroll
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.liuzhuan.app.core.SettingsStore
import com.liuzhuan.app.net.LanClient
import com.liuzhuan.app.net.LanDiscovery
import com.liuzhuan.app.net.Proto
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext
import okhttp3.MediaType.Companion.toMediaTypeOrNull
import okhttp3.Request

class MainActivity : ComponentActivity() {

    private lateinit var store: SettingsStore

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        onBackPressedDispatcher.addCallback(this, object : androidx.activity.OnBackPressedCallback(true) {
            override fun handleOnBackPressed() { moveTaskToBack(true) }
        })
        store = SettingsStore(this)
        Proto.deviceLabel = Build.MODEL
        // 幂等装配剪贴板监控组件（无障碍服务未启动时也要可用，供前台捕获/测试）
        com.liuzhuan.app.clipboard.ClipboardMonitorCoordinator.init(applicationContext)

        // Android 13+ 通知权限（前台服务通知需要）
        if (Build.VERSION.SDK_INT >= 33) {
            requestPermissions(arrayOf(android.Manifest.permission.POST_NOTIFICATIONS), 1)
        }

        val client = LanController.init(applicationContext)

        setContent {
            FlowTheme {
                val connection by LanController.state.collectAsStateWithLifecycle()
                MainScreen(
                    store = store,
                    client = client,
                    stateText = when (val state = connection) {
                        LanClient.State.Idle -> "未连接"
                        LanClient.State.Connecting -> "连接中…"
                        LanClient.State.Connected -> "已连接 ✓"
                        is LanClient.State.AuthFailed -> "认证失败：${state.reason}"
                        LanClient.State.Disconnected -> "已断开（自动重连中…）"
                        LanClient.State.Paused -> "已暂停重连"
                    },
                    isConnected = connection == LanClient.State.Connected,
                    isReconnecting = connection == LanClient.State.Disconnected || connection == LanClient.State.Connecting
                )
            }
        }
    }

    /** 分享菜单入口：任意 App 分享文字/文件 → 流转 */
    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        if (intent.action == Intent.ACTION_SEND) {
            handleShareIntent(intent)
        }
    }

    /**
     * 窗口焦点建立 → 立即重读剪贴板（「切回流转秒发」的关键时机）。
     *
     * 为什么不在 onResume 里做（已有）：ColorOS 实测（M15 用户测试 + 图四诊断）
     * onResume 时窗口焦点尚未建立，系统焦点检查仍拒绝剪贴板读取，
     * 前台重读失败，要等界面事件（如截屏）触发的兜底才能读到——表现为
     * 「切回流转好一会才发送」。onWindowFocusChanged(true) 是焦点确切就绪的信号。
     */
    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) {
            try {
                com.liuzhuan.app.clipboard.ClipboardMonitorCoordinator
                    .captureManager.captureOnForeground()
            } catch (_: Exception) {
            }
        }
    }

    private fun handleShareIntent(intent: Intent) {
        val text = intent.getStringExtra(Intent.EXTRA_TEXT)
        val stream = if (android.os.Build.VERSION.SDK_INT >= 33)
            intent.getParcelableExtra(Intent.EXTRA_STREAM, android.net.Uri::class.java)
        else
            @Suppress("DEPRECATION") intent.getParcelableExtra(Intent.EXTRA_STREAM)

        val client = LanHub.client ?: return
        if (client.state !is LanClient.State.Connected) {
            toast(this, "❌ 请先连接电脑再分享")
            return
        }
        when {
            stream != null -> uploadSharedFile(stream)
            !text.isNullOrBlank() -> {
                client.sendText(text.trim())
                toast(this, "📤 已发送到电脑")
            }
        }
    }

    /** 分享的文件 → HTTP POST 流式上传到电脑（大文件不整载内存，见 uploadFileStreaming） */
    private fun uploadSharedFile(uri: android.net.Uri) {
        val app = applicationContext
        toast(app, "⬆️ 正在上传...")
        uploadFileStreaming(
            context = app,
            uri = uri,
            store = store,
            onProgress = { _, _ -> /* 分享路径无进度 UI，完成/失败以 Toast 提示 */ },
            onResult = { ok, msg ->
                if (ok) toast(app, "✅ 已上传到电脑")
                else toast(app, "❌ 上传失败: $msg")
            }
        )
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun MainScreen(
    store: SettingsStore,
    client: LanClient,
    stateText: String,
    isConnected: Boolean,
    isReconnecting: Boolean
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val focusManager = LocalFocusManager.current
    val keyboard = LocalSoftwareKeyboardController.current

    var selectedTab by rememberSaveable { mutableStateOf(0) }
    var showLogs by rememberSaveable { mutableStateOf(false) }
    // 不可见面板不订阅；后台业务仍在进程中继续。
    val materials = if (selectedTab == 2) com.liuzhuan.app.data.MaterialRepository.materials.collectAsStateWithLifecycle().value else emptyList()
    val syncState = if (selectedTab == 2) com.liuzhuan.app.data.MaterialRepository.syncState.collectAsStateWithLifecycle().value else com.liuzhuan.app.data.MaterialRepository.SyncState.IDLE
    val clipDiagnostic = if (selectedTab == 0) com.liuzhuan.app.clipboard.ClipboardMonitorCoordinator.diagnostic.collectAsStateWithLifecycle().value else ""
    val diagnosticHistory = if (selectedTab == 0 && showLogs) com.liuzhuan.app.clipboard.ClipboardMonitorCoordinator.diagnosticHistory.collectAsStateWithLifecycle().value else emptyList()
    val logLines = if (selectedTab == 0 && showLogs) LanController.logs.collectAsStateWithLifecycle().value else emptyList()
    val diagnosticOwner = androidx.lifecycle.compose.LocalLifecycleOwner.current
    DisposableEffect(diagnosticOwner, selectedTab, showLogs) {
        fun updateTrace() {
            com.liuzhuan.app.clipboard.CaptureDiagnostics.verbose = selectedTab == 0 && showLogs &&
                diagnosticOwner.lifecycle.currentState.isAtLeast(androidx.lifecycle.Lifecycle.State.STARTED)
        }
        val observer = androidx.lifecycle.LifecycleEventObserver { _, _ -> updateTrace() }
        diagnosticOwner.lifecycle.addObserver(observer)
        updateTrace()
        onDispose {
            diagnosticOwner.lifecycle.removeObserver(observer)
            com.liuzhuan.app.clipboard.CaptureDiagnostics.verbose = false
        }
    }

    // 自动发现状态
    var searching by remember { mutableStateOf(false) }
    var discoveryError by remember { mutableStateOf("") }
    val discoveredServers = remember { mutableStateListOf<LanDiscovery.Server>() }

    var ip by remember { mutableStateOf("") }
    var port by remember { mutableStateOf("8899") }
    var password by remember { mutableStateOf("") }
    var textInput by rememberSaveable { mutableStateOf("") }
    var showTransfers by remember { mutableStateOf(false) }
    var showHistory by rememberSaveable { mutableStateOf(false) }
    val historyStore = remember { com.liuzhuan.app.core.ConnectionHistoryStore(context) }
    val history by historyStore.state.collectAsStateWithLifecycle(com.liuzhuan.app.core.ConnectionHistoryStore.State())
    var recallingHistory by remember { mutableStateOf(false) }
    val runningTransfers by remember { TransferTasks.tasks.map { tasks -> tasks.count { it.running } }.distinctUntilChanged() }.collectAsStateWithLifecycle(0)
    var manualConnection by rememberSaveable { mutableStateOf(false) }
    var receiveSearch by rememberSaveable { mutableStateOf("") }
    var receiveType by rememberSaveable { mutableStateOf("All") }
    var sendStatus by remember { mutableStateOf("") }
    val pageScrollStates = List(3) { rememberLazyListState() }
    val visibleMaterials = remember(materials, receiveType, receiveSearch) {
        materials.asSequence().filter { (receiveType == "All" || it.type == receiveType) && it.name.contains(receiveSearch, ignoreCase = true) }.take(50).toList()
    }
    var settings by remember { mutableStateOf<SettingsStore.Settings?>(null) }

    fun selectPage(page: Int) {
        if (selectedTab == page) return
        focusManager.clearFocus()
        keyboard?.hide()
        selectedTab = page
    }

    // 发送文件（任务：发送页加入文件按钮；流式上传，任意格式/大小）
    var uploading by remember { mutableStateOf(false) }
    var uploadStatus by remember { mutableStateOf("") }
    var lastProgressPct by remember { mutableStateOf(-1) }

    val filePicker = androidx.activity.compose.rememberLauncherForActivityResult(
        androidx.activity.result.contract.ActivityResultContracts.OpenDocument()
    ) { uri ->
        if (uri == null) return@rememberLauncherForActivityResult
        try { context.contentResolver.takePersistableUriPermission(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION) } catch (_: Exception) {}
        uploading = true
        lastProgressPct = -1
        uploadStatus = "⬆️ 准备上传…"
        uploadFileStreaming(
            context = context,
            uri = uri,
            store = store,
            onProgress = { sent, total ->
                val pct = if (total > 0) (sent * 100 / total).toInt() else -1
                if (pct != lastProgressPct) { // 每变化 1% 才刷新，避免过度重组
                    lastProgressPct = pct
                    uploadStatus = if (pct >= 0)
                        "⬆️ 上传中 $pct%（${formatSize(sent)}/${formatSize(total)}）"
                    else "⬆️ 上传中 ${formatSize(sent)}"
                }
            },
            onResult = { ok, msg ->
                uploading = false
                if (ok) {
                    uploadStatus = "✅ $msg 已上传到电脑"
                    toast(context, "✅ 已上传到电脑")
                } else if(msg.startsWith("已暂停") || msg.startsWith("已取消")) {
                    uploadStatus=msg
                    toast(context,msg)
                } else {
                    uploadStatus = "❌ 上传失败：$msg"
                    toast(context, "❌ 上传失败: $msg")
                }
            }
        )
    }

    // 读取已保存配置
    LaunchedEffect(Unit) {
        store.settings.collect { s ->
            settings = s
            ip = s.serverIp
            port = s.serverPort
            password = s.password
            withContext(Dispatchers.IO) {
                com.liuzhuan.app.clipboard.ClipboardPrivacy.migrateLegacyAutomaticEnabled(s.autoSendClipboard)
            }
        }
    }

    fun saveAndConnect() {
        if (ip.isBlank() || port.toIntOrNull() !in 1..65534 || password.isBlank()) {
            toast(context, "请填写电脑地址、有效端口和口令")
            return
        }
        val newSettings = SettingsStore.Settings(
            serverIp = ip.trim(),
            serverPort = port.trim().ifBlank { "8899" },
            password = password.trim(),
            autoSendClipboard = com.liuzhuan.app.clipboard.ClipboardPrivacy.state.value.automaticEnabled,
            deviceName = android.os.Build.MODEL,
            balancedPower = settings?.balancedPower ?: false
        )
        settings = newSettings
        scope.launch { store.save(newSettings) }
        client.connect(newSettings)
    }

    // 扫码连接（ScanContract 现代ActivityResult API，绕过 onActivityResult 的坑）
    val scanLauncher = androidx.activity.compose.rememberLauncherForActivityResult(
        com.journeyapps.barcodescanner.ScanContract()
    ) { result ->
        val content = result.contents
        if (content == null) {
            toast(context, "未识别到二维码")
        } else {
            try {
                val json = org.json.JSONObject(content)
                val qrIp = json.optString("ip")
                val qrPort = json.optInt("port", 8899).toString()
                val qrPwd = json.optString("pwd", "")
                if (qrIp.isNotBlank() && qrPwd.isNotBlank()) {
                    ip = qrIp
                    port = qrPort
                    password = qrPwd
                    saveAndConnect()
                    toast(context, "✅ 扫码成功，正在连接 $qrIp")
                } else {
                    toast(context, "❌ 二维码内容无效")
                }
            } catch (ex: Exception) {
                toast(context, "❌ 二维码解析失败")
            }
        }
    }

    if (showTransfers) TransferTaskDialog { showTransfers = false }
    if (showHistory) ConnectionHistoryDialog(history, recallingHistory, onDismiss = { showHistory = false },
        onConnect = { profile ->
            if (!recallingHistory) {
                recallingHistory = true
                scope.launch {
                    try {
                        val recalled = historyStore.recall(profile.id)
                        ip = recalled.serverIp; port = recalled.serverPort; password = recalled.password
                        showHistory = false
                        saveAndConnect()
                    } catch (ex: CancellationException) { throw ex }
                    catch (_: Exception) { toast(context, "历史口令不可用，请重新扫码或手动输入") }
                    finally { recallingHistory = false }
                }
            }
        }, onDelete = { profile -> scope.launch {
            try { historyStore.remove(profile.id) }
            catch (ex: CancellationException) { throw ex }
            catch (_: Exception) { toast(context, "历史记录未能删除，请稍后重试") }
        } }, onClear = { scope.launch {
            try { historyStore.clear() }
            catch (ex: CancellationException) { throw ex }
            catch (_: Exception) { toast(context, "历史记录未能清空，请稍后重试") }
        } })
    Scaffold(
        topBar = {
            CenterAlignedTopAppBar(
                title = { Text("流转  /  LIUZHUAN", style = MaterialTheme.typography.titleMedium) },
                actions = { TextButton(onClick = { showTransfers = true }) { Text("收发 $runningTransfers") } },
                colors = TopAppBarDefaults.centerAlignedTopAppBarColors(
                    containerColor = MaterialTheme.colorScheme.background
                )
            )
        },
        bottomBar = {
            NavigationBar(containerColor = MaterialTheme.colorScheme.background, tonalElevation = 0.dp) {
                NavigationBarItem(
                    selected = selectedTab == 0,
                    onClick = { selectPage(0) },
                    icon = { FlowNavIcon(0, selectedTab == 0) },
                    label = { Text("连接") }
                )
                NavigationBarItem(
                    selected = selectedTab == 1,
                    onClick = { selectPage(1) },
                    icon = { FlowNavIcon(1, selectedTab == 1) },
                    label = { Text("发送") }
                )
                NavigationBarItem(
                    selected = selectedTab == 2,
                    onClick = { selectPage(2) },
                    icon = { FlowNavIcon(2, selectedTab == 2) },
                    label = { Text("接收") }
                )
            }
        }
    ) { padding ->
        LazyColumn(
            state = pageScrollStates[selectedTab],
            modifier = Modifier
                .padding(padding)
                .fillMaxSize()
                .imePadding()
                .padding(horizontal = 16.dp),
            contentPadding = PaddingValues(vertical = 16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp)
        ) {
            item(key = "page-$selectedTab") {
            Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
            FlowIntro(selectedTab, isConnected, stateText)
            if (selectedTab == 0) {
                // ===== 连接页 =====
                Card(modifier = Modifier.fillMaxWidth(), colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface)) {
                    Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                        // ===== 自动发现 + 扫码配对 =====
                        Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                            OutlinedButton(
                                onClick = search@{
                                    if (searching) return@search
                                    searching = true
                                    discoveryError = ""
                                    scope.launch {
                                        try {
                                            val found = LanDiscovery.discover()
                                            discoveredServers.clear()
                                            discoveredServers.addAll(found)
                                            if (found.isEmpty()) discoveryError = "未发现电脑，请确认电脑已启动且连接同一 Wi-Fi"
                                        } catch (ex: CancellationException) {
                                            throw ex
                                        } catch (ex: Exception) {
                                            discoveredServers.clear()
                                            discoveryError = "搜索未完成，请检查 Wi-Fi 或 VPN 后重试（${ex.javaClass.simpleName}）"
                                        } finally {
                                            searching = false
                                        }
                                    }
                                },
                                enabled = !searching,
                                modifier = Modifier.weight(1f)
                            ) { Text(if (searching) "搜索中..." else "搜索电脑") }
                            OutlinedButton(
                                onClick = {
                                    val options = com.journeyapps.barcodescanner.ScanOptions().apply {
                                        setDesiredBarcodeFormats(com.journeyapps.barcodescanner.ScanOptions.QR_CODE)
                                        setPrompt("扫描电脑屏幕上的配对二维码")
                                        setBeepEnabled(false)
                                    }
                                    scanLauncher.launch(options)
                                },
                                enabled = !isConnected,
                                modifier = Modifier.weight(1f)
                            ) { Text("扫码连接") }
                        }
                        TextButton(onClick = { showHistory = true }, modifier = Modifier.fillMaxWidth()) {
                            Text(if (history.unavailable) "历史连接 · 暂不可读取" else "历史连接 · ${history.entries.size} 个地址")
                        }
                        if (discoveryError.isNotEmpty()) Text(discoveryError, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall)
                        if (discoveredServers.isNotEmpty()) {
                            Text("发现 ${discoveredServers.size} 台电脑（点选填入 IP）", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                            discoveredServers.forEach { s ->
                                Row(
                                    Modifier
                                        .fillMaxWidth()
                                        .clickable {
                                            manualConnection = true
                                            ip = s.ip
                                            port = s.port.toString()
                                            toast(context, "已填入 ${s.name} 的 IP，请输入口令后连接")
                                        }
                                        .padding(vertical = 6.dp),
                                    verticalAlignment = Alignment.CenterVertically
                                ) {
                                    Text("🖥️", style = MaterialTheme.typography.bodyMedium)
                                    Spacer(Modifier.width(8.dp))
                                    Column(Modifier.weight(1f)) {
                                        Text(s.name, style = MaterialTheme.typography.bodyMedium)
                                        Text("${s.ip}:${s.port}", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                                    }
                                }
                            }
                        }

                        if (isConnected && !manualConnection) {
                            OutlinedButton(onClick = { client.disconnect() }, modifier = Modifier.fillMaxWidth()) { Text("断开当前电脑") }
                        }
                        if (!isConnected && !isReconnecting && ip.isNotBlank() && password.isNotBlank()) {
                            Button(onClick = { saveAndConnect() }, modifier = Modifier.fillMaxWidth()) { Text("连接上次的电脑") }
                        }
                        if (isReconnecting && !manualConnection) {
                            OutlinedButton(onClick = { client.pause() }, modifier = Modifier.fillMaxWidth()) { Text("暂停自动重连") }
                        }
                        FlowDisclosure(manualConnection, "收起手动配置", "手动输入地址与口令") {
                            if (manualConnection) { focusManager.clearFocus(); keyboard?.hide() }
                            manualConnection = !manualConnection
                        }
                        FlowReveal(manualConnection) {
                        OutlinedTextField(
                            value = ip,
                            onValueChange = { ip = it },
                            label = { Text("电脑 IP") },
                            placeholder = { Text("例如 192.168.1.5") },
                            singleLine = true,
                            modifier = Modifier.fillMaxWidth()
                        )
                        OutlinedTextField(
                            value = port,
                            onValueChange = { port = it },
                            label = { Text("端口") },
                            singleLine = true,
                            modifier = Modifier.fillMaxWidth()
                        )
                        OutlinedTextField(
                            value = password,
                            onValueChange = { password = it },
                            label = { Text("口令码") },
                            visualTransformation = PasswordVisualTransformation(),
                            singleLine = true,
                            modifier = Modifier.fillMaxWidth()
                        )

                        // 状态指示
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Box(
                                Modifier
                                    .size(12.dp),
                                contentAlignment = Alignment.Center
                            ) {
                                Surface(
                                    shape = androidx.compose.foundation.shape.CircleShape,
                                    color = when {
                                        isConnected -> Color(0xFF4CAF50)
                                        stateText.contains("失败") || stateText.contains("断开") -> Color(0xFFF44336)
                                        stateText.contains("连接中") -> Color(0xFFFFC107)
                                        else -> MaterialTheme.colorScheme.onSurfaceVariant
                                    }
                                ) { Box(Modifier.size(12.dp)) }
                            }
                            Spacer(Modifier.width(8.dp))
                            Text(stateText, style = MaterialTheme.typography.bodyMedium)
                        }

                        Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                            Button(
                                onClick = {
                                    // 检测剪贴板监控：未启用则先跳转开启（LanClient 有指数退避重连，无需用户手动重连）
                                    if (!isAccessibilityEnabled(context)) {
                                        LanController.appendLog("⚠️ 未开启剪贴板监控，正在跳转到无障碍设置...")
                                        context.startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                                    }
                                    saveAndConnect()
                                },
                                enabled = !isConnected,
                                modifier = Modifier.weight(1f)
                            ) { Text("连接") }
                            OutlinedButton(
                                onClick = { client.disconnect() },
                                enabled = isConnected,
                                modifier = Modifier.weight(1f)
                            ) { Text("断开") }
                        }

                        // ===== 暂停重连（连接失败死循环时手动打断）=====
                        if (isReconnecting) {
                            OutlinedButton(
                                onClick = { client.pause() },
                                modifier = Modifier.fillMaxWidth(),
                                colors = ButtonDefaults.outlinedButtonColors(
                                    contentColor = Color(0xFFFFB74D)
                                )
                            ) { Text("⏸️ 暂停自动重连") }
                        }

                        }
                    }
                }

                // ===== 剪贴板监控 =====
                Card(modifier = Modifier.fillMaxWidth(), colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface)) {
                    Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        Text("自动同步", style = MaterialTheme.typography.titleSmall)
                        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                            Column(Modifier.weight(1f)) {
                                Text("均衡省电", style = MaterialTheme.typography.titleSmall)
                                Text(if (settings?.balancedPower == true) "空闲时允许休眠，锁屏到达可能稍慢" else "当前优先即时同步，后台保持唤醒",
                                    style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                            }
                            Switch(checked = settings?.balancedPower == true, enabled = settings != null,
                                onCheckedChange = { enabled ->
                                    val previous = settings ?: return@Switch
                                    scope.launch {
                                        try { store.save(previous.copy(balancedPower = enabled)) }
                                        catch (ex: CancellationException) { throw ex }
                                        catch (_: Exception) { toast(context, "省电设置保存失败，请重试") }
                                    }
                                })
                        }
                        ClipboardPrivacyControls(settingsReady = settings != null) { checked ->
                            val saved = settings ?: return@ClipboardPrivacyControls
                            // 隐私组件已确认落盘；旧连接偏好仅保留兼容副本，不能反向覆盖隐私规则。
                            val mirrored = saved.copy(autoSendClipboard = checked)
                            settings = mirrored
                            scope.launch {
                                try { store.save(mirrored) }
                                catch (ex: CancellationException) { throw ex }
                                catch (_: Exception) { android.util.Log.w("ClipboardPrivacy", "旧连接偏好副本未能保存，隐私规则仍然生效") }
                            }
                        }
                        HorizontalDivider()
                        // 返回页面时重新检测（修复：系统开启了但 App 里显示未开启）
                        // 三态检测：开关/服务进程/系统绑定，防止 Settings 残留字符串误报
                        var accState by remember { mutableStateOf(accessibilityState(context)) }
                        val lifecycleOwner = androidx.compose.ui.platform.LocalLifecycleOwner.current
                        androidx.compose.runtime.DisposableEffect(lifecycleOwner) {
                            val observer = androidx.lifecycle.LifecycleEventObserver { _, event ->
                                if (event == androidx.lifecycle.Lifecycle.Event.ON_RESUME) {
                                    accState = accessibilityState(context)
                                    // 前台捕获：切回流转（获得焦点）→ 立即读剪贴板并走统一分发
                                    try {
                                        com.liuzhuan.app.clipboard.ClipboardMonitorCoordinator
                                            .captureManager.captureOnForeground()
                                    } catch (_: Exception) {
                                    }
                                }
                            }
                            lifecycleOwner.lifecycle.addObserver(observer)
                            onDispose { lifecycleOwner.lifecycle.removeObserver(observer) }
                        }
                        val (switchOn, serviceRunning, systemBound) = accState
                        Text(
                            when {
                                serviceRunning || systemBound ->
                                    "无障碍服务已连接"
                                switchOn ->
                                    "⚠️ 开关已开但服务未运行（重装后授权丢失），请到无障碍设置关闭后重新开启"
                                else ->
                                    "未开启：复制内容不会自动同步"
                            },
                            style = MaterialTheme.typography.bodySmall
                        )
                        Button(
                            onClick = {
                                context.startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))
                            }
                        ) { Text(if (serviceRunning || systemBound) "管理" else "开启监控（无障碍）") }

                        Text(
                            "$clipDiagnostic",
                            style = MaterialTheme.typography.bodySmall,
                            color = Color(0xFFFFB74D)
                        )
                        Text(
                            "诊断历史见下方「日志」卡片",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                }

                // ===== 日志（双击复制全部）=====
                Card(modifier = Modifier.fillMaxWidth(), colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface)) {
                    Column(
                        Modifier
                            .padding(16.dp)
                            .fillMaxWidth()
                            .pointerInput(logLines) {
                                detectTapGestures(
                                    onDoubleTap = {
                                        val allLogs = LanController.logs.value
                                        if (allLogs.isEmpty()) toast(context, "暂无日志可复制")
                                        else {
                                            copyToClipboard(context, allLogs.joinToString("\n"))
                                            toast(context, "✅ 已复制 ${allLogs.size} 条日志")
                                        }
                                    }
                                )
                            }
                    ) {
                        FlowDisclosure(showLogs, "收起连接诊断", "查看连接诊断与日志") { showLogs = !showLogs }
                        FlowReveal(showLogs) {
                        Text("📜 日志（双击复制）", style = MaterialTheme.typography.titleSmall)
                        Spacer(Modifier.height(6.dp))
                        // 最近事件：复制链路诊断历史（原连接页监控卡片，归集至此）
                        if (diagnosticHistory.isNotEmpty()) {
                            Text(
                                "🩺 最近事件",
                                style = MaterialTheme.typography.bodyMedium,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                            Spacer(Modifier.height(2.dp))
                            diagnosticHistory.reversed().forEach { line ->
                                Text(
                                    "· $line",
                                    style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant
                                )
                            }
                            Spacer(Modifier.height(8.dp))
                        }
                        logLines.takeLast(8).reversed().forEach { line ->
                            Text(
                                line,
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                        }
                        if (logLines.isEmpty()) {
                            Text("暂无日志", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                        }
                    }
                }
            } else if (selectedTab == 1) {
                // ===== 发送页 =====
                Card(modifier = Modifier.fillMaxWidth(), colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface)) {
                    Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                        Text("01 / 发送文字", style = MaterialTheme.typography.titleSmall)
                        OutlinedTextField(
                            value = textInput,
                            onValueChange = { textInput = it; sendStatus = "" },
                            label = { Text("文字内容") },
                            minLines = 4,
                            maxLines = 8,
                            modifier = Modifier.fillMaxWidth()
                        )
                        Button(
                            onClick = {
                                if (textInput.isNotBlank()) {
                                    sendStatus = if (client.sendText(textInput.trim())) "已提交发送，接收页可查看同步结果" else "发送未成功，请检查连接后重试"
                                }
                            },
                            enabled = isConnected && textInput.isNotBlank(),
                            modifier = Modifier.fillMaxWidth()
                        ) { Text(if (isConnected) "发送到电脑  ↑" else "连接电脑后发送") }
                        if (!isConnected) TextButton(onClick = { selectPage(0) }) { Text("前往连接电脑") }
                        FlowReveal(sendStatus.isNotBlank()) { Text(sendStatus, color = MaterialTheme.colorScheme.primary, style = MaterialTheme.typography.bodySmall) }
                        HorizontalDivider()
                        // ===== 发送文件到电脑（任意格式/大小，流式直传不经剪贴板）=====
                        Text("02 / 发送文件", style = MaterialTheme.typography.titleSmall)
                        Button(
                            onClick = { filePicker.launch(arrayOf("*/*")) },
                            enabled = isConnected && !uploading,
                            modifier = Modifier.fillMaxWidth()
                        ) { Text(if (uploading) "⬆️ 上传中…" else "📎 加入文件（任意格式）") }
                        FlowReveal(uploading) {
                            if (lastProgressPct >= 0) FlowProgressIndicator(progress = lastProgressPct / 100f, modifier = Modifier.fillMaxWidth())
                            else LinearProgressIndicator(modifier = Modifier.fillMaxWidth())
                        }
                        if (uploadStatus.isNotEmpty()) {
                            Text(
                                uploadStatus,
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                        }
                        Text(
                            "文件经局域网直传电脑端素材库，不限格式与大小",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                        HorizontalDivider()
                        TextButton(onClick = { selectPage(0) }) { Text("管理自动同步与隐私") }
                    }
                }
            } else {
                // ===== 接收页（单一事实源：Repository → StateFlow，WS 推送增量）=====
                Card(modifier = Modifier.fillMaxWidth(), colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface)) {
                    Column(Modifier.padding(16.dp)) {
                        Row(
                            Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.SpaceBetween,
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            Text("电脑端素材", style = MaterialTheme.typography.titleSmall)
                            Text(
                                "${materials.size} 条",
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                        }
                        // 轻量同步状态（一眼看出卡在哪）
                        Text(
                            if (!isConnected) "○ 已离线 · 显示上次素材" else when (syncState) {
                                com.liuzhuan.app.data.MaterialRepository.SyncState.SYNCED ->
                                    "● 已同步 · ${materials.size} 项"
                                com.liuzhuan.app.data.MaterialRepository.SyncState.SYNCING ->
                                    "● 同步中…"
                                com.liuzhuan.app.data.MaterialRepository.SyncState.RESYNCING ->
                                    "● 重新同步中…"
                                com.liuzhuan.app.data.MaterialRepository.SyncState.ERROR ->
                                    "● 同步异常"
                                else -> "● 未同步"
                            },
                            style = MaterialTheme.typography.bodySmall,
                            color = if (isConnected && syncState == com.liuzhuan.app.data.MaterialRepository.SyncState.SYNCED) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant
                        )
                        Text(
                            "文字点按复制，图片与文件点按保存",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                        Spacer(Modifier.height(8.dp))
                        OutlinedTextField(value = receiveSearch, onValueChange = { receiveSearch = it },
                            placeholder = { Text("搜索素材名称") }, singleLine = true, modifier = Modifier.fillMaxWidth(),
                            trailingIcon = { if (receiveSearch.isNotEmpty()) TextButton(onClick = { receiveSearch = "" }) { Text("清除") } })
                        Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            listOf("All" to "全部", "Text" to "文字", "Image" to "图片", "Video" to "视频", "Audio" to "音频", "Other" to "文件").forEach { (type, title) ->
                                FilterChip(selected = receiveType == type, onClick = { receiveType = type }, label = { Text(title) })
                            }
                        }
                        if (visibleMaterials.isEmpty()) {
                            Text(
                                if (receiveSearch.isNotBlank() || receiveType != "All") "没有匹配的素材，试试其他分类或关键词" else if (!isConnected) "连接电脑后，素材会出现在这里" else "还没有素材，先向电脑流转拖入一个文件",
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                        }
                    }
                }
            }
            }
            }
            if (selectedTab == 2) {
                items(visibleMaterials, key = { it.id }, contentType = { it.type }) { item ->
                    Card(modifier = Modifier.fillMaxWidth(), colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface)) {
                        Row(Modifier.fillMaxWidth()
                            .clickable(enabled = isConnected) {
                                client.requestItem(item.id)
                                toast(context, if (item.type == "Text") "正在获取文字…" else "正在获取文件…")
                            }.padding(14.dp), verticalAlignment = Alignment.CenterVertically) {
                            if (item.type == "Image") PhotoThumbnail(if (isConnected) client.thumbnailUrl(item.id) else null, item.name)
                            else Text(typeIcon(item.type), style = MaterialTheme.typography.titleMedium)
                            Spacer(Modifier.width(10.dp))
                            Column(Modifier.weight(1f)) {
                                Text(item.name.ifBlank { "(无标题)" }, style = MaterialTheme.typography.bodyMedium,
                                    maxLines = 1, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
                                Text("${typeName(item.type)} · ${formatTime(item.time)} · 点击${if (item.type == "Text") "复制" else "保存"}",
                                    style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                            }
                        }
                    }
                }
            }
        }
    }
}

/** 素材类型 → 图标 */
private fun typeIcon(type: String): String = when (type) {
    "Text" -> "📝"
    "Image" -> "🖼️"
    "Video" -> "🎬"
    "Audio" -> "🎵"
    else -> "📄"
}

/** 素材类型 → 中文名 */
private fun typeName(type: String): String = when (type) {
    "Text" -> "文字"
    "Image" -> "图片"
    "Video" -> "视频"
    "Audio" -> "音频"
    else -> "文件"
}

/** Unix 秒 → "HH:mm" */
private fun formatTime(unix: Long): String {
    if (unix <= 0) return ""
    val dt = java.util.Date(unix * 1000)
    return String.format("%02d:%02d", dt.hours, dt.minutes)
}

/** Toast（主线程） */
internal fun toast(context: Context, msg: String) {
    android.os.Handler(android.os.Looper.getMainLooper()).post {
        android.widget.Toast.makeText(context, msg, android.widget.Toast.LENGTH_SHORT).show()
    }
}

/** 复制文字到剪贴板 */
internal fun copyToClipboard(context: Context, text: String) {
    val cm = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
    cm.setPrimaryClip(ClipData.newPlainText("liuzhuan", text))
    // 循环回写防护：本 App 写入的内容标记为已处理，避免再被捕获回推 PC
    com.liuzhuan.app.clipboard.ClipboardMonitorCoordinator.markLocalText(text)
}

/**
 * 流式上传任意文件到电脑（POST /upload，PC 端 FileHttpServer 本就流式写盘）。
 *
 * 参考 LocalSend 等局域网传输实现的结论：同一可信局域网内，单条 HTTP 流式传输即可跑满
 * Wi-Fi 带宽（GB 级文件约 1 分钟），无需分块/并行；分块+断点续传留作未来协议增强。
 * 本端以 64KB buffer 从 ContentResolver 边读边发，内存占用恒定（旧版 readBytes() 整载
 * 内存在大文件时会 OOM）。PC 端要求 Content-Length，SAF 未提供长度时先落缓存再传。
 *
 * 不经剪贴板/广播（M23 ClipHook 广播路径的 512KB Binder 限制与本路径无关），任意格式任意大小。
 */
private fun uploadFileStreaming(
    context: Context,
    uri: android.net.Uri,
    store: SettingsStore,
    onProgress: (sent: Long, total: Long) -> Unit,
    onResult: (ok: Boolean, msg: String) -> Unit,
    taskId:String = java.util.UUID.randomUUID().toString()
) {
    val appContext=context.applicationContext
    TransferTasks.start("准备文件", "手机 → 电脑",taskId,cancel={TransferWork.cancel(taskId)}) {
        uploadFileStreaming(appContext,uri,SettingsStore(appContext),{_,_->},{ok,msg->toast(appContext,msg)},taskId)
    }
    val scheduled = TransferWork.launch(taskId) {
        var tmp: java.io.File? = null
        try {
            val resolver = appContext.contentResolver
            var name = "file_${System.currentTimeMillis()}.bin"
            var size = -1L
            resolver.query(uri, null, null, null, null)?.use { c ->
                val iName = c.getColumnIndex(android.provider.OpenableColumns.DISPLAY_NAME)
                val iSize = c.getColumnIndex(android.provider.OpenableColumns.SIZE)
                if (c.moveToFirst()) {
                    if (iName >= 0) c.getString(iName)?.let { name = it }
                    if (iSize >= 0 && !c.isNull(iSize)) size = c.getLong(iSize)
                }
            }
            if (name.isBlank()) name = "file_${System.currentTimeMillis()}.bin"

            TransferTasks.progress(taskId,0,size.coerceAtLeast(0),name)
            var readFrom = uri
            if (size < 0) {
                // 个别 DocumentsProvider 不报长度 → 先流式落缓存拿 Content-Length
                val f = java.io.File(appContext.cacheDir, "lz_upload_${java.util.UUID.randomUUID()}")
                tmp = f // 中途失败也能清理，不能等复制完成才登记。
                val budget = (appContext.cacheDir.usableSpace - 1024 * 1024).coerceAtLeast(0)
                val source = resolver.openInputStream(uri) ?: throw Exception("无法读取文件")
                val copyJob = kotlinx.coroutines.currentCoroutineContext()[kotlinx.coroutines.Job]!!
                TransferWork.useResource(taskId, source) { input ->
                    f.outputStream().use { output ->
                        val buffer = ByteArray(64 * 1024)
                        var copied = 0L
                        while (true) {
                            copyJob.ensureActive()
                            val n = input.read(buffer)
                            if (n < 0) break
                            copied += n
                            if (copied > budget) throw Exception("缓存空间不足，请释放手机空间后重试")
                            output.write(buffer, 0, n)
                            PowerSignals.touch()
                        }
                    }
                }
                size = f.length()
                readFrom = android.net.Uri.fromFile(f)
                tmp = f
            }
            if (size <= 0L) throw Exception("空文件")

            val settings = store.settings.first()
            if (settings.serverIp.isBlank()) throw Exception("未配置电脑 IP")
            val auth = Proto.sha256Hex(settings.password)
            val url = "http://${settings.serverIp}:${(settings.serverPort.toIntOrNull() ?: 8899)+1}/upload?name=" +
                java.net.URLEncoder.encode(name, "UTF-8") + "&auth=$auth"

            val body = object : okhttp3.RequestBody() {
                override fun contentType(): okhttp3.MediaType? =
                    "application/octet-stream".toMediaTypeOrNull()
                override fun contentLength(): Long = size
                override fun writeTo(sink: okio.BufferedSink) {
                    val source = resolver.openInputStream(readFrom) ?: throw Exception("无法读取文件")
                    TransferWork.useResource(taskId, source) { input ->
                        val buf = ByteArray(64 * 1024)
                        var sent = 0L
                        var lastUiAt = 0L
                        while (true) {
                            val n = input.read(buf)
                            if (n <= 0) break
                            sink.write(buf, 0, n)
                            sent += n
                            PowerSignals.touch()
                            val now = android.os.SystemClock.elapsedRealtime()
                            if (now - lastUiAt >= 250 || sent == size) {
                                lastUiAt = now
                                TransferTasks.progress(taskId,sent,size)
                                val published = sent
                                android.os.Handler(android.os.Looper.getMainLooper()).post { onProgress(published, size) }
                            }
                        }
                    }
                }
            }
            TransferWork.withCall(taskId, com.liuzhuan.app.net.HttpClients.transfer.newCall(Request.Builder().url(url).post(body).build())) { call ->
            call.execute().use { resp ->
                if (!resp.isSuccessful) throw Exception("HTTP ${resp.code}")
                TransferTasks.finish(taskId,true,"电脑已接收")
                android.os.Handler(android.os.Looper.getMainLooper()).post { onResult(true,name) }
            }
            }
        } catch (ex: Exception) {
            if (ex is CancellationException) {
                val stopped=if(TransferTasks.tasks.value.firstOrNull {it.id==taskId}?.paused==true) "已暂停，可在收发任务中继续" else "已取消"
                android.os.Handler(android.os.Looper.getMainLooper()).post { onResult(false, stopped) }
                throw ex
            }
            TransferTasks.finish(taskId,false,"${ex.message ?: "传输失败"}；重试前请检查电脑是否已收到")
            android.os.Handler(android.os.Looper.getMainLooper()).post { onResult(false, ex.message?.take(40) ?: "未知错误") }
        } finally {
            tmp?.delete()
        }
    }
    if (!scheduled) onResult(false, "等待任务较多，请稍后重新传输")
}

/** 字节数 → 人类可读大小 */
private fun formatSize(bytes: Long): String = when {
    bytes >= 1L shl 30 -> String.format("%.1f GB", bytes / 1073741824.0)
    bytes >= 1L shl 20 -> String.format("%.1f MB", bytes / 1048576.0)
    bytes >= 1L shl 10 -> String.format("%.1f KB", bytes / 1024.0)
    else -> "$bytes B"
}

/** 下载采用流式写入与 IS_PENDING，未完成的文件不会出现在相册。 */
internal fun receiveFile(context:Context,data:Proto.ItemData) {
    val id="download:${data.id}"
    if(TransferTasks.running(id)) return
    val name=data.name.ifBlank { "liuzhuan_${System.currentTimeMillis()}" }
    val app = context.applicationContext
    TransferTasks.start(name,"电脑 → 手机",id,cancel={TransferWork.cancel(id)}) { receiveFile(app,data) }
    TransferWork.launch(id) {
        try { downloadToMediaStore(app,data.downloadUrl,name,id); TransferTasks.finish(id,true,"已保存到手机"); toast(context,"已保存到手机") }
        catch(ex:Exception) { if(ex is CancellationException) throw ex;TransferTasks.finish(id,false,ex.message ?: "保存失败");toast(app,"保存失败，请在收发任务中重试") }
    }
}

private suspend fun downloadToMediaStore(context: Context, url: String, fileName: String, taskId:String) {
    val safeName = fileName.substringAfterLast('/').ifBlank { "liuzhuan_${System.currentTimeMillis()}" }
    TransferWork.withCall(taskId, com.liuzhuan.app.net.HttpClients.transfer.newCall(Request.Builder().url(url).build())) { call ->
    call.execute().use { resp ->
        if (!resp.isSuccessful) throw Exception("HTTP ${resp.code}")
        val body=resp.body ?: throw Exception("empty body")
        val total=body.contentLength()
        val collection = MediaStore.Downloads.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY)
        val values=ContentValues().apply {
            put(MediaStore.MediaColumns.DISPLAY_NAME,safeName)
            put(MediaStore.MediaColumns.MIME_TYPE,mimeFor(safeName))
            put(MediaStore.MediaColumns.RELATIVE_PATH,ReceivedFiles.FOLDER)
            put(MediaStore.MediaColumns.IS_PENDING,1)
        }
        val resolver=context.contentResolver
        val uri=resolver.insert(collection,values) ?: throw Exception("无法创建保存文件")
        try {
            resolver.openOutputStream(uri)?.use { output ->
                body.byteStream().use { input ->
                    val buffer=ByteArray(65536); var done=0L
                    while(true) { val n=input.read(buffer); if(n<0) break; output.write(buffer,0,n); done+=n;PowerSignals.touch();TransferTasks.progress(taskId,done,total.coerceAtLeast(0)) }
                    if(total>=0 && done!=total) throw Exception("下载中断：$done/$total 字节")
                }
            } ?: throw Exception("无法写入文件")
            if(resolver.update(uri,ContentValues().apply {put(MediaStore.MediaColumns.IS_PENDING,0)},null,null)<=0) throw Exception("无法完成文件保存")
            TransferTasks.savedTo(taskId,ReceivedFiles.FOLDER)
        } catch(ex:Exception) { resolver.delete(uri,null,null);throw ex }
    }
    }
}
private fun mimeFor(name: String): String {
    val n = name.lowercase()
    return when {
        n.endsWith(".png") -> "image/png"
        n.endsWith(".jpg") || n.endsWith(".jpeg") -> "image/jpeg"
        n.endsWith(".gif") -> "image/gif"
        n.endsWith(".webp") -> "image/webp"
        n.endsWith(".mp4") -> "video/mp4"
        n.endsWith(".mp3") -> "audio/mpeg"
        n.endsWith(".wav") -> "audio/wav"
        else -> "application/octet-stream"
    }
}

/**
 * 无障碍状态三态检测（区分「开关开了但服务没跑」和「开关没开」）
 *
 * @return Triple(开关已开, 服务真实运行中, 框架绑定列表里是否存在)
 * - serviceRunning：ClipMonitorService.onServiceConnected 已触发（本轮进程内真实状态）
 * - systemBound：AccessibilityManager 框架层面已注册本服务（比读 Settings 字符串可靠，
 *   Settings 字符串在卸载重装后可能是残留，导致误显示「已开启」）
 */
private fun accessibilityState(context: Context): Triple<Boolean, Boolean, Boolean> {
    // 1) 服务进程内真实运行标志
    val serviceRunning = com.liuzhuan.app.clipboard.ClipMonitorService.isRunning

    // 2) 框架真实绑定列表（反映 AccessibilityManagerService 当前已绑定的服务）
    val am = context.getSystemService(Context.ACCESSIBILITY_SERVICE)
        as? android.view.accessibility.AccessibilityManager
    val systemBound = am?.getEnabledAccessibilityServiceList(
        android.accessibilityservice.AccessibilityServiceInfo.FEEDBACK_GENERIC
    )?.any {
        it.resolveInfo?.serviceInfo?.packageName == context.packageName &&
                it.resolveInfo?.serviceInfo?.name?.contains("ClipMonitorService") == true
    } ?: false

    // 3) Settings 字符串（可能残留，仅作兜底显示）
    val enabled = Settings.Secure.getString(
        context.contentResolver,
        Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES
    ) ?: ""
    val switchOn = enabled.split(':').any {
        (it.contains("com.liuzhuan.app") && it.contains("ClipMonitorService")) ||
        it.contains("com.liuzhuan.app.clipboard.ClipMonitorService")
    }

    return Triple(switchOn, serviceRunning, systemBound)
}

/** 兼容旧调用：任一信号为真即视为已启用 */
private fun isAccessibilityEnabled(context: Context): Boolean {
    val (switchOn, serviceRunning, systemBound) = accessibilityState(context)
    return switchOn || serviceRunning || systemBound
}
