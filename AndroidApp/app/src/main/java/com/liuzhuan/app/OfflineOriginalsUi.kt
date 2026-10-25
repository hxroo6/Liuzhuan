package com.liuzhuan.app

import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.liuzhuan.app.core.OfflineOriginalCatalog
import com.liuzhuan.app.data.MaterialItem
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch

private fun originalBytes(n:Long)=if(n>=1024L*1024*1024)"%.1f GB".format(n/1073741824.0)else if(n>=1024*1024)"%.1f MB".format(n/1048576.0)else "${n/1024} KB"
private fun originalTarget(epoch:String)=epoch.split(':').take(2).joinToString(":")

/** Also usable on individual material cards; metadata favorites and private-file pins are independent. */
@Composable fun LibraryOfflineOriginalAction(item:MaterialItem,epoch:String,connected:Boolean) {
    if(!item.hasFile)return
    val originals by OfflineOriginals.state.collectAsStateWithLifecycle()
    val context=LocalContext.current;val scope=rememberCoroutineScope()
    val key=runCatching {OfflineOriginalCatalog.key(originalTarget(epoch),item.id,item.sourceRevision)}.getOrNull()?:return
    val pin=originals.pins.firstOrNull {it.key==key}
    var open by remember(key){mutableStateOf(false)};var busy by remember{mutableStateOf(false)};var error by remember{mutableStateOf("")}
    TextButton(enabled=originals.ready&&!originals.unavailable&&!busy,onClick={open=true;error=""}) {Text(if(pin?.state=="ready")"离线原件" else if(pin!=null)"固定待处理" else "保留原件")}
    if(open)AlertDialog(onDismissRequest={if(!busy)open=false},title={Text("离线原件 · ${item.name.take(60)}")},text={Column(verticalArrangement=Arrangement.spacedBy(8.dp)) {
        Text("主动固定后，通过现有可靠下载校验并保留独立私有副本。只保留这个版本，离线可打开；不会默认下载整库。",style=MaterialTheme.typography.bodySmall)
        Text("本机总配额 ${originalBytes(originals.reserved)} / ${originalBytes(originals.limit)}，含待下载预留。",style=MaterialTheme.typography.bodySmall)
        if(pin!=null)Text(when(pin.state){"ready"->"私有原件已校验保存 · ${originalBytes(pin.size)}";"error"->pin.error;else->"已登记固定请求；可在收发查看下载，失败或尚未开始时点击重试。"})
        if(pin!=null&&pin.sourceRun!=epoch.substringAfterLast(':'))Text("这是较早会话保存的历史快照，不代表电脑当前原件。需要新副本时请取消固定后重新下载。",style=MaterialTheme.typography.bodySmall,color=MaterialTheme.colorScheme.tertiary)
        Text("取消固定会停止本次固定下载并移除私有副本；已接收的 Downloads 文件保留。",style=MaterialTheme.typography.bodySmall)
        if(error.isNotBlank())Text(error,color=MaterialTheme.colorScheme.error)
        if(busy)LinearProgressIndicator(Modifier.fillMaxWidth())
        Row(Modifier.horizontalScroll(rememberScrollState())) {
            if(pin?.state=="ready")TextButton(enabled=!busy,onClick={busy=true;scope.launch{try{OfflineOriginals.open(context,key)}catch(ex:CancellationException){throw ex}catch(ex:Exception){error=ex.message?:"没有可打开此文件的应用"}finally{busy=false}}}){Text("校验并打开")}
            else TextButton(enabled=connected&&!busy,onClick={busy=true;scope.launch{try{if(pin==null)OfflineOriginals.pin(item,epoch)else OfflineOriginals.retry(key,epoch)}catch(ex:CancellationException){throw ex}catch(ex:Exception){error=ex.message?:"固定请求未完成"}finally{busy=false}}}){Text(if(pin==null)"固定此版本" else "重试下载")}
            if(pin!=null)TextButton(enabled=!busy,onClick={busy=true;scope.launch{try{OfflineOriginals.unpin(key);open=false}catch(ex:CancellationException){throw ex}catch(ex:Exception){error=ex.message?:"取消固定未完成"}finally{busy=false}}}){Text("取消固定")}
        }
    }},confirmButton={TextButton(enabled=!busy,onClick={open=false}){Text("关闭")}})
}

@Composable fun LibraryOfflineOriginalsButton(library:MaterialLibrary.State) {
    val state by OfflineOriginals.state.collectAsStateWithLifecycle();val context=LocalContext.current;val scope=rememberCoroutineScope()
    var show by remember{mutableStateOf(false)};var busy by remember{mutableStateOf(false)};var error by remember{mutableStateOf("")};var actionJob by remember{mutableStateOf<Job?>(null)}
    fun launch(block:suspend ()->Unit){busy=true;error="";actionJob=scope.launch{try{block()}catch(ex:CancellationException){throw ex}catch(ex:Exception){error=ex.message?:"操作未完成，原记录保留"}finally{busy=false}}}
    TextButton(onClick={show=true}){Text("离线原件 ${state.pins.count {it.state=="ready"}}")}
    if(show)AlertDialog(onDismissRequest={if(!busy)show=false},title={Text("本机离线原件")},text={Column(verticalArrangement=Arrangement.spacedBy(8.dp)) {
        Text("${originalBytes(state.reserved)} / ${originalBytes(state.limit)} · ${state.pins.size}/200 项，包含所有已配对电脑及待下载预留。",style=MaterialTheme.typography.bodySmall)
        Text("从素材的“整理 → 保留原件”明确选择文件。每份副本按电脑、素材库及来源版本隔离，断开连接仍可打开。",style=MaterialTheme.typography.bodySmall)
        if(state.unavailable)Text(state.error,color=MaterialTheme.colorScheme.error)
        if(error.isNotBlank())Text(error,color=MaterialTheme.colorScheme.error)
        if(busy) {LinearProgressIndicator(Modifier.fillMaxWidth());TextButton(onClick={actionJob?.cancel()}){Text("取消当前操作")}}
        Row(Modifier.horizontalScroll(rememberScrollState())) {listOf(128L,512L,1024L,2048L).forEach {mb->FilterChip(selected=state.limit==mb*1048576,enabled=!busy&&!state.unavailable,onClick={launch{OfflineOriginals.setLimit(mb*1048576)}},label={Text(if(mb>=1024)"${mb/1024} GB" else "$mb MB")})}}
        LazyColumn(Modifier.heightIn(max=360.dp)) {items(state.pins,key={it.key}) {pin->Column(Modifier.fillMaxWidth().padding(vertical=8.dp)) {
            Text(pin.name,maxLines=2,style=MaterialTheme.typography.titleSmall)
            Text("${pin.target.substringBefore(':').take(8)} / ${pin.target.substringAfter(':').take(8)} · ${originalBytes(pin.size)} · "+when(pin.state){"ready"->"已保留";"error"->"需重试";else->"待下载或收发中"},style=MaterialTheme.typography.bodySmall)
            if(pin.error.isNotBlank())Text(pin.error,color=MaterialTheme.colorScheme.error,style=MaterialTheme.typography.bodySmall)
            Row(Modifier.horizontalScroll(rememberScrollState())) {
                if(pin.state=="ready")TextButton(enabled=!busy,onClick={launch{OfflineOriginals.open(context,pin.key)}}){Text("校验并打开")}
                else TextButton(enabled=!busy&&library.connected&&originalTarget(library.epoch)==pin.target,onClick={launch{OfflineOriginals.retry(pin.key,library.epoch)}}){Text("重试下载")}
                TextButton(enabled=!busy,onClick={launch{OfflineOriginals.unpin(pin.key)}}){Text("取消固定")}
            }
        }}}
        if(state.pins.isEmpty())Text("尚未固定原件。素材元数据与文字离线索引可独立保存。",style=MaterialTheme.typography.bodySmall)
        if(state.orphanBytes>0)TextButton(enabled=!busy,onClick={launch{val n=OfflineOriginals.cleanOrphans();error="已清理 $n 份无固定关联的本模块私有副本"}}){Text("清理失联私有副本 ${originalBytes(state.orphanBytes)}")}
    }},confirmButton={TextButton(enabled=!busy,onClick={show=false}){Text("关闭")}})
}
