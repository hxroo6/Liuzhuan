package com.liuzhuan.app.net

/** MaterialItem 转换器的协议模型依赖；协议解析仍由正式 APK 构建检查。 */
object Proto {
    data class ItemSummary(
        val id: String,
        val type: String,
        val name: String,
        val size: Long,
        val time: Long,
        val sequence: Long = 0
    )
}
