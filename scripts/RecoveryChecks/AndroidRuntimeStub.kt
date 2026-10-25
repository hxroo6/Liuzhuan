package com.liuzhuan.app
import kotlinx.coroutines.flow.MutableStateFlow
object LanController {fun appendLog(text:String){println(text)}}
object LanHub {var client:FixtureClient?=null}
class FixtureClient(var link:DurableTransfers.Link){fun recoveryLink()=link}
object FolderSubscriptions {
    suspend fun rebindVerifiedTargetAwait(old:String,new:String){}
    data class Entry(val id:String,val target:String)
    data class State(val entries:List<Entry> = emptyList())
    val state=MutableStateFlow(State())
    suspend fun awaitReady(){}
}
object ReceivedFiles {const val FOLDER="Download/Liuzhuan"}
object TransferPolicyStore {fun init(context:android.content.Context){}}
