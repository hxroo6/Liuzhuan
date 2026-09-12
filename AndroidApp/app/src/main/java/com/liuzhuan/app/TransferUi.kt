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
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import okhttp3.OkHttpClient
import okhttp3.Request
import java.util.concurrent.TimeUnit

private val thumbnails=object:LruCache<String,Bitmap>(8*1024*1024) { override fun sizeOf(key:String,value:Bitmap)=value.byteCount }
private val thumbnailSlots=Semaphore(3)
private val thumbnailHttp=OkHttpClient.Builder().connectTimeout(5,TimeUnit.SECONDS).readTimeout(10,TimeUnit.SECONDS).build()

@Composable fun PhotoThumbnail(url:String?, label:String) {
    // A recycled row must never briefly display another item's previously loaded bitmap.
    key(url) { ThumbnailContent(url, label) }
}

@Composable private fun ThumbnailContent(url:String?, label:String) {
    val bitmap by produceState<Bitmap?>(null,url) {
        value=null
        if(url==null) return@produceState
        value=thumbnails.get(url) ?: withContext(Dispatchers.IO) {
            thumbnailSlots.withPermit {
                try {
                    thumbnailHttp.newCall(Request.Builder().url(url).build()).execute().use { response ->
                        if(!response.isSuccessful) return@use null
                        val body=response.body ?: return@use null
                        if(body.contentLength()>2*1024*1024) return@use null
                        val bytes=body.byteStream().use { it.readNBytes(2*1024*1024+1) }
                        if(bytes.size>2*1024*1024) return@use null
                        val bounds=BitmapFactory.Options().apply { inJustDecodeBounds=true }
                        BitmapFactory.decodeByteArray(bytes,0,bytes.size,bounds)
                        if(bounds.outWidth<=0 || bounds.outHeight<=0) return@use null
                        val options=BitmapFactory.Options().apply { inSampleSize=1; while(bounds.outWidth/inSampleSize>512 || bounds.outHeight/inSampleSize>512) inSampleSize*=2 }
                        BitmapFactory.decodeByteArray(bytes,0,bytes.size,options)?.also { thumbnails.put(url,it) }
                    }
                } catch(_:Exception) { null }
            }
        }
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
    AlertDialog(onDismissRequest=onDismiss,title={Text("收发任务")},confirmButton={TextButton(onClick=onDismiss){Text("关闭")}},text={
        Column {
            Text(if (entries.isEmpty()) "本次运行记录" else "${entries.count { it.running }} 项进行中 · ${entries.count { it.failed }} 项待处理",
                style=MaterialTheme.typography.bodySmall,color=MaterialTheme.colorScheme.onSurfaceVariant)
            if(entries.isEmpty()) Text("还没有收发任务\n发送或保存文件后，在这里查看进度。",modifier=Modifier.padding(top=20.dp,bottom=8.dp),style=MaterialTheme.typography.bodyMedium)
            LazyColumn(Modifier.heightIn(max=420.dp),contentPadding=PaddingValues(top=16.dp,bottom=4.dp),verticalArrangement=Arrangement.spacedBy(12.dp)) {
                items(entries,key={it.id}) { task ->
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
                            if(task.failed && TransferTasks.canRetry(task.id)) OutlinedButton(onClick={TransferTasks.retry(task.id)},modifier=Modifier.align(Alignment.End)) { Text("重新传输") }
                        }
                    }
                }
            }
            if(entries.isNotEmpty()) Text("仅保留本次运行记录，重试会重新发送全部内容。",modifier=Modifier.padding(top=8.dp),style=MaterialTheme.typography.labelSmall,color=MaterialTheme.colorScheme.onSurfaceVariant)
        }
    })
}
private fun bytesLabel(n:Long)=when { n>=1048576->"%.1f MB".format(n/1048576.0);n>=1024->"%.1f KB".format(n/1024.0);else->"$n B" }
