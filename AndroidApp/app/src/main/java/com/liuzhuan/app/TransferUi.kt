package com.liuzhuan.app

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.util.LruCache
import androidx.compose.animation.animateContentSize
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.tween
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.launch
import okhttp3.Request

private val thumbnails=object:LruCache<String,Bitmap>(8*1024*1024) { override fun sizeOf(key:String,value:Bitmap)=value.byteCount }
private val thumbnailSlots=Semaphore(3)
private val thumbnailHttp=com.liuzhuan.app.net.HttpClients.thumbnail
private val thumbnailFailure = object:LruCache<String,Long>(64) {}

/** 每个 URL 的等待者共用锁；消费者归零后移除，长期滚动不会积累锁。 */
private data class ThumbnailFlight(val mutex: kotlinx.coroutines.sync.Mutex = kotlinx.coroutines.sync.Mutex(), var users: Int = 0)
private val flights = mutableMapOf<String, ThumbnailFlight>()

private suspend fun loadThumbnail(url: String): Bitmap? {
    val flight = synchronized(flights) { flights.getOrPut(url) { ThumbnailFlight() }.also { it.users++ } }
    try {
        return flight.mutex.withLock {
            thumbnails.get(url)?.let { return@withLock it }
            val failedAt = thumbnailFailure.get(url)
            if (failedAt != null && android.os.SystemClock.elapsedRealtime() - failedAt < 3_000) return@withLock null
            thumbnailSlots.withPermit {
                thumbnails.get(url)?.let { return@withPermit it }
                kotlinx.coroutines.coroutineScope {
                    val call = thumbnailHttp.newCall(Request.Builder().url(url).build())
                    kotlinx.coroutines.suspendCancellableCoroutine<Bitmap?> { continuation ->
                        continuation.invokeOnCancellation { call.cancel() }
                        launch(Dispatchers.IO) {
                            try {
                                val decoded = call.execute().use { response ->
                                    if (!response.isSuccessful) return@use null
                                    val body = response.body ?: return@use null
                                    if (body.contentLength() > 2*1024*1024) return@use null
                                    val bytes = body.byteStream().use { it.readNBytes(2*1024*1024+1) }
                                    if (bytes.size > 2*1024*1024) return@use null
                                    val bounds=BitmapFactory.Options().apply { inJustDecodeBounds=true }
                                    BitmapFactory.decodeByteArray(bytes,0,bytes.size,bounds)
                                    if(bounds.outWidth<=0 || bounds.outHeight<=0) return@use null
                                    val options=BitmapFactory.Options().apply { inSampleSize=1; while(bounds.outWidth/inSampleSize>512 || bounds.outHeight/inSampleSize>512) inSampleSize*=2 }
                                    BitmapFactory.decodeByteArray(bytes,0,bytes.size,options)
                                }
                                if (continuation.isActive) {
                                    if (decoded != null) { thumbnails.put(url, decoded); thumbnailFailure.remove(url) }
                                    else thumbnailFailure.put(url, android.os.SystemClock.elapsedRealtime())
                                    continuation.resumeWith(Result.success(decoded))
                                }
                            } catch (ex: Exception) {
                                if (continuation.isActive) {
                                    thumbnailFailure.put(url, android.os.SystemClock.elapsedRealtime())
                                    continuation.resumeWith(Result.success(null))
                                }
                            }
                        }
                    }
                }
            }
        }
    } finally {
        synchronized(flights) { if (--flight.users == 0) flights.remove(url) }
    }
}

internal fun clearThumbnailCache() { thumbnails.evictAll(); thumbnailFailure.evictAll() }

@Composable fun PhotoThumbnail(url:String?, label:String) {
    // A recycled row must never briefly display another item's previously loaded bitmap.
    key(url) { ThumbnailContent(url, label) }
}

@Composable private fun ThumbnailContent(url:String?, label:String) {
    var bitmap by remember(url) { mutableStateOf<Bitmap?>(null) }
    LaunchedEffect(url) {
        bitmap=null
        if(url==null) return@LaunchedEffect
        bitmap=thumbnails.get(url) ?: loadThumbnail(url)
    }
    val opacity by animateFloatAsState(if (bitmap == null) 0f else 1f, tween(180), label = "thumbnail reveal")
    Surface(Modifier.size(56.dp),color=MaterialTheme.colorScheme.surfaceVariant,shape=MaterialTheme.shapes.small) {
        Box(contentAlignment = Alignment.Center) {
            if (bitmap == null) Text("图片", style=MaterialTheme.typography.labelSmall, color=MaterialTheme.colorScheme.onSurfaceVariant)
            bitmap?.let { image ->
                Image(image.asImageBitmap(),contentDescription=label,
                    modifier=Modifier.fillMaxSize().clip(MaterialTheme.shapes.small).graphicsLayer { alpha=opacity },contentScale=ContentScale.Crop)
            }
        }
    }
}

@Composable fun TransferTaskDialog(onDismiss:()->Unit) {
    val entries by TransferTasks.tasks.collectAsStateWithLifecycle()
    val context=LocalContext.current
    val store=remember { com.liuzhuan.app.core.SettingsStore(context) }
    val settings by store.settings.collectAsStateWithLifecycle(com.liuzhuan.app.core.SettingsStore.Settings())
    val scope=rememberCoroutineScope()
    var folderPicker by remember { mutableStateOf(false) }
    var managerPicker by remember { mutableStateOf(false) }
    var filter by remember { mutableIntStateOf(0) }
    var managers by remember { mutableStateOf<List<ReceivedFiles.Manager>>(emptyList()) }
    LaunchedEffect(managerPicker) { if(managerPicker) managers=kotlinx.coroutines.withContext(Dispatchers.IO) { runCatching { ReceivedFiles.managers(context) }.getOrDefault(emptyList()) } }
    fun selectManager(packageName:String) { scope.launch {
        try {store.saveFileManager(packageName);managerPicker=false}
        catch(_:Exception) {toast(context,"文件管理器设置未保存，请稍后重试")}
    } }
    Dialog(onDismissRequest=onDismiss,properties=DialogProperties(usePlatformDefaultWidth=false)) {
        Surface(Modifier.fillMaxSize(),color=MaterialTheme.colorScheme.background) {
        Column(Modifier.fillMaxSize().safeDrawingPadding().padding(horizontal=20.dp)) {
            Row(Modifier.fillMaxWidth().padding(vertical=12.dp),verticalAlignment=Alignment.CenterVertically) {
                Text("收发任务",style=MaterialTheme.typography.headlineSmall,modifier=Modifier.weight(1f))
                TextButton(onClick=onDismiss){Text("关闭")}
            }
            Text("${entries.count { it.running }} 项进行中 · ${entries.count { it.paused }} 项暂停 · ${entries.count { it.failed }} 项失败",
                style=MaterialTheme.typography.bodyMedium,color=MaterialTheme.colorScheme.onSurfaceVariant)
            Row(horizontalArrangement=Arrangement.spacedBy(8.dp)) {
                OutlinedButton(onClick={folderPicker=true}) { Text("接收目录") }
                TextButton(onClick={managerPicker=true}) { Text("文件管理器") }
            }
            Text("暂停后继续会从头传输，暂不支持断点续传。",style=MaterialTheme.typography.labelSmall,color=MaterialTheme.colorScheme.onSurfaceVariant)
            Row(Modifier.fillMaxWidth().padding(top=8.dp),horizontalArrangement=Arrangement.spacedBy(8.dp)) {
                listOf("全部","进行中","已结束").forEachIndexed { index,label ->
                    FilterChip(selected=filter==index,onClick={filter=index},label={Text(label)})
                }
            }
            val visible=entries.filter { filter==0 || if(filter==1) it.running || it.paused else !it.running && !it.paused }
            if(visible.isEmpty()) Box(Modifier.weight(1f).fillMaxWidth(),contentAlignment=Alignment.Center) {
                Text(if(entries.isEmpty()) "发送或保存文件后，在这里查看进度" else "当前筛选没有任务",color=MaterialTheme.colorScheme.onSurfaceVariant)
            }
            else LazyColumn(Modifier.weight(1f).fillMaxWidth(),contentPadding=PaddingValues(top=12.dp,bottom=24.dp),verticalArrangement=Arrangement.spacedBy(12.dp)) {
                items(visible,key={it.id}) { task ->
                    Surface(modifier=Modifier.fillMaxWidth().animateItem(fadeInSpec=tween(140),fadeOutSpec=tween(100),placementSpec=tween(220)),
                        shape=MaterialTheme.shapes.medium,color=MaterialTheme.colorScheme.surface,
                        border=BorderStroke(1.dp,if(task.failed) MaterialTheme.colorScheme.error.copy(alpha=.4f) else MaterialTheme.colorScheme.outlineVariant)) {
                        Column(Modifier.animateContentSize(tween(180)).padding(14.dp),verticalArrangement=Arrangement.spacedBy(8.dp)) {
                            Text(task.direction,style=MaterialTheme.typography.labelSmall,color=MaterialTheme.colorScheme.onSurfaceVariant)
                            Text(task.name,maxLines=2,overflow=TextOverflow.Ellipsis,style=MaterialTheme.typography.titleSmall)
                            Text(task.state,style=MaterialTheme.typography.labelMedium,
                                color=if(task.failed) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.primary,
                                modifier=Modifier.semantics { liveRegion=LiveRegionMode.Polite })
                            if(task.total>0) {
                                key(task.started) {
                                    FlowProgressIndicator(progress=task.done.toFloat()/task.total,modifier=Modifier.fillMaxWidth(),running=task.running)
                                }
                                Text("${bytesLabel(task.done)} / ${bytesLabel(task.total)}${if(task.running) " · ${bytesLabel(task.speed)}/s" else ""}",
                                    style=MaterialTheme.typography.labelSmall,color=MaterialTheme.colorScheme.onSurfaceVariant)
                            } else if(task.running) LinearProgressIndicator(Modifier.fillMaxWidth())
                            if(task.error.isNotEmpty()) Text(task.error,style=MaterialTheme.typography.bodySmall,color=MaterialTheme.colorScheme.error)
                            if(task.savedFolder.isNotEmpty()) {
                                Text("内部存储 / ${task.savedFolder}",style=MaterialTheme.typography.bodySmall,color=MaterialTheme.colorScheme.onSurfaceVariant)
                                OutlinedButton(onClick={ReceivedFiles.open(context,task.savedFolder,settings.fileManagerPackage)}) { Text("打开保存位置") }
                            }
                            Row(Modifier.fillMaxWidth(),horizontalArrangement=Arrangement.spacedBy(8.dp)) {
                                if(task.running && TransferTasks.canPause(task.id)) OutlinedButton(enabled=!task.stopping,onClick={TransferTasks.pause(task.id)}) { Text("暂停") }
                                if(task.paused && !task.running) OutlinedButton(enabled=!task.stopping,onClick={TransferTasks.resume(task.id)}) { Text("继续（从头传输）") }
                                if(task.failed && TransferTasks.canRetry(task.id)) OutlinedButton(onClick={TransferTasks.retry(task.id)}) { Text("重新传输") }
                                if((task.running || task.paused) && TransferTasks.canCancel(task.id)) TextButton(enabled=!task.stopping,onClick={TransferTasks.cancel(task.id)}) { Text("取消") }
                            }
                            if(task.running && !TransferTasks.canCancel(task.id)) Text("文字已提交，等待回执期间无法撤回。",style=MaterialTheme.typography.labelSmall,color=MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                    }
                }
            }
            Text("仅保留本次运行记录；重传前请检查对端是否已收到。",modifier=Modifier.padding(vertical=10.dp),style=MaterialTheme.typography.labelSmall,color=MaterialTheme.colorScheme.onSurfaceVariant)
        }
        }
    }
    if(folderPicker) AlertDialog(onDismissRequest={folderPicker=false},title={Text("接收文件保存位置")},confirmButton={TextButton(onClick={folderPicker=false}){Text("关闭")}},text={
        Column {
            Text("按文件类型保存到内部存储下的流转目录。空目录或定位功能由文件管理器处理。",style=MaterialTheme.typography.bodySmall)
            ReceivedFiles.folders.forEach { folder -> TextButton(onClick={ReceivedFiles.open(context,folder,settings.fileManagerPackage)}) {Text(folder)} }
        }
    })
    if(managerPicker) AlertDialog(onDismissRequest={managerPicker=false},title={Text("打开目录的文件管理器")},confirmButton={TextButton(onClick={managerPicker=false}){Text("关闭")}},text={
        LazyColumn(Modifier.heightIn(max=400.dp)) {
            item { Text("选择支持目录定位的应用。系统文件打开浏览选择器，不会修改保存位置或获取新授权。",style=MaterialTheme.typography.bodySmall) }
            item { TextButton(onClick={selectManager("")}) { Text(if(settings.fileManagerPackage.isBlank()) "✓ 系统文件（默认）" else "系统文件（默认）") } }
            items(managers,key={it.packageName}) { manager ->
                TextButton(onClick={selectManager(manager.packageName)}) {
                    Text((if(settings.fileManagerPackage==manager.packageName) "✓ " else "")+manager.label)
                }
            }
            if(settings.fileManagerPackage.isNotBlank() && managers.none{it.packageName==settings.fileManagerPackage}) item { Text("已选应用暂不可用，可切回系统文件。",style=MaterialTheme.typography.bodySmall) }
        }
    })
}
private fun bytesLabel(n:Long)=when { n>=1048576->"%.1f MB".format(n/1048576.0);n>=1024->"%.1f KB".format(n/1024.0);else->"$n B" }
