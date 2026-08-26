package com.liuzhuan.app.net

import org.json.JSONObject
import java.security.MessageDigest
import java.util.UUID

/**
 * 协议消息构建/解析（v1）
 * 与电脑端 LanMessage 对应：docs/流转安卓版开发文档.md §3
 */
object Proto {

    fun sha256Hex(input: String): String {
        val digest = MessageDigest.getInstance("SHA-256").digest(input.toByteArray())
        return digest.joinToString("") { "%02x".format(it) }
    }

    fun buildHello(authHash: String): String = JSONObject()
        .put("v", 1)
        .put("type", "hello")
        .put("id", UUID.randomUUID().toString().replace("-", ""))
        .put("ts", System.currentTimeMillis() / 1000)
        .put("device", android.os.Build.MODEL)
        .put("data", JSONObject()
            .put("auth", authHash)
            .put("ts", System.currentTimeMillis() / 1000))
        .toString()

    fun buildHeartbeat(): String = JSONObject()
        .put("v", 1)
        .put("type", "heartbeat")
        .put("id", UUID.randomUUID().toString().replace("-", ""))
        .put("ts", System.currentTimeMillis() / 1000)
        .put("device", android.os.Build.MODEL)
        .put("data", JSONObject().put("t", System.currentTimeMillis() / 1000))
        .toString()

    fun buildSyncText(content: String, source: String = "android"): String = JSONObject()
        .put("v", 1)
        .put("type", "sync_text")
        .put("id", UUID.randomUUID().toString().replace("-", ""))
        .put("ts", System.currentTimeMillis() / 1000)
        .put("device", android.os.Build.MODEL)
        .put("data", JSONObject()
            .put("content", content)
            .put("source", source))
        .toString()

    fun buildClipboardPush(content: String, app: String): String = JSONObject()
        .put("v", 1)
        .put("type", "clipboard_push")
        .put("id", UUID.randomUUID().toString().replace("-", ""))
        .put("ts", System.currentTimeMillis() / 1000)
        .put("device", android.os.Build.MODEL)
        .put("data", JSONObject()
            .put("content", content)
            .put("app", app))
        .toString()

    /** 请求电脑端最近素材列表 */
    fun buildListSync(): String = JSONObject()
        .put("v", 1)
        .put("type", "list_sync")
        .put("id", UUID.randomUUID().toString().replace("-", ""))
        .put("ts", System.currentTimeMillis() / 1000)
        .put("device", android.os.Build.MODEL)
        .put("data", JSONObject())
        .toString()

    /** 素材摘要（接收页列表项，轻量） */
    data class ItemSummary(
        val id: String,
        val type: String,
        val name: String,
        val size: Long,
        val time: Long,
        val sequence: Long = 0
    )

    /** 解析 list_data 消息 → 素材摘要列表 */
    fun parseListData(data: JSONObject): List<ItemSummary> {
        val arr = data.optJSONArray("items") ?: return emptyList()
        val out = mutableListOf<ItemSummary>()
        for (i in 0 until arr.length()) {
            val it = arr.optJSONObject(i) ?: continue
            out.add(ItemSummary(
                id = it.optString("id", ""),
                type = it.optString("type", "Text"),
                name = it.optString("name", ""),
                size = it.optLong("size", 0),
                time = it.optLong("time", 0),
                sequence = it.optLong("sequence", 0)
            ))
        }
        return out
    }

    /** 解析 item_added 消息 → 单条摘要 */
    fun parseItemAdded(data: JSONObject): ItemSummary = ItemSummary(
        id = data.optString("id", ""),
        type = data.optString("type", "Text"),
        name = data.optString("name", ""),
        size = data.optLong("size", 0),
        time = data.optLong("time", 0),
        sequence = data.optLong("sequence", 0)
    )

    /** 解析消息携带的服务器序号（snapshot / added / deleted / cleared 通用） */
    fun parseSequence(data: JSONObject): Long = data.optLong("sequence", 0)

    /** 解析 item_deleted 消息 → 素材 id */
    fun parseItemDeletedId(data: JSONObject): String = data.optString("id", "")

    /** 请求素材详情（文字全文 / 文件下载地址） */
    fun buildGetItem(id: String): String = JSONObject()
        .put("v", 1)
        .put("type", "get_item")
        .put("id", UUID.randomUUID().toString().replace("-", ""))
        .put("ts", System.currentTimeMillis() / 1000)
        .put("device", android.os.Build.MODEL)
        .put("data", JSONObject().put("id", id))
        .toString()

    /** 素材详情（get_item 响应） */
    data class ItemData(
        val id: String,
        val type: String,
        val name: String,
        val content: String = "",       // 文字全文
        val downloadUrl: String = "",   // 文件下载地址
        val size: Long = 0,
        val error: String = ""
    )

    /** 解析 item_data 消息 */
    fun parseItemData(data: JSONObject): ItemData = ItemData(
        id = data.optString("id", ""),
        type = data.optString("type", "Text"),
        name = data.optString("name", ""),
        content = data.optString("content", ""),
        downloadUrl = data.optString("downloadUrl", ""),
        size = data.optLong("size", 0),
        error = data.optString("error", "")
    )

    /** 解析服务端消息，返回 (type, data) */
    fun parse(raw: String): Pair<String, JSONObject> {
        val obj = JSONObject(raw)
        return obj.optString("type", "unknown") to (obj.optJSONObject("data") ?: JSONObject())
    }
}
