package com.liuzhuan.app

import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.compose.ui.text.input.TextFieldValue
import androidx.compose.ui.text.TextRange
import com.liuzhuan.app.data.MaterialItem
import com.liuzhuan.app.data.toMaterialItem
import com.liuzhuan.app.net.Proto
import kotlinx.coroutines.launch
import com.liuzhuan.app.core.LibraryMetadata
import com.liuzhuan.app.core.LibraryDraft
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.ui.platform.LocalContext

/** Available independently of the current workspace, including a sixth connected library. */
@Composable fun LibrarySavedWorkspacesButton(state:MaterialLibrary.State) {
    val scope=rememberCoroutineScope();var open by remember{mutableStateOf(false)}
    var entries by remember{mutableStateOf<List<MaterialLibrary.SavedWorkspace>>(emptyList())}
    var busy by remember{mutableStateOf(false)};var error by remember{mutableStateOf("")}
    var removing by remember{mutableStateOf<MaterialLibrary.SavedWorkspace?>(null)}
    suspend fun refresh() {entries=MaterialLibrary.savedWorkspaces()}
    LaunchedEffect(open) {if(open){busy=true;error="";try{refresh()}catch(ex:Exception){error=ex.message?:"离线库列表未能读取，原记录保留"}finally{busy=false}}}
    TextButton(onClick={open=true}){Text("离线库管理")}
    if(open)AlertDialog(onDismissRequest={if(!busy)open=false},title={Text("已保存的离线库 · ${entries.size}/5")},text={Column {
        Text("旧电脑离线也可查看与移除缓存；有待同步草稿时须先打开处理。不会删除电脑素材或手机已接收文件。",style=MaterialTheme.typography.bodySmall)
        if(error.isNotBlank())Text(error,color=MaterialTheme.colorScheme.error)
        if(busy)LinearProgressIndicator(Modifier.fillMaxWidth())
        LazyColumn(Modifier.heightIn(max=420.dp)) {items(entries,key={it.target}){entry->Column(Modifier.fillMaxWidth().padding(vertical=10.dp)) {
            Text("电脑 ${entry.target.substringBefore(':').take(8)} · 素材库 ${entry.target.substringAfter(':').take(8)}",style=MaterialTheme.typography.titleSmall)
            Text("${if(entry.complete)"完整索引" else "部分摘要"} ${entry.count} 项 · 待同步 ${entry.pending} 条",style=MaterialTheme.typography.bodySmall)
            if(entry.error.isNotBlank())Text("记录不可读取，停止移除：${entry.error}",color=MaterialTheme.colorScheme.error,style=MaterialTheme.typography.bodySmall)
            Row {
                TextButton(enabled=!busy&&!state.connected&&entry.error.isBlank(),onClick={busy=true;error="";scope.launch{try{MaterialLibrary.selectOfflineWorkspace(entry.target);open=false}catch(ex:Exception){error=ex.message?:"未能打开离线库"}finally{busy=false}}}){Text("打开离线库")}
                TextButton(enabled=!busy&&entry.pending==0&&entry.error.isBlank(),onClick={removing=entry}){Text("移除缓存")}
            }
        }}}
        if(!busy&&entries.isEmpty())Text("尚未保存离线库，连接电脑后可保存索引。",style=MaterialTheme.typography.bodySmall)
        if(state.connected)Text("切换离线库前请先断开当前电脑；移除旧缓存无需断开。",style=MaterialTheme.typography.bodySmall)
    }},confirmButton={TextButton(enabled=!busy,onClick={busy=true;error="";scope.launch{try{refresh()}catch(ex:Exception){error=ex.message?:"未能刷新"}finally{busy=false}}}){Text("刷新")}},dismissButton={TextButton(enabled=!busy,onClick={open=false}){Text("关闭")}})
    removing?.let {entry->AlertDialog(onDismissRequest={removing=null},title={Text("移除此离线库缓存？")},text={Text("仅移除电脑 ${entry.target.substringBefore(':').take(8)}／素材库 ${entry.target.substringAfter(':').take(8)} 的手机离线索引与本机常用搜索并释放名额。移除前会再次检查待同步草稿；有草稿时不会删除。")},confirmButton={TextButton(onClick={removing=null;busy=true;error="";scope.launch{try{MaterialLibrary.removeSavedWorkspace(entry.target);refresh()}catch(ex:Exception){error=ex.message?:"未能移除，原草稿保留"}finally{busy=false}}}){Text("确认移除")}},dismissButton={TextButton(onClick={removing=null}){Text("取消")}})}
}

@Composable fun LibraryFilters(state:MaterialLibrary.State) {
    val q=state.query
    var search by remember(state.epoch){mutableStateOf(TextFieldValue(q.search))}
    LaunchedEffect(state.epoch,q.search) {
        // Typing/IME callbacks already hold the correct TextFieldValue: preserve selection/composition.
        val latest=MaterialLibrary.state.value
        if(latest.epoch==state.epoch&&latest.query.search==q.search&&search.text!=q.search)search=TextFieldValue(q.search,selection=TextRange(q.search.length))
    }
    Text("完整素材库",style=MaterialTheme.typography.titleLarge)
    val retained=com.liuzhuan.app.data.MaterialRepository.materials.value.size
    val first=MaterialLibrary.firstRetainedOffset()
    Text(if(!state.connected)"离线浏览 · ${if(state.offlineComplete)"已保存全库索引" else "仅已保存的部分摘要"} · 当前筛选 ${state.total} 项" else if(state.loading)"正在同步…" else "当前 ${if(retained>0)first+1 else 0}–${first+retained} / ${state.total} 项 · 最多保留1000条摘要",
        style=MaterialTheme.typography.bodySmall,color=MaterialTheme.colorScheme.onSurfaceVariant)
    OutlinedTextField(value=search,onValueChange={if(it.text.length<=256){search=it;MaterialLibrary.query(MaterialLibrary.state.value.query.copy(search=it.text))}},singleLine=true,enabled=state.connected||state.canEditOffline,
        modifier=Modifier.fillMaxWidth(),placeholder={Text("全库搜索名称、标签、备注…",maxLines=1)},
        trailingIcon={if(search.text.isNotEmpty())TextButton(onClick={search=TextFieldValue("");MaterialLibrary.query(q.copy(search=""))}){Text("清除")}})
    LibrarySavedSearchControls(state)
    Row(Modifier.horizontalScroll(rememberScrollState()),horizontalArrangement=Arrangement.spacedBy(8.dp)) {
        FilterChip(selected=q.favorite,enabled=state.connected||state.canEditOffline,onClick={MaterialLibrary.query(q.copy(favorite=!q.favorite))},label={Text("★ 收藏")})
        listOf("All" to "全部","Image" to "图片","Text" to "文字","Video" to "视频","Audio" to "音频","Other" to "其他").forEach {(type,title)->
            FilterChip(selected=q.type==type,enabled=state.connected||state.canEditOffline,onClick={MaterialLibrary.query(q.copy(type=type))},label={Text(title)})
        }
    }
    Row(Modifier.horizontalScroll(rememberScrollState()),horizontalArrangement=Arrangement.spacedBy(8.dp)) {
        LibraryFacet("分组",q.group,state.groups,state.connected||state.canEditOffline){MaterialLibrary.query(q.copy(group=it))}
        LibraryFacet("标签",q.tag,state.tags,state.connected||state.canEditOffline){MaterialLibrary.query(q.copy(tag=it))}
        TextButton(enabled=state.connected&&!state.loading,onClick={MaterialLibrary.invalidated()}){Text("刷新")}
        if(first>0)TextButton(enabled=(state.connected||state.canEditOffline)&&!state.loading,onClick={MaterialLibrary.latest()}){Text("返回最新")}
    }
    if(state.error.isNotEmpty())Text(state.error,style=MaterialTheme.typography.bodySmall,color=MaterialTheme.colorScheme.error)
    Text(if(state.connected)"点按复制或保存 · 支持单条与批量整理" else "可离线整理；重连原电脑后检查冲突并同步。文件原件和图片需在线获取。",style=MaterialTheme.typography.bodySmall,color=MaterialTheme.colorScheme.onSurfaceVariant)
    LibraryOfflineControls(state)
}
@Composable private fun LibrarySavedSearchControls(state:MaterialLibrary.State) {
    val scope=rememberCoroutineScope();var manage by remember(state.epoch){mutableStateOf(false)}
    var showSave by remember(state.epoch){mutableStateOf(false)};var editingId by remember(state.epoch){mutableStateOf<String?>(null)}
    var name by remember{mutableStateOf("")};var busy by remember{mutableStateOf(false)};var error by remember{mutableStateOf("")}
    var removing by remember(state.epoch){mutableStateOf<com.liuzhuan.app.core.SavedLibrarySearch?>(null)}
    Row(Modifier.horizontalScroll(rememberScrollState()),horizontalArrangement=Arrangement.spacedBy(4.dp)) {
        TextButton(enabled=state.canSaveSearches&&!busy&&state.savedSearches.size<10,onClick={editingId=null;name=state.query.search.take(40);error="";showSave=true}){Text("保存此筛选")}
        TextButton(enabled=state.canSaveSearches&&!busy,onClick={error="";manage=true}){Text("常用搜索 ${state.savedSearches.size}/10")}
    }
    if(state.savedSearchError.isNotBlank())Text("常用搜索不可读取，原记录保留：${state.savedSearchError}",color=MaterialTheme.colorScheme.error,style=MaterialTheme.typography.bodySmall)
    if(error.isNotBlank())Text(error,color=MaterialTheme.colorScheme.error,style=MaterialTheme.typography.bodySmall)
    if(manage)AlertDialog(onDismissRequest={if(!busy)manage=false},title={Text("本机常用搜索 · ${state.savedSearches.size}/10")},text={Column {
        Text("只在明确保存时记录条件；不收集输入历史。按电脑和素材库分别保存，离线可用。",style=MaterialTheme.typography.bodySmall)
        if(error.isNotBlank())Text(error,color=MaterialTheme.colorScheme.error)
        if(state.savedSearches.isEmpty())Text("先设置搜索、类型、收藏或分组标签，再点“保存此筛选”。",style=MaterialTheme.typography.bodySmall)
        LazyColumn(Modifier.heightIn(max=420.dp)) {items(state.savedSearches,key={it.id}){value->Column(Modifier.fillMaxWidth().padding(vertical=8.dp)) {
            Text(value.name,style=MaterialTheme.typography.titleSmall)
            Text(listOfNotNull(value.search.takeIf {it.isNotBlank()}?.let {"搜索：$it"},value.type.takeIf {it!="All"},if(value.favorite)"收藏" else null,value.group.takeIf {it.isNotBlank()}?.let {"分组：$it"},value.tag.takeIf {it.isNotBlank()}?.let {"标签：$it"}).joinToString(" · ").ifBlank {"全部素材"},style=MaterialTheme.typography.bodySmall,maxLines=3)
            Row(Modifier.horizontalScroll(rememberScrollState())) {
                TextButton(enabled=!busy,onClick={busy=true;error="";scope.launch{try{MaterialLibrary.applySearch(value.id,state.epoch);manage=false}catch(ex:kotlinx.coroutines.CancellationException){throw ex}catch(ex:Exception){error=ex.message?:"未应用常用搜索"}finally{busy=false}}}){Text("应用")}
                TextButton(enabled=!busy,onClick={editingId=value.id;name=value.name;showSave=true;error=""}){Text("用当前筛选更新")}
                TextButton(enabled=!busy,onClick={removing=value}){Text("删除")}
            }
        }}}
    }},confirmButton={TextButton(enabled=!busy,onClick={manage=false}){Text("关闭")}})
    if(showSave)AlertDialog(onDismissRequest={if(!busy)showSave=false},title={Text(if(editingId==null)"保存当前筛选" else "更新常用搜索")},text={Column(verticalArrangement=Arrangement.spacedBy(8.dp)) {
        Text("保存当前搜索词、类型、收藏、分组及标签条件，不保存素材副本或同步到电脑。",style=MaterialTheme.typography.bodySmall)
        OutlinedTextField(name,{if(it.length<=40)name=it},singleLine=true,label={Text("名称（1–40字）")},enabled=!busy)
        if(error.isNotBlank())Text(error,color=MaterialTheme.colorScheme.error)
    }},confirmButton={TextButton(enabled=!busy&&name.isNotBlank(),onClick={busy=true;error="";val q=state.query;scope.launch{try{MaterialLibrary.saveSearch(name,state.epoch,editingId,q);showSave=false}catch(ex:kotlinx.coroutines.CancellationException){throw ex}catch(ex:Exception){error=ex.message?:"保存失败，原条件保留"}finally{busy=false}}}){Text(if(busy)"保存中…" else "确认保存")}},dismissButton={TextButton(enabled=!busy,onClick={showSave=false}){Text("取消")}})
    removing?.let {value->AlertDialog(onDismissRequest={removing=null},title={Text("删除“${value.name}”？")},text={Text("只删除本机保存的筛选条件，不改动素材。")},confirmButton={TextButton(onClick={removing=null;busy=true;error="";scope.launch{try{MaterialLibrary.deleteSearch(value.id,state.epoch)}catch(ex:kotlinx.coroutines.CancellationException){throw ex}catch(ex:Exception){error=ex.message?:"未能删除，原条件保留"}finally{busy=false}}}){Text("确认删除")}},dismissButton={TextButton(onClick={removing=null}){Text("取消")}})}
}
@Composable private fun LibraryFacet(title:String,value:String,choices:List<String>,enabled:Boolean,select:(String)->Unit) {
    var open by remember {mutableStateOf(false)}
    Box {
        OutlinedButton(enabled=enabled,onClick={open=true},modifier=Modifier.widthIn(max=180.dp)){Text("$title：${value.ifBlank {"全部"}}",maxLines=1)}
        DropdownMenu(expanded=open,onDismissRequest={open=false},modifier=Modifier.heightIn(max=320.dp).widthIn(max=250.dp)) {
            DropdownMenuItem(text={Text("全部$title")},onClick={select("");open=false})
            (listOf(value).filter {it.isNotBlank()}+choices).distinct().forEach {choice->DropdownMenuItem(text={Text(choice)},onClick={select(choice);open=false})}
        }
    }
}

@Composable fun MaterialEditor(initial:MaterialItem,epoch:String,onDismiss:()->Unit) {
    var base by remember(initial.id,epoch){mutableStateOf(initial)}
    var favorite by remember(initial.id,epoch){mutableStateOf(initial.favorite)}
    var group by remember(initial.id,epoch){mutableStateOf(initial.group)}
    var tags by remember(initial.id,epoch){mutableStateOf(initial.tags.joinToString("，"))}
    var note by remember(initial.id,epoch){mutableStateOf(initial.note)}
    var busy by remember{mutableStateOf(false)}
    var error by remember{mutableStateOf("")}
    var conflict by remember{mutableStateOf<MaterialItem?>(null)}
    val scope=rememberCoroutineScope()
    AlertDialog(onDismissRequest={if(!busy)onDismiss()},title={Text("整理素材")},text={
        Column(Modifier.verticalScroll(rememberScrollState()),verticalArrangement=Arrangement.spacedBy(10.dp)) {
            Text(base.name,maxLines=2,style=MaterialTheme.typography.titleSmall)
            Row {Checkbox(checked=favorite,onCheckedChange={favorite=it},enabled=!busy);Text("收藏",Modifier.padding(top=12.dp))}
            OutlinedTextField(group,{if(it.length<=48)group=it},label={Text("分组")},singleLine=true,enabled=!busy,modifier=Modifier.fillMaxWidth())
            OutlinedTextField(tags,{tags=it},label={Text("标签，用逗号分隔（最多20个）")},enabled=!busy,modifier=Modifier.fillMaxWidth())
            OutlinedTextField(note,{if(it.length<=4000)note=it},label={Text("备注（最多4000字）")},minLines=3,maxLines=7,enabled=!busy,modifier=Modifier.fillMaxWidth())
            if(base.source.isNotBlank()||base.sourceDevice.isNotBlank())Text("来源：${base.source} · ${base.sourceDevice}",style=MaterialTheme.typography.bodySmall)
            if(base.sourceMissing)Text("电脑上次检查：原文件失效。整理信息仍保留。",color=MaterialTheme.colorScheme.error)
            LibraryOfflineOriginalAction(base,epoch,MaterialLibrary.state.value.connected)
            if(error.isNotEmpty())Text(error,color=MaterialTheme.colorScheme.error)
            conflict?.let {latest ->TextButton(onClick={base=latest;favorite=latest.favorite;group=latest.group;tags=latest.tags.joinToString("，");note=latest.note;conflict=null;error="已重新加载最新状态，请核对后再保存"}){Text("重新加载最新状态（替换当前草稿）")}}
        }
    },confirmButton={TextButton(enabled=!busy,onClick={
        val values=tags.split(',', '，',';','；').map {it.trim()}.filter {it.isNotEmpty()}.distinct()
        if(values.size>20 || values.any {it.length>32}){error="最多20个标签，每个最多32字";return@TextButton}
        busy=true;error=""
        scope.launch {
            try {
                if(MaterialLibrary.state.value.canEditOffline) {
                    val desired=LibraryMetadata.parse(LibraryMetadata(favorite,group,values,note).json())
                    val initial=LibraryMetadata.of(base)
                    val fields=LibraryMetadata.FIELDS.filterNot {desired.sameField(initial,it)}.toSet()
                    MaterialLibrary.stage(listOf(base to desired),epoch,mapOf(base.id to fields));onDismiss();return@launch
                }
                val result=MaterialLibrary.edit(base,favorite,group,values,note,epoch)
                if(result.optString("error").isNotBlank()) {
                    error=result.getString("error")
                    conflict=result.optJSONObject("item")?.let {Proto.parseItemAdded(it).toMaterialItem()}
                } else onDismiss()
            }catch(ex:kotlinx.coroutines.CancellationException){throw ex}
            catch(ex:Exception){error=ex.message?:"未能确认保存，请刷新后核对"}
            finally{busy=false}
        }
    }){Text(if(busy)"保存中…" else if(MaterialLibrary.state.value.canEditOffline)"保存整理更改" else "保存到电脑")}},dismissButton={TextButton(enabled=!busy,onClick=onDismiss){Text("取消")}})
}

@Composable private fun LibraryOfflineControls(state:MaterialLibrary.State) {
    val scope=rememberCoroutineScope();var confirm by remember(state.epoch){mutableStateOf("")};var showDrafts by remember(state.epoch){mutableStateOf(false)}
    var error by remember{mutableStateOf("")}
    Text(if(state.offlineSaved==0L)"尚未保存离线索引" else "离线${if(state.offlineComplete)"全库索引" else "部分摘要"}：${state.offlineCount} 项 · ${java.text.DateFormat.getDateTimeInstance(java.text.DateFormat.SHORT,java.text.DateFormat.SHORT).format(java.util.Date(state.offlineSaved))}",style=MaterialTheme.typography.bodySmall)
    Row(Modifier.horizontalScroll(rememberScrollState()),horizontalArrangement=Arrangement.spacedBy(4.dp)) {
        TextButton(enabled=state.connected&&state.canEditOffline&&!state.workBusy,onClick={confirm="download"}){Text("保存离线索引")}
        TextButton(enabled=state.offlineCount>0&&!state.workBusy,onClick={confirm="clear"}){Text("清除离线索引")}
        TextButton(enabled=state.drafts.isNotEmpty(),onClick={showDrafts=true}){Text("待同步 ${state.drafts.size}")}
        if(state.canEditOffline&&state.offlineCount==0&&state.drafts.isEmpty())TextButton(enabled=!state.workBusy,onClick={confirm="forget"}){Text("移除此离线库")}
    }
    if(state.workBusy){LinearProgressIndicator(Modifier.fillMaxWidth());Text(state.workStatus,style=MaterialTheme.typography.bodySmall);TextButton(onClick={MaterialLibrary.cancelOfflineIndexSave()}){Text("取消保存，保留原索引")}}
    if(state.syncing)Text(state.syncStatus,style=MaterialTheme.typography.bodySmall,color=MaterialTheme.colorScheme.onSurfaceVariant)
    if(state.editStatus.isNotBlank())Text(state.editStatus,style=MaterialTheme.typography.bodySmall,color=MaterialTheme.colorScheme.primary)
    if(error.isNotBlank())Text(error,color=MaterialTheme.colorScheme.error)
    if(confirm.isNotEmpty())AlertDialog(onDismissRequest={confirm=""},title={Text(when(confirm){"download"->"保存离线索引";"forget"->"移除此离线库";else->"清除离线索引"})},text={Text(
        if(confirm=="download")"更新当前电脑的素材索引与可保存的纯文字，不下载图片、视频或其他原件。可用时优先增量更新，否则重新保存完整快照。最多5万项／64MB；超限或途中变更会保留旧索引与整理草稿。"
        else if(confirm=="forget")"释放当前离线库名额（最多5个），一并移除该库的本机常用搜索。不删除电脑素材或接收文件；下次重新连接可再次启用。"
        else "只清除手机上的离线索引与文字副本，保留待同步草稿、已接收文件和电脑原件。")},confirmButton={TextButton(onClick={val action=confirm;confirm="";error="";scope.launch{try{when(action){"download"->MaterialLibrary.startOfflineIndexSave(state.epoch);"forget"->MaterialLibrary.forgetOfflineWorkspace(state.epoch);else->MaterialLibrary.clearOfflineIndex(state.epoch)}}catch(ex:kotlinx.coroutines.CancellationException){throw ex}catch(ex:Exception){error=ex.message?:"操作未完成，原记录保留"}}}){Text("确认")}},dismissButton={TextButton(onClick={confirm=""}){Text("取消")}})
    if(showDrafts)LibraryDraftsDialog(state,onDismiss={showDrafts=false})
}

@Composable private fun LibraryDraftsDialog(state:MaterialLibrary.State,onDismiss:()->Unit) {
    val scope=rememberCoroutineScope();var chosen by remember(state.epoch){mutableStateOf<LibraryDraft?>(null)};var error by remember{mutableStateOf("")}
    AlertDialog(onDismissRequest=onDismiss,title={Text("整理更改 · ${state.drafts.size} 条")},text={
        Column {
            Text("只同步到原电脑和原素材库。冲突或删除不会自动覆盖。",style=MaterialTheme.typography.bodySmall)
            if(error.isNotBlank())Text(error,color=MaterialTheme.colorScheme.error)
            LazyColumn(Modifier.heightIn(max=420.dp)) {items(state.drafts,key={it.id}){draft->
                Column(Modifier.fillMaxWidth().padding(vertical=8.dp)) {
                    Text(draft.base.name,maxLines=2)
                    Text(draft.issue.ifBlank {"已保存在手机，等待原电脑确认"},style=MaterialTheme.typography.bodySmall,color=if(draft.issue.isBlank())MaterialTheme.colorScheme.onSurfaceVariant else MaterialTheme.colorScheme.error)
                    TextButton(onClick={chosen=draft},enabled=!state.workBusy){Text("查看与处理")}
                }
            }}
        }
    },confirmButton={TextButton(enabled=state.connected&&!state.workBusy&&!state.syncing&&state.drafts.isNotEmpty(),onClick={scope.launch{MaterialLibrary.syncPending()}}){Text(if(state.syncing)"正在同步…" else "同步待处理更改")}},dismissButton={TextButton(onClick=onDismiss){Text("关闭")}})
    chosen?.let {draft->
        fun details(meta:LibraryMetadata)="收藏：${if(meta.favorite)"是" else "否"}\n分组：${meta.group.ifBlank{"无"}}\n标签：${meta.tags.joinToString("，").ifBlank{"无"}}\n备注：${meta.note.ifBlank{"无"}}"
        AlertDialog(onDismissRequest={chosen=null},title={Text(draft.base.name,maxLines=2)},text={Column(Modifier.verticalScroll(rememberScrollState())) {
            Text("我的更改",style=MaterialTheme.typography.titleSmall);Text(details(draft.desired))
            draft.remote?.let {HorizontalDivider(Modifier.padding(vertical=8.dp));Text("电脑最新状态",style=MaterialTheme.typography.titleSmall);Text(details(LibraryMetadata.of(it)))}
            Text("重新提交会以这里的电脑版本为基准；若再次变化仍会检查冲突。放弃只删除本机草稿。",style=MaterialTheme.typography.bodySmall,modifier=Modifier.padding(top=12.dp))
        }},confirmButton={if(draft.remote!=null)TextButton(onClick={chosen=null;scope.launch{try{MaterialLibrary.resolveDraft(draft.id,true,state.epoch)}catch(ex:Exception){error=ex.message?:"尚未处理"}}}){Text("核对后重新提交我的修改")}},dismissButton={Row {
            TextButton(onClick={chosen=null;scope.launch{try{MaterialLibrary.resolveDraft(draft.id,false,state.epoch)}catch(ex:Exception){error=ex.message?:"尚未处理"}}}){Text("放弃此草稿")}
            TextButton(onClick={chosen=null}){Text("返回")}
        }})
    }
}

@Composable fun LibraryBatchEditor(items:List<MaterialItem>,epoch:String,onDismiss:()->Unit) {
    var favorite by remember{mutableIntStateOf(0)};var setGroup by remember{mutableStateOf(false)};var group by remember{mutableStateOf("")}
    var tagsMode by remember{mutableIntStateOf(0)};var tags by remember{mutableStateOf("")};var busy by remember{mutableStateOf(false)};var error by remember{mutableStateOf("")}
    val scope=rememberCoroutineScope()
    AlertDialog(onDismissRequest={if(!busy)onDismiss()},title={Text("批量整理 ${items.size} 项")},text={Column(Modifier.verticalScroll(rememberScrollState()),verticalArrangement=Arrangement.spacedBy(8.dp)) {
        Text("仅修改勾选的字段；备注保留。每批最多100项，冲突按素材分别处理。",style=MaterialTheme.typography.bodySmall)
        Row(Modifier.horizontalScroll(rememberScrollState())) {listOf("保留收藏","收藏","取消收藏").forEachIndexed {index,title->FilterChip(selected=favorite==index,onClick={favorite=index},label={Text(title)})}}
        Row {Checkbox(setGroup,{setGroup=it});Text("修改分组（留空表示清除）",Modifier.padding(top=12.dp))}
        if(setGroup)OutlinedTextField(group,{if(it.length<=48)group=it},singleLine=true,label={Text("分组")})
        Text("标签动作",style=MaterialTheme.typography.labelLarge)
        Row(Modifier.horizontalScroll(rememberScrollState())) {listOf("保留","追加","移除","替换").forEachIndexed {index,title->FilterChip(selected=tagsMode==index,onClick={tagsMode=index},label={Text(title)})}}
        if(tagsMode!=0)OutlinedTextField(tags,{if(it.length<=1500)tags=it},label={Text("标签，用逗号分隔")})
        if(error.isNotBlank())Text(error,color=MaterialTheme.colorScheme.error)
    }},confirmButton={TextButton(enabled=!busy&&(favorite!=0||setGroup||tagsMode!=0),onClick={
        busy=true;error="";scope.launch {try {
            val values=tags.split(',', '，',';','；').map{it.trim()}.filter{it.isNotEmpty()}.distinctBy{it.lowercase()}
            val changes=items.map {item->val current=LibraryMetadata.of(MaterialLibrary.state.value.drafts.firstOrNull{it.id==item.id}?.overlay(item)?:item)
                val nextTags=when(tagsMode){1->(current.tags+values).distinctBy{it.lowercase()};2->current.tags.filterNot{v->values.any{it.equals(v,true)}};3->values;else->current.tags}
                item to LibraryMetadata(if(favorite==0)current.favorite else favorite==1,if(setGroup)group else current.group,nextTags,current.note)
            }
            val fields=buildSet {if(favorite!=0)add("favorite");if(setGroup)add("group");if(tagsMode!=0)add("tags")}
            MaterialLibrary.stage(changes,epoch,changes.associate {it.first.id to fields});onDismiss()
        }catch(ex:Exception){error=ex.message?:"未保存，请重试"}finally{busy=false}}}){Text(if(busy)"保存中…" else "保存批量更改")}},dismissButton={TextButton(enabled=!busy,onClick=onDismiss){Text("取消")}})
}
