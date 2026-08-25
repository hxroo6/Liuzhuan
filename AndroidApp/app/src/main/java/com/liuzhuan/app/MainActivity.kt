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
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import com.liuzhuan.app.core.SettingsStore
import com.liuzhuan.app.net.LanClient
import com.liuzhuan.app.net.LanDiscovery
import com.liuzhuan.app.net.Proto
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import java.util.concurrent.TimeUnit

class MainActivity : ComponentActivity() {

    private lateinit var store: SettingsStore

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        store = SettingsStore(this)

        // Android 13+ 通知权限（前台服务通知需要）
        if (Build.VERSION.SDK_INT >= 33) {
            requestPermissions(arrayOf(android.Manifest.permission.POST_NOTIFICATIONS), 1)
        }

        // 创建全局连接（供剪贴板服务共享）
        // 先断开旧 client（防止 Activity 重建时残留多个 LanClient → 日志重复）
        LanHub.client?.disconnect()
        val logLines = mutableStateListOf<String>()
        val stateText = mutableStateOf("未连接")
        val isConnected = mutableStateOf(false)
        val isReconnecting = mutableStateOf(false)
        val remoteItems = mutableStateListOf<Proto.ItemSummary>()
        val client = LanClient(
            onState = { s ->
                stateText.value = when (s) {
                    LanClient.State.Idle -> "未连接"
                    LanClient.State.Connecting -> "连接中..."
                    LanClient.State.Connected -> "已连接 ✓"
                    is LanClient.State.AuthFailed -> "认证失败：${s.reason}"
                    LanClient.State.Disconnected -> "已断开（自动重连中...）"
                    LanClient.State.Paused -> "已暂停重连"
                }
                isConnected.value = s == LanClient.State.Connected
                // 是否处于「断开后自动重连」状态（供暂停按钮显示）
                isReconnecting.value = s == LanClient.State.Disconnected || s == LanClient.State.Connecting
                // 前台服务保活：连接成功 → 启动；断开/失败/暂停 → 停止
                if (s == LanClient.State.Connected) {
                    ContextCompat.startForegroundService(
                        this,
                        Intent(this, ForegroundService::class.java)
                    )
                } else if (s is LanClient.State.AuthFailed || s == LanClient.State.Disconnected || s == LanClient.State.Paused) {
                    stopService(Intent(this, ForegroundService::class.java))
                }
            },
            onLog = { msg ->
                if (logLines.size > 50) logLines.removeAt(0)
                logLines.add(msg)
            },
            // 接收页：全量拉取 → 替换
            onListData = { items ->
                remoteItems.clear()
                remoteItems.addAll(items.take(50))
            },
            // 接收页：新增广播 → 去重插入最前
            onItemAdded = { item ->
                if (remoteItems.any { it.id == item.id }) return@LanClient
                remoteItems.add(0, item)
                while (remoteItems.size > 50) remoteItems.removeAt(remoteItems.size - 1)
            },
            // 接收页：点击素材 → 文字复制 / 文件下载
            onItemData = { data ->
                val appContext = applicationContext
                if (data.error.isNotEmpty()) {
                    toast(appContext, "❌ 获取素材失败")
                    return@LanClient
                }
                if (data.content.isNotEmpty()) {
                    // 文字 → 复制剪贴板
                    copyToClipboard(appContext, data.content)
                    toast(appContext, "📝 已复制到剪贴板")
                } else if (data.downloadUrl.isNotEmpty()) {
                    // 文件 → 下载保存（MediaStore，Android 10+ 免存储权限）
                    val fileName = data.name.ifBlank { "liuzhuan_${System.currentTimeMillis()}" }
                    Thread {
                        try {
                            downloadToMediaStore(appContext, data.downloadUrl, fileName, data.type)
                            toast(appContext, "✅ 已保存到手机")
                        } catch (ex: Exception) {
                            toast(appContext, "❌ 保存失败: ${ex.message?.take(30)}")
                        }
                    }.start()
                }
            }
        )
        LanHub.client = client

        setContent {
            MaterialTheme(colorScheme = darkColorScheme()) {
                MainScreen(
                    store = store,
                    client = client,
                    stateText = stateText.value,
                    isConnected = isConnected.value,
                    isReconnecting = isReconnecting.value,
                    logLines = logLines,
                    remoteItems = remoteItems
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

    /** 返回键 → 缩进后台（不退出 App，保活连接不断） */
    @Deprecated("Deprecated in Java")
    override fun onBackPressed() {
        moveTaskToBack(true)
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

    /** 分享的文件 → HTTP POST 上传到电脑 */
    private fun uploadSharedFile(uri: android.net.Uri) {
        toast(this, "⬆️ 正在上传...")
        Thread {
            try {
                val resolver = contentResolver
                // 文件名（ContentResolver 查询）
                var name = "share_" + System.currentTimeMillis() + ".bin"
                resolver.query(uri, null, null, null, null)?.use { c ->
                    val idx = c.getColumnIndex(android.provider.OpenableColumns.DISPLAY_NAME)
                    if (idx >= 0 && c.moveToFirst()) c.getString(idx)?.let { name = it }
                }
                // 读取全部字节（基础版；大文件后续分块）
                val bytes = resolver.openInputStream(uri)?.use { it.readBytes() }
                    ?: throw Exception("无法读取文件")
                if (bytes.isEmpty()) { toast(this, "❌ 空文件"); return@Thread }

                // 电脑 IP + 口令哈希（从配置读取，下载/上传鉴权用）
                val settings = runBlocking { store.settings.first() }
                if (settings.serverIp.isBlank()) { toast(this, "❌ 未配置电脑 IP"); return@Thread }
                val auth = Proto.sha256Hex(settings.password)

                val url = "http://${settings.serverIp}:8900/upload?name=" +
                    java.net.URLEncoder.encode(name, "UTF-8") + "&auth=$auth"
                val client = OkHttpClient.Builder()
                    .connectTimeout(10, TimeUnit.SECONDS)
                    .writeTimeout(300, TimeUnit.SECONDS)
                    .build()
                val request = Request.Builder()
                    .url(url)
                    .post(bytes.toRequestBody())
                    .build()
                client.newCall(request).execute().use { resp ->
                    if (resp.isSuccessful) {
                        toast(this, "✅ 已上传到电脑")
                    } else {
                        toast(this, "❌ 上传失败 HTTP ${resp.code}")
                    }
                }
            } catch (ex: Exception) {
                toast(this, "❌ 上传失败: ${ex.message?.take(30)}")
            }
        }.start()
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun MainScreen(
    store: SettingsStore,
    client: LanClient,
    stateText: String,
    isConnected: Boolean,
    isReconnecting: Boolean,
    logLines: androidx.compose.runtime.snapshots.SnapshotStateList<String>,
    remoteItems: androidx.compose.runtime.snapshots.SnapshotStateList<Proto.ItemSummary>
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()

    // 自动发现状态
    var searching by remember { mutableStateOf(false) }
    val discoveredServers = remember { mutableStateListOf<LanDiscovery.Server>() }

    var ip by remember { mutableStateOf("") }
    var port by remember { mutableStateOf("8899") }
    var password by remember { mutableStateOf("") }
    var textInput by remember { mutableStateOf("") }
    var selectedTab by remember { mutableStateOf(0) }
    var settings by remember { mutableStateOf<SettingsStore.Settings?>(null) }
    var autoSend by remember { mutableStateOf(true) }

    // 读取已保存配置
    LaunchedEffect(Unit) {
        store.settings.collect { s ->
            settings = s
            ip = s.serverIp
            port = s.serverPort
            password = s.password
            autoSend = s.autoSendClipboard
            LanHub.autoSendClipboard = s.autoSendClipboard // 同步给无障碍服务
        }
    }

    fun saveAndConnect() {
        val newSettings = SettingsStore.Settings(
            serverIp = ip.trim(),
            serverPort = port.trim().ifBlank { "8899" },
            password = password.trim(),
            deviceName = android.os.Build.MODEL
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

    Scaffold(
        topBar = {
            CenterAlignedTopAppBar(
                title = { Text("⚡ 流转") },
                colors = TopAppBarDefaults.centerAlignedTopAppBarColors(
                    containerColor = Color(0xFF16161E)
                )
            )
        },
        bottomBar = {
            NavigationBar {
                NavigationBarItem(
                    selected = selectedTab == 0,
                    onClick = { selectedTab = 0 },
                    icon = { Text("🔌") },
                    label = { Text("连接") }
                )
                NavigationBarItem(
                    selected = selectedTab == 1,
                    onClick = { selectedTab = 1 },
                    icon = { Text("📤") },
                    label = { Text("发送") }
                )
                NavigationBarItem(
                    selected = selectedTab == 2,
                    onClick = { selectedTab = 2 },
                    icon = { Text("📥") },
                    label = { Text("接收") }
                )
            }
        }
    ) { padding ->
        Column(
            modifier = Modifier
                .padding(padding)
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp)
        ) {
            if (selectedTab == 0) {
                // ===== 连接页 =====
                Card {
                    Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
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
                                        else -> Color(0xFF9E9E9E)
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
                                        logLines.add("⚠️ 未开启剪贴板监控，正在跳转到无障碍设置...")
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

                        // ===== 自动发现 + 扫码配对 =====
                        Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                            OutlinedButton(
                                onClick = {
                                    scope.launch {
                                        searching = true
                                        val found = LanDiscovery.discover()
                                        discoveredServers.clear()
                                        discoveredServers.addAll(found)
                                        searching = false
                                        if (found.isEmpty()) {
                                            toast(context, "未发现电脑端流转\n请确认电脑已启动且在同一Wi-Fi")
                                        }
                                    }
                                },
                                enabled = !searching,
                                modifier = Modifier.weight(1f)
                            ) { Text(if (searching) "搜索中..." else "🔍 搜索电脑") }
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
                            ) { Text("📷 扫码连接") }
                        }
                        if (discoveredServers.isNotEmpty()) {
                            Text("发现 ${discoveredServers.size} 台电脑（点选填入 IP）", style = MaterialTheme.typography.bodySmall, color = Color(0xFF9E9E9E))
                            discoveredServers.forEach { s ->
                                Row(
                                    Modifier
                                        .fillMaxWidth()
                                        .clickable {
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
                                        Text("${s.ip}:${s.port}", style = MaterialTheme.typography.bodySmall, color = Color(0xFF9E9E9E))
                                    }
                                }
                            }
                        }
                    }
                }

                // ===== 剪贴板监控 =====
                Card {
                    Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        Text("📋 剪贴板监控", style = MaterialTheme.typography.titleSmall)
                        // 返回页面时重新检测（修复：系统开启了但 App 里显示未开启）
                        // 三态检测：开关/服务进程/系统绑定，防止 Settings 残留字符串误报
                        var accState by remember { mutableStateOf(accessibilityState(context)) }
                        val lifecycleOwner = androidx.compose.ui.platform.LocalLifecycleOwner.current
                        androidx.compose.runtime.DisposableEffect(lifecycleOwner) {
                            val observer = androidx.lifecycle.LifecycleEventObserver { _, event ->
                                if (event == androidx.lifecycle.Lifecycle.Event.ON_RESUME) {
                                    accState = accessibilityState(context)
                                }
                            }
                            lifecycleOwner.lifecycle.addObserver(observer)
                            onDispose { lifecycleOwner.lifecycle.removeObserver(observer) }
                        }
                        val (switchOn, serviceRunning, systemBound) = accState
                        Text(
                            when {
                                serviceRunning || systemBound ->
                                    "✅ 运行中：复制文字将自动同步到电脑"
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
                    }
                }

                // ===== 日志（双击复制全部）=====
                Card {
                    Column(
                        Modifier
                            .padding(16.dp)
                            .fillMaxWidth()
                            .pointerInput(logLines) {
                                detectTapGestures(
                                    onDoubleTap = {
                                        if (logLines.isEmpty()) {
                                            toast(context, "暂无日志可复制")
                                        } else {
                                            val clipboard = context.getSystemService(android.content.Context.CLIPBOARD_SERVICE)
                                                    as android.content.ClipboardManager
                                            clipboard.setPrimaryClip(
                                                android.content.ClipData.newPlainText(
                                                    "liuzhuan_logs", logLines.joinToString("\n")
                                                )
                                            )
                                            toast(context, "✅ 已复制 ${logLines.size} 条日志")
                                        }
                                    }
                                )
                            }
                    ) {
                        Text("📜 日志（双击复制）", style = MaterialTheme.typography.titleSmall)
                        Spacer(Modifier.height(6.dp))
                        logLines.takeLast(8).reversed().forEach { line ->
                            Text(
                                line,
                                style = MaterialTheme.typography.bodySmall,
                                color = Color(0xFF9E9E9E)
                            )
                        }
                        if (logLines.isEmpty()) {
                            Text("暂无日志", style = MaterialTheme.typography.bodySmall, color = Color(0xFF616161))
                        }
                    }
                }
            } else if (selectedTab == 1) {
                // ===== 发送页 =====
                Card {
                    Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                        Text("📤 发送文字到电脑", style = MaterialTheme.typography.titleSmall)
                        OutlinedTextField(
                            value = textInput,
                            onValueChange = { textInput = it },
                            label = { Text("文字内容") },
                            minLines = 4,
                            maxLines = 8,
                            modifier = Modifier.fillMaxWidth()
                        )
                        Button(
                            onClick = {
                                if (textInput.isNotBlank()) {
                                    client.sendText(textInput.trim())
                                }
                            },
                            enabled = isConnected,
                            modifier = Modifier.fillMaxWidth()
                        ) { Text("发送") }
                        HorizontalDivider()
                        // ===== 剪贴板自动发送开关 =====
                        Row(
                            Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.SpaceBetween,
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            Column(Modifier.weight(1f)) {
                                Text("后台自动发送剪贴板", style = MaterialTheme.typography.bodyMedium)
                                Text(
                                    "开启后：在任意 App 复制 → 自动读取并发送到电脑",
                                    style = MaterialTheme.typography.bodySmall,
                                    color = Color(0xFF757575)
                                )
                            }
                            Switch(
                                checked = autoSend,
                                onCheckedChange = { checked ->
                                    autoSend = checked
                                    LanHub.autoSendClipboard = checked // 立即同步给无障碍服务
                                    scope.launch {
                                        store.save(
                                            settings?.copy(autoSendClipboard = checked)
                                                ?: SettingsStore.Settings(autoSendClipboard = checked)
                                        )
                                    }
                                    toast(context, if (checked) "已开启自动发送" else "已关闭自动发送")
                                }
                            )
                        }
                    }
                }
            } else {
                // ===== 接收页（与电脑端流转最近素材同步，WS 推送增量）=====
                Card {
                    Column(Modifier.padding(16.dp)) {
                        Row(
                            Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.SpaceBetween,
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            Text("📥 电脑端最近素材", style = MaterialTheme.typography.titleSmall)
                            Text(
                                "${remoteItems.size} 条",
                                style = MaterialTheme.typography.bodySmall,
                                color = Color(0xFF9E9E9E)
                            )
                        }
                        Text(
                            "实时同步：电脑流转新增素材会即时显示（仅元信息，省电省流量）",
                            style = MaterialTheme.typography.bodySmall,
                            color = Color(0xFF757575)
                        )
                        Spacer(Modifier.height(8.dp))
                        if (remoteItems.isEmpty()) {
                            Text(
                                "暂无素材\n提示：连接后自动拉取，电脑端新增会实时推送",
                                style = MaterialTheme.typography.bodySmall,
                                color = Color(0xFF616161)
                            )
                        } else {
                            remoteItems.take(50).forEach { item ->
                                Row(
                                    Modifier
                                        .fillMaxWidth()
                                        .clickable { client.requestItem(item.id) }
                                        .padding(vertical = 8.dp),
                                    verticalAlignment = Alignment.CenterVertically
                                ) {
                                    Text(
                                        typeIcon(item.type),
                                        style = MaterialTheme.typography.titleMedium
                                    )
                                    Spacer(Modifier.width(10.dp))
                                    Column(Modifier.weight(1f)) {
                                        Text(
                                            item.name.ifBlank { "(无标题)" },
                                            style = MaterialTheme.typography.bodyMedium,
                                            maxLines = 1,
                                            overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis
                                        )
                                        Text(
                                            "${typeName(item.type)} · ${formatTime(item.time)} · 点击${if (item.type == "Text") "复制" else "保存"}",
                                            style = MaterialTheme.typography.bodySmall,
                                            color = Color(0xFF9E9E9E)
                                        )
                                    }
                                }
                                HorizontalDivider(color = Color(0xFF2A2A35))
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
private fun toast(context: Context, msg: String) {
    android.os.Handler(android.os.Looper.getMainLooper()).post {
        android.widget.Toast.makeText(context, msg, android.widget.Toast.LENGTH_SHORT).show()
    }
}

/** 复制文字到剪贴板 */
private fun copyToClipboard(context: Context, text: String) {
    val cm = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
    cm.setPrimaryClip(ClipData.newPlainText("liuzhuan", text))
}

/** 下载文件到 MediaStore（Android 10+ 免存储权限） */
private fun downloadToMediaStore(context: Context, url: String, fileName: String, type: String) {
    val safeName = fileName.substringAfterLast('/').ifBlank { "liuzhuan_${System.currentTimeMillis()}" }
    val client = OkHttpClient.Builder()
        .connectTimeout(10, TimeUnit.SECONDS)
        .readTimeout(180, TimeUnit.SECONDS)
        .build()
    val req = Request.Builder().url(url).build()
    client.newCall(req).execute().use { resp ->
        if (!resp.isSuccessful) throw Exception("HTTP ${resp.code}")
        val bytes = resp.body?.bytes() ?: throw Exception("empty body")
        val collection = when (type) {
            "Image" -> MediaStore.Images.Media.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY)
            "Video" -> MediaStore.Video.Media.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY)
            "Audio" -> MediaStore.Audio.Media.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY)
            else -> MediaStore.Downloads.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY)
        }
        val relativePath = when (type) {
            "Image" -> "Pictures/Liuzhuan"
            "Video" -> "Movies/Liuzhuan"
            "Audio" -> "Music/Liuzhuan"
            else -> "Download/Liuzhuan"
        }
        val values = ContentValues().apply {
            put(MediaStore.MediaColumns.DISPLAY_NAME, safeName)
            put(MediaStore.MediaColumns.MIME_TYPE, mimeFor(safeName))
            put(MediaStore.MediaColumns.RELATIVE_PATH, relativePath)
        }
        val uri = context.contentResolver.insert(collection, values)
            ?: throw Exception("insert failed")
        context.contentResolver.openOutputStream(uri)?.use { it.write(bytes) }
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
