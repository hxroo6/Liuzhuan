package com.liuzhuan.app.data

import com.liuzhuan.app.net.Proto

/**
 * 素材条目（接收页统一模型）
 *
 * 单一事实源：所有素材数据（PC 端权威状态）最终都收敛到这个模型，
 * 由 [MaterialRepository] 持有。UI 只观察 Repository，不各自维护列表。
 */
data class MaterialItem(
    val id: String,
    val type: String,          // Text / Image / Video / Audio / Other
    val name: String,
    val size: Long,
    val time: Long,            // Unix 秒
    val sequence: Long = 0,    // 服务器变更序号（去重 / gap 检测），不作为素材创建时间
    val sourceDeviceId: String = "",
    val group: String = "",
    val tags: List<String> = emptyList(),
    val note: String = "",
    val hasFile: Boolean = type != "Text",
    val favorite:Boolean=false,val revision:Long=0,val sourceMissing:Boolean=false,
    val source:String="",val sourceDevice:String="",val offlineText:String="",val textComplete:Boolean=false,val sourceRevision:Long=0
)

/** 协议摘要 → 统一模型 */
fun Proto.ItemSummary.toMaterialItem(): MaterialItem = MaterialItem(
    id = id,
    type = type,
    name = name,
    size = size,
    time = time,
    sequence = sequence, group = group, tags = tags, note = note, hasFile=hasFile,
    favorite=favorite,revision=revision,sourceMissing=sourceMissing,source=source,sourceDevice=sourceDevice,offlineText=offlineText,textComplete=textComplete,sourceRevision=sourceRevision
)

