package com.liuzhuan.app

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.coroutines.launch
import kotlinx.coroutines.ensureActive

/** Local-only controls. Never deletes downloads, pending task sources, receipts, credentials or PC files. */
@Composable fun StoragePanel(onDismiss:()->Unit) {
    val context=LocalContext.current
    val scope=rememberCoroutineScope()
    val tasks by TransferTasks.tasks.collectAsStateWithLifecycle()
    var days by remember {mutableIntStateOf(context.getSharedPreferences("storage-retention",0).getInt("days",30).takeIf {it in listOf(0,7,30,90)}?:30)}
    var summary by remember {mutableStateOf("点击统计本机占用；不会自动删除文件。")}
    var busy by remember {mutableStateOf(false)}
    var preview by remember {mutableStateOf<List<TransferTask>?>(null)}
    var clearImages by remember {mutableStateOf(false)}
    var cacheBytes by remember {mutableIntStateOf(thumbnailCacheBytes())}
    fun size(n:Long)=if(n>=1048576)"%.1f MB".format(n/1048576.0) else "$n B"
    fun inspect() {if(busy)return;busy=true;scope.launch {
        try {summary=withContext(Dispatchers.IO) {
            val operationContext=kotlinx.coroutines.currentCoroutineContext()
            fun measure(root:java.io.File):Long {
                if(!root.exists())return 0
                var count=0;var bytes=0L;val base=root.canonicalPath+java.io.File.separator
                root.walkTopDown().onEnter {it==root || it.canonicalPath.startsWith(base)&&!java.nio.file.Files.isSymbolicLink(it.toPath())}.forEach {
                    operationContext.ensureActive()
                    if(++count>10000)error("统计达到1万文件上限，请在系统存储页面检查。")
                    if(it.isFile&&!java.nio.file.Files.isSymbolicLink(it.toPath()))bytes+=it.length()
                };return bytes
            }
            val cache=measure(context.cacheDir);val recovery=measure(java.io.File(context.noBackupFilesDir,"recovery"))
            "应用缓存 ${size(cache)}（可能含任务来源，保护）\n加密任务/回执 ${size(recovery)}（保护）\n接收文件固定保存到 ${ReceivedFiles.FOLDER}，保留原文件；电脑容量请在电脑设置中查看。"
        };cacheBytes=thumbnailCacheBytes()}
        catch(ex:kotlinx.coroutines.CancellationException){throw ex}
        catch(ex:Exception){summary=ex.message?:"无法完整读取占用，未修改文件。"}finally{busy=false}
    }}
    SecondaryPanel(onDismiss=onDismiss) {
        Column(Modifier.verticalScroll(rememberScrollState()).padding(22.dp),verticalArrangement=Arrangement.spacedBy(14.dp)) {
            Text("容量与历史",style=MaterialTheme.typography.headlineMedium)
            Text(summary,style=MaterialTheme.typography.bodyMedium)
            Button(enabled=!busy,onClick={inspect()}){Text(if(busy)"正在统计…" else "统计本机占用")}
            Text("缩略图内存缓存 ${size(cacheBytes.toLong())} / 8 MB。清空后可按需重新下载；当前显示的图片可能仍被界面持有。",style=MaterialTheme.typography.bodySmall)
            OutlinedButton(onClick={clearImages=true}){Text("清空缩略图缓存…")}
            HorizontalDivider()
            Text("已结束任务的历史保留",style=MaterialTheme.typography.titleMedium)
            Row(horizontalArrangement=Arrangement.spacedBy(6.dp)) {
                listOf(7,30,90,0).forEach {value->FilterChip(selected=days==value,onClick={days=value},label={Text(if(value==0)"全部" else "$value 天")})}
            }
            Text("当前 ${tasks.size} 条记录。只清理已完成或已取消的展示历史；暂停、重试、待发、续传与登记回执始终保留。",style=MaterialTheme.typography.bodySmall)
            Button(onClick={scope.launch {
                val ok=withContext(Dispatchers.IO){context.getSharedPreferences("storage-retention",0).edit().putInt("days",days).commit()}
                if(ok)preview=TransferTasks.historyCandidates(days) else summary="保留策略未保存，请重试。"
            }}){Text("记住策略并预览历史")}
            Text("不会定时自动清理，也不会删除已接收文件或电脑素材。",style=MaterialTheme.typography.bodySmall)
            TextButton(onClick=onDismiss){Text("返回收发")}
        }
    }
    preview?.let {rows->AlertDialog(onDismissRequest={preview=null},title={Text("清理预览 · ${rows.size} 条")},
        text={Text("仅移除以下已结束展示记录，原文件和恢复数据保留：\n"+rows.take(8).joinToString("\n"){it.name}+if(rows.size>8)"\n…其余 ${rows.size-8} 条" else "")},
        confirmButton={TextButton(enabled=rows.isNotEmpty(),onClick={try{val n=TransferTasks.clearHistory(rows.map {it.id}.toSet());preview=null;summary="已移除 $n 条展示历史，原文件与恢复数据保留；历史在后台保存，失败会在日志提示。"}catch(ex:Exception){summary=ex.message?:"历史未清理";preview=null}}){Text("确认清理历史")}},
        dismissButton={TextButton(onClick={preview=null}){Text("取消")}})}
    if(clearImages)AlertDialog(onDismissRequest={clearImages=false},title={Text("清空缩略图缓存？")},text={Text("只清空可重新下载的内存缓存，不删除文件、索引或待发来源。")},
        confirmButton={TextButton(onClick={clearThumbnailCache();cacheBytes=thumbnailCacheBytes();clearImages=false}){Text("确认清空")}},dismissButton={TextButton(onClick={clearImages=false}){Text("取消")}})
}
