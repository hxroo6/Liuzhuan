package com.liuzhuan.app

import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle

@Composable fun TransferPolicyControls(modifier:Modifier=Modifier) {
    val policy by TransferPolicyStore.state.collectAsStateWithLifecycle()
    val error by TransferPolicyStore.error.collectAsStateWithLifecycle()
    val mic by com.liuzhuan.app.audio.MicrophoneState.status.collectAsStateWithLifecycle()
    val tasks by TransferTasks.tasks.collectAsStateWithLifecycle()
    var expanded by remember {mutableStateOf(false)}
    ElevatedCard(modifier.fillMaxWidth()) {Column(Modifier.padding(12.dp),verticalArrangement=Arrangement.spacedBy(6.dp)) {
        Text("传输调度",style=MaterialTheme.typography.titleSmall)
        val yielding=policy.yieldToAudio && mic.connected && mic.forwarding
        Text(TransferPolicyRuntime.summary(),style=MaterialTheme.typography.bodySmall)
        Text("正在执行 ${tasks.count {it.running && !it.state.startsWith("排队中")}} · 排队 ${tasks.count {it.running && it.state.startsWith("排队中")}}"+
            if(yielding)" · 麦克风期间上下行各不超过 1 MiB/s" else "",style=MaterialTheme.typography.bodySmall)
        TextButton(onClick={expanded=!expanded}){Text(if(expanded)"收起调度设置" else "调整优先级与限速")}
        if(expanded) {
            TransferChoice("排队优先",policy.priority,listOf("balanced" to "按加入顺序","upload" to "发送优先","download" to "接收优先")){value->TransferPolicyStore.change {it.copy(priority=value)}}
            Row(horizontalArrangement=Arrangement.spacedBy(8.dp)) {
                val rates=listOf(0,1,2,5,10,20,50,100).map {it.toString() to if(it==0)"不限速" else "$it MiB/s"}
                TransferChoice("发送",policy.uploadMiB.toString(),rates,Modifier.weight(1f)){value->TransferPolicyStore.change {it.copy(uploadMiB=value.toInt())}}
                TransferChoice("接收",policy.downloadMiB.toString(),rates,Modifier.weight(1f)){value->TransferPolicyStore.change {it.copy(downloadMiB=value.toInt())}}
            }
            Row(horizontalArrangement=Arrangement.SpaceBetween,modifier=Modifier.fillMaxWidth()) {
                Text("音频转发时为麦克风让路",modifier=Modifier.weight(1f),style=MaterialTheme.typography.bodyMedium)
                Switch(checked=policy.yieldToAudio,onCheckedChange={value->TransferPolicyStore.change {it.copy(yieldToAudio=value)}})
            }
            Text("保存后立即作用于后续数据块；优先级只调整等待任务，最多同时执行 2 项。两端同时限速时取较低速度。",style=MaterialTheme.typography.bodySmall)
        }
        if(error.isNotBlank())Text(error,color=MaterialTheme.colorScheme.error,style=MaterialTheme.typography.bodySmall)
    }}
}

@Composable private fun TransferChoice(label:String,value:String,choices:List<Pair<String,String>>,modifier:Modifier=Modifier,onChange:(String)->Unit) {
    var open by remember {mutableStateOf(false)}
    Box(modifier) {
        OutlinedButton(onClick={open=true}) {Text("$label · ${choices.firstOrNull {it.first==value}?.second?:value}")}
        DropdownMenu(expanded=open,onDismissRequest={open=false}) {choices.forEach {(key,title)->DropdownMenuItem(text={Text(title)},onClick={open=false;onChange(key)})}}
    }
}
