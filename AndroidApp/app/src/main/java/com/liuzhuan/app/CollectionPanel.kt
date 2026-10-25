package com.liuzhuan.app

import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.liuzhuan.app.core.SettingsStore
import com.liuzhuan.app.net.LanClient
import kotlinx.coroutines.*
import org.json.JSONArray
import org.json.JSONObject

private data class RuleDraft(val id:String=java.util.UUID.randomUUID().toString(),val name:String="新规则",val enabled:Boolean=true,val type:String="All",val source:String="All",val contains:String="",val group:String="待整理",val tags:String="",val device:String="") {
    fun json()=JSONObject().put("id",id).put("name",name).put("enabled",enabled).put("type",type).put("source",source).put("contains",contains).put("group",group).put("device",device).put("tags",JSONArray(tags.split(',', '，',';', '；').map { it.trim() }.filter { it.isNotBlank() }))
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
internal fun CollectionPanel(store: SettingsStore, openSend:()->Unit, dismiss:()->Unit) {
    val context=LocalContext.current;val scope=rememberCoroutineScope()
    val connection by LanController.state.collectAsStateWithLifecycle()
    val subscriptions by FolderSubscriptions.state.collectAsStateWithLifecycle()
    var tab by rememberSaveable { mutableStateOf(0) }
    var rules by remember { mutableStateOf<List<RuleDraft>>(emptyList()) }
    var revision by remember { mutableStateOf(0L) }
    var loadedTarget by remember { mutableStateOf<String?>(null) }
    var loaded by remember { mutableStateOf(false) }
    var busy by remember { mutableStateOf(false) }
    var selected by remember { mutableStateOf<String?>(null) }
    var status by remember { mutableStateOf("连接电脑后读取共用规则。") }
    var quickText by rememberSaveable { mutableStateOf("") }
    fun load(document:JSONObject,target:String?) {
        revision=document.getLong("revision")
        val arr=document.getJSONArray("rules")
        rules=(0 until arr.length()).map { i -> val r=arr.getJSONObject(i);val tags=r.optJSONArray("tags")?:JSONArray(); RuleDraft(r.getString("id"),r.getString("name"),r.optBoolean("enabled",true),r.optString("type","All"),r.optString("source","All"),r.optString("contains"),r.optString("group"),(0 until tags.length()).joinToString(", "){tags.getString(it)},r.optString("device")) }
        loaded=true;loadedTarget=target;selected=rules.firstOrNull()?.id
    }
    fun document()=JSONObject().put("version",1).put("revision",revision).put("rules",JSONArray().also { arr->rules.forEach { arr.put(it.json()) } })
    fun request(action:String) {
        if(busy)return
        busy=true
        scope.launch {
            try {
                val client=LanHub.client?:error("请先连接电脑")
                val target=client.collectionTarget()
                if(action!="get") check(loaded && target==loadedTarget) { "电脑已变化，请重新加载规则" }
                val reply=withContext(Dispatchers.IO) { client.automation(action,if(action=="get")null else document()) }
                if(action=="preview") {
                    val samples=reply.optJSONArray("samples")?:JSONArray()
                    status="预览：最近 50 条有 ${reply.optInt("matched")} 条匹配，未修改素材。\n"+(0 until samples.length()).joinToString("\n") { i -> val s=samples.getJSONObject(i);"${s.optString("name")} → ${s.optString("group")}" }
                } else {load(reply.getJSONObject("document"),target);status=if(action=="save")"已保存，新收集的素材将按规则整理。" else "规则已加载；手机与电脑共用，保存只影响新素材。"}
            }catch(ex:CancellationException){throw ex}catch(ex:Exception){status=ex.message?:"操作失败，请重试"}finally{busy=false}
        }
    }
    LaunchedEffect(connection) { if(connection!=LanClient.State.Connected) {loaded=false;loadedTarget=null;rules=emptyList();selected=null;status="连接电脑后读取共用规则。"} else request("get") }
    val treePicker=rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri ->
        if(uri!=null) scope.launch {
            busy=true
            try {
                context.contentResolver.takePersistableUriPermission(uri,Intent.FLAG_GRANT_READ_URI_PERMISSION)
                FolderSubscriptions.add(uri);status="来源订阅已保存；接收文件仍统一保存到 Download/Liuzhuan。"
            }catch(ex:CancellationException){throw ex}catch(ex:Exception){
                if(subscriptions.entries.none { it.uri==uri.toString() }) runCatching {context.contentResolver.releasePersistableUriPermission(uri,Intent.FLAG_GRANT_READ_URI_PERMISSION)}
                status=ex.message?:"无法订阅此目录"
            }finally{busy=false}
        }
    }
    val filePicker=rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if(uri!=null) {
            runCatching {context.contentResolver.takePersistableUriPermission(uri,Intent.FLAG_GRANT_READ_URI_PERMISSION)}
            uploadFileStreaming(context,uri,store,{_,_->},{_,msg->toast(context,msg)})
            status="已加入收发任务，可在收发中查看进度。"
        }
    }
    SecondaryPanel(dismiss) {
        Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(horizontal=20.dp).padding(bottom=32.dp),verticalArrangement=Arrangement.spacedBy(14.dp)) {
            Row(Modifier.fillMaxWidth(),horizontalArrangement=Arrangement.SpaceBetween) {Text("收集与整理",style=MaterialTheme.typography.headlineSmall);TextButton(onClick=dismiss){Text("完成")}}
            Text("收进来，自然有序",style=MaterialTheme.typography.bodyMedium,color=MaterialTheme.colorScheme.onSurfaceVariant)
            Row(horizontalArrangement=Arrangement.spacedBy(8.dp)) {listOf("自动整理","目录订阅","快捷收集").forEachIndexed { i,title->FilterChip(selected=tab==i,onClick={tab=i},label={Text(title)})}}
            if(busy || subscriptions.checking) LinearProgressIndicator(Modifier.fillMaxWidth())
            when(tab) {
                0 -> {
                    Text("共用电脑的规则，分组和标签也会同步到手机。不会更改原文件或覆盖已有分组。",color=MaterialTheme.colorScheme.onSurfaceVariant)
                    FlowRow(horizontalArrangement=Arrangement.spacedBy(8.dp)) {
                        OutlinedButton(onClick={request("get")},enabled=!busy){Text("重新加载")}
                        OutlinedButton(onClick={if(rules.size<30){val rule=RuleDraft();rules=rules+rule;selected=rule.id}},enabled=loaded&&!busy&&rules.size<30){Text("＋ 新规则")}
                    }
                    FlowRow(horizontalArrangement=Arrangement.spacedBy(8.dp)) { rules.forEach { rule->FilterChip(selected=selected==rule.id,onClick={selected=rule.id},label={Text(rule.name.ifBlank{"未命名"})}) } }
                    rules.firstOrNull { it.id==selected }?.let { rule ->
                        fun update(next:RuleDraft) { rules=rules.map { if(it.id==rule.id)next else it } }
                        Card(colors=CardDefaults.cardColors(containerColor=MaterialTheme.colorScheme.surface)) {
                            Column(Modifier.padding(16.dp),verticalArrangement=Arrangement.spacedBy(12.dp)) {
                                OutlinedTextField(rule.name,{if(it.length<=48)update(rule.copy(name=it))},label={Text("规则名称")},singleLine=true,modifier=Modifier.fillMaxWidth(),enabled=!busy)
                                Row(Modifier.fillMaxWidth(),horizontalArrangement=Arrangement.SpaceBetween){Text("启用规则");Switch(rule.enabled,{update(rule.copy(enabled=it))},enabled=!busy)}
                                Text("素材类型",style=MaterialTheme.typography.labelLarge)
                                FlowRow(horizontalArrangement=Arrangement.spacedBy(6.dp)) {listOf("All" to "全部","Image" to "图片","Text" to "文字","Video" to "视频","Audio" to "音频","Other" to "其他").forEach { (v,l)->FilterChip(rule.type==v,{update(rule.copy(type=v))},label={Text(l)},enabled=!busy)} }
                                Text("收集来源",style=MaterialTheme.typography.labelLarge)
                                FlowRow(horizontalArrangement=Arrangement.spacedBy(6.dp)) {listOf("All" to "全部","Desktop" to "电脑","Android" to "手机","Directory" to "目录订阅").forEach { (v,l)->FilterChip(rule.source==v,{update(rule.copy(source=v))},label={Text(l)},enabled=!busy)} }
                                OutlinedTextField(rule.device,{if(it.length<=64)update(rule.copy(device=it))},label={Text("设备名称包含 · 留空为全部")},singleLine=true,modifier=Modifier.fillMaxWidth(),enabled=!busy)
                                OutlinedTextField(rule.contains,{if(it.length<=120)update(rule.copy(contains=it))},label={Text("名称包含 · 留空为全部")},singleLine=true,modifier=Modifier.fillMaxWidth(),enabled=!busy)
                                OutlinedTextField(rule.group,{if(it.length<=48)update(rule.copy(group=it))},label={Text("分组 · 仅填入未分组素材")},singleLine=true,modifier=Modifier.fillMaxWidth(),enabled=!busy)
                                OutlinedTextField(rule.tags,{if(it.length<=1000)update(rule.copy(tags=it))},label={Text("追加标签 · 用逗号分隔")},modifier=Modifier.fillMaxWidth(),enabled=!busy)
                                TextButton(onClick={rules=rules.filterNot {it.id==rule.id};selected=rules.firstOrNull()?.id},enabled=!busy){Text("移除此规则")}
                            }
                        }
                    }
                    Row(horizontalArrangement=Arrangement.spacedBy(10.dp)) {OutlinedButton(onClick={request("preview")},enabled=loaded&&!busy){Text("预览最近 50 条")};Button(onClick={request("save")},enabled=loaded&&!busy){Text("保存全部规则")}}
                }
                1 -> {
                    Text("订阅来源文件夹",style=MaterialTheme.typography.titleLarge)
                    Text("首次跳过已有文件，不递归子目录。目录授权只用于收集来源；接收路径仍为 Download/Liuzhuan。",color=MaterialTheme.colorScheme.onSurfaceVariant)
                    Text("仅向订阅时连接的电脑发送。更换电脑需停止旧订阅后重新添加。部分文件管理器不提供变化通知，回到流转或点检查新文件即可补查。",style=MaterialTheme.typography.bodySmall,color=MaterialTheme.colorScheme.onSurfaceVariant)
                    Row(horizontalArrangement=Arrangement.spacedBy(8.dp)) {
                        Button(onClick={treePicker.launch(null)},enabled=connection==LanClient.State.Connected&&!busy&&!subscriptions.unavailable){Text("＋ 授权来源目录")}
                        OutlinedButton(onClick={FolderSubscriptions.scanNow()},enabled=!busy){Text("检查新文件")}
                    }
                    subscriptions.entries.forEach { entry ->
                        Card {Column(Modifier.padding(16.dp),verticalArrangement=Arrangement.spacedBy(8.dp)) {
                            Text(entry.name,style=MaterialTheme.typography.titleMedium)
                            Text(if(LanHub.client?.collectionTarget()==entry.target)"绑定当前电脑" else "等待原电脑连接",color=MaterialTheme.colorScheme.onSurfaceVariant)
                            TextButton(onClick={scope.launch {try {FolderSubscriptions.remove(entry.id);runCatching {context.contentResolver.releasePersistableUriPermission(android.net.Uri.parse(entry.uri),Intent.FLAG_GRANT_READ_URI_PERMISSION)};status="已停止订阅，文件均保留。"}catch(ex:Exception){if(ex is CancellationException)throw ex;status=ex.message?:"停止失败"}}},enabled=!busy){Text("停止订阅")}
                        }}
                    }
                    Text(subscriptions.status,color=MaterialTheme.colorScheme.primary)
                }
                2 -> {
                    Text("灵感到来，随手收下",style=MaterialTheme.typography.titleLarge)
                    Text("在前台读取剪贴板并预览，确认后发给电脑。也可以从其他应用的系统分享菜单发送。",color=MaterialTheme.colorScheme.onSurfaceVariant)
                    OutlinedButton(onClick={
                        val clip=(context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager).primaryClip
                        quickText=if(clip!=null&&clip.itemCount>0) clip.getItemAt(0).coerceToText(context)?.toString().orEmpty().take(100000) else ""
                        status=if(quickText.isBlank())"剪贴板没有可收集的文字。" else "已读取，请确认内容后发送。"
                    }){Text("读取剪贴板文字")}
                    OutlinedTextField(quickText,{if(it.length<=100000)quickText=it},label={Text("待收集文字")},modifier=Modifier.fillMaxWidth(),minLines=3,maxLines=7)
                    Button(onClick={if(LanHub.client?.sendText(quickText)==true){quickText="";status="已加入发送任务，可在收发中查看电脑回执。"}else status="发送失败，请检查电脑连接。"},enabled=connection==LanClient.State.Connected&&quickText.isNotBlank()){Text("收集到电脑")}
                    OutlinedButton(onClick={filePicker.launch(arrayOf("*/*"))},enabled=connection==LanClient.State.Connected){Text("选择文件收集")}
                    TextButton(onClick=openSend){Text("打开完整发送页面")}
                }
            }
            Text(status,color=MaterialTheme.colorScheme.primary,style=MaterialTheme.typography.bodySmall)
        }
    }
}
