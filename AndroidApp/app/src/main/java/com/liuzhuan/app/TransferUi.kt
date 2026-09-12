package com.liuzhuan.app

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.util.LruCache
import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.unit.dp
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
    if(bitmap!=null) Image(bitmap!!.asImageBitmap(),contentDescription=label,modifier=Modifier.size(56.dp),contentScale=ContentScale.Crop)
    else Surface(Modifier.size(56.dp),color=MaterialTheme.colorScheme.surfaceVariant,shape=MaterialTheme.shapes.small) { Box(Modifier.padding(12.dp)) { Text("图片",style=MaterialTheme.typography.labelSmall) } }
}

@Composable fun TransferTaskDialog(onDismiss:()->Unit) {
    val entries by TransferTasks.tasks.collectAsState()
    AlertDialog(onDismissRequest=onDismiss,title={Text("收发任务")},confirmButton={TextButton(onClick=onDismiss){Text("关闭")}},text={
        Column {
            Text("本次运行记录 · 失败任务可重试",style=MaterialTheme.typography.bodySmall)
            if(entries.isEmpty()) Text("还没有任务，发送或保存文件后会显示在这里。",modifier=Modifier.padding(top=16.dp))
            LazyColumn(Modifier.heightIn(max=420.dp),verticalArrangement=Arrangement.spacedBy(16.dp)) {
                items(entries,key={it.id}) { task ->
                    Column {
                        Text(task.name,maxLines=2,style=MaterialTheme.typography.titleSmall)
                        Text("${task.direction} · ${task.state}",style=MaterialTheme.typography.bodySmall,color=MaterialTheme.colorScheme.primary)
                        if(task.total>0) {
                            LinearProgressIndicator(progress={(task.done.toFloat()/task.total).coerceIn(0f,1f)},modifier=Modifier.fillMaxWidth().padding(vertical=6.dp))
                            Text("${bytesLabel(task.done)} / ${bytesLabel(task.total)} · ${bytesLabel(task.speed)}/s",style=MaterialTheme.typography.labelSmall)
                        } else if(task.running) LinearProgressIndicator(Modifier.fillMaxWidth())
                        if(task.error.isNotEmpty()) Text(task.error,style=MaterialTheme.typography.bodySmall,color=MaterialTheme.colorScheme.error)
                        if(task.failed && TransferTasks.canRetry(task.id)) TextButton(onClick={TransferTasks.retry(task.id)}){Text("重试")}
                    }
                }
            }
        }
    })
}
private fun bytesLabel(n:Long)=when { n>=1048576->"%.1f MB".format(n/1048576.0);n>=1024->"%.1f KB".format(n/1024.0);else->"$n B" }
