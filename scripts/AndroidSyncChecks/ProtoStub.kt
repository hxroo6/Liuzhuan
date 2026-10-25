package com.liuzhuan.app.net

/** MaterialItem 转换器的协议模型依赖；协议解析仍由正式 APK 构建检查。 */
object Proto {
    data class ItemSummary(
        val id: String,
        val type: String,
        val name: String,
        val size: Long,
        val time: Long,
        val sequence: Long = 0,
        val group: String = "", val tags: List<String> = emptyList(), val note: String = "", val hasFile: Boolean = type != "Text",val favorite:Boolean=false,val revision:Long=0,val sourceMissing:Boolean=false,val source:String="",val sourceDevice:String="",val offlineText:String="",val textComplete:Boolean=false,val sourceRevision:Long=0
    )
}
