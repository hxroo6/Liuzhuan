package com.liuzhuan.app

/** File engine is exercised by dedicated recovery checks; clipboard checks isolate its platform boundary. */
object DurableTransfers {
    data class Link(val host:String,val port:Int,val auth:String,val serverId:String,val target:String,val legacyTarget:String="")
    fun connected(link:Link){}
    fun disconnected(link:Link){}
    fun remoteState(serverId:String,id:String,state:String,revision:Long){}
    fun invalidateAutomatic(){}
    fun cached(file:java.io.File,name:String,id:String,event:com.liuzhuan.app.clipboard.ClipboardEvent)=false
}
object LanController {fun appendLog(value:String){}}
