package com.liuzhuan.app

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.repeatOnLifecycle
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.liuzhuan.app.data.MaterialItem
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import org.json.JSONArray
import org.json.JSONObject
import java.util.UUID

/** Explicit project operations use the existing authenticated library socket and reliable download path. */
@Composable fun LibraryProjectPackageControls(selected:List<MaterialItem>,epoch:String,connected:Boolean) {
    var open by remember(epoch){mutableStateOf(false)}
    var busy by remember(epoch){mutableStateOf(false)}
    var jobId by remember(epoch){mutableStateOf("")}
    var status by remember(epoch){mutableStateOf<JSONObject?>(null)}
    var error by remember(epoch){mutableStateOf("")}
    var downloadedJob by remember(epoch){mutableStateOf("")}
    val scope=rememberCoroutineScope()
    val library by MaterialLibrary.state.collectAsStateWithLifecycle()
    val selectionIds=selected.map {it.id}.toSet()
    val hasDrafts=library.epoch==epoch&&library.drafts.any {it.id in selectionIds}
    val lifecycle=LocalLifecycleOwner.current.lifecycle
    fun client():com.liuzhuan.app.net.LanClient {
        check(connected&&MaterialLibrary.state.value.connected&&MaterialLibrary.state.value.epoch==epoch){"请连接原电脑和原素材库后操作"}
        val c=LanHub.client?:kotlin.error("电脑连接已变化")
        check(c.projectPackageSupported){"电脑端暂不支持手机项目包，请更新电脑端后重连"}
        return c
    }
    suspend fun request(value:JSONObject):JSONObject {
        val parts=epoch.split(':');check(parts.size>=3){"请更新两端并重新连接"}
        val c=client();value.put("expectedDeviceId",parts[0]).put("libraryId",parts[1])
        val reply=c.library(value)
        check(MaterialLibrary.state.value.epoch==epoch&&LanHub.client===c){"电脑会话已变化，电脑任务可能仍在继续，请重新连接后核对"}
        return reply
    }
    fun download(reply:JSONObject) {
        val id=reply.optJSONObject("result")?.optString("itemId").orEmpty()
        check(id.isNotBlank()){"电脑未返回项目包素材，请在电脑检查"}
        client().requestItem(id);downloadedJob=reply.optString("jobId")
    }
    fun begin(operation:String,token:String="") {
        if(busy)return
        val id=UUID.randomUUID().toString()
        val selectedIds=selected.map {it.id};val preview=selected.singleOrNull()
        busy=true;jobId=id;error="";status=JSONObject().put("state","queued").put("stage","正在提交到电脑").put("operation",operation).put("jobId",id)
        scope.launch {
            try {
                val payload=JSONObject().put("action","project_begin").put("operation",operation).put("operationId",id)
                if(operation=="export")check(MaterialLibrary.state.value.drafts.none {it.id in selectedIds}){"所选素材还有本机待同步整理，请先同步再打包"}
                when(operation){"export"->payload.put("ids",JSONArray(selectedIds));"preview"->payload.put("itemId",preview?.id?:kotlin.error("请选择一个ZIP项目包"));"apply"->payload.put("token",token)}
                val reply=request(payload);status=reply
                if(reply.optString("error").isNotBlank())error=reply.optString("error")
            }catch(ex:CancellationException){throw ex}
            catch(ex:Exception){error=ex.message?:"项目请求未确认，可恢复查询；不要重复提交"}
            finally {busy=false}
        }
    }
    LaunchedEffect(open,jobId,epoch,busy,connected) {
        if(!open||jobId.isBlank()||busy||!connected)return@LaunchedEffect
        lifecycle.repeatOnLifecycle(Lifecycle.State.STARTED) {
            while(open&&jobId.isNotBlank()) {
                try {
                    val reply=request(JSONObject().put("action","project_status").put("jobId",jobId))
                    if(reply.optString("error").isNotBlank()) {
                        error=reply.optString("error")
                        if(!error.contains("正在处理素材库请求")){status=reply;break}
                        delay(1000);continue
                    }
                    status=reply
                    error=""
                    if(reply.optString("state") in listOf("ready","failed")) {
                        if(reply.optString("state")=="ready"&&reply.optString("operation")=="export"&&downloadedJob!=jobId)download(reply)
                        break
                    }
                }catch(ex:CancellationException){throw ex}
                catch(ex:Exception){error=ex.message?:"项目进度未能读取，请保持原电脑连接"}
                delay(1000)
            }
        }
    }
    TextButton(enabled=connected,onClick={open=true}){Text("项目素材包")}
    if(open) {
        val running=busy||status?.optString("state") in listOf("queued","running")
        val previewZip=selected.singleOrNull()?.takeIf {it.hasFile&&it.name.endsWith(".zip",ignoreCase=true)}
        val readyPreview=status?.takeIf {it.optString("state")=="ready"&&it.optString("operation")=="preview"}?.optJSONObject("result")
        AlertDialog(onDismissRequest={open=false},title={Text("项目素材包")},text={Column(Modifier.heightIn(max=460.dp).verticalScroll(rememberScrollState()),verticalArrangement=Arrangement.spacedBy(10.dp)) {
            Text("已选择 ${selected.size} 项。项目包来自当前电脑已保存的素材库，包含原件、文字和电脑已确认的整理信息，不带连接口令、电脑身份或设置。",style=MaterialTheme.typography.bodySmall)
            Text("下载的项目ZIP可带到另一台电脑，用电脑“项目素材包”窗口合入。手机导入先将ZIP发送到电脑，再在素材库选中该ZIP校验。",style=MaterialTheme.typography.bodySmall)
            if(hasDrafts)Text("所选素材还有手机待同步整理，请先在“待同步”中完成同步后再打包。",color=MaterialTheme.colorScheme.error,style=MaterialTheme.typography.bodySmall)
            OutlinedButton(enabled=connected&&!running&&!hasDrafts&&selected.size in 1..100,onClick={begin("export")}){Text("确认打包所选并下载")}
            OutlinedButton(enabled=connected&&!running&&previewZip!=null,onClick={begin("preview")}){Text("校验并预览选中的项目ZIP")}
            status?.let {job->
                Text(job.optString("stage"),style=MaterialTheme.typography.titleSmall)
                if(running)LinearProgressIndicator(Modifier.fillMaxWidth())
                if(job.optString("state")=="ready"&&job.optString("operation")=="export") {
                    Text("项目包已保存到电脑，下载进度请查看收发任务。",style=MaterialTheme.typography.bodySmall)
                    TextButton(enabled=connected,onClick={try {download(job)}catch(ex:Exception){error=ex.message?:"请重新连接原电脑后下载"}}){Text("再次下载项目包")}
                }
                if(job.optString("state")=="ready"&&job.optString("operation")=="apply") {
                    val result=job.optJSONObject("result")
                    Text("已保存：新增 ${result?.optInt("added")?:0} 项，同包跳过 ${result?.optInt("already")?:0} 项，同文保留 ${result?.optInt("duplicateText")?:0} 项。",style=MaterialTheme.typography.bodySmall)
                }
            }
            readyPreview?.let {result->
                Text("${result.optInt("count")} 项 · ${result.optInt("files")} 个原件；将新增 ${result.optInt("added")} 项，同包跳过 ${result.optInt("already")} 项，同文保留 ${result.optInt("duplicateText")} 项。",style=MaterialTheme.typography.bodyMedium)
                result.optJSONArray("names")?.let {names->for(index in 0 until names.length())Text("• ${names.optString(index)}",style=MaterialTheme.typography.bodySmall)}
                Text("确认后合入当前电脑素材库，保留已有整理；库在预览后变化需重新预览。",style=MaterialTheme.typography.bodySmall)
            }
            if(error.isNotBlank())Text(error,color=MaterialTheme.colorScheme.error,style=MaterialTheme.typography.bodySmall)
            Text("关闭面板或进入后台即停止进度查询，电脑继续处理；准备文件会保留供核对。任务进度在本次会话内保留15分钟。",style=MaterialTheme.typography.bodySmall)
        }},confirmButton={
            if(readyPreview!=null)TextButton(enabled=connected&&!running,onClick={begin("apply",readyPreview.optString("token"))}){Text("确认合入当前电脑")}
            else TextButton(onClick={open=false}){Text("完成")}
        },dismissButton={TextButton(onClick={open=false}){Text("关闭")}})
    }
}
