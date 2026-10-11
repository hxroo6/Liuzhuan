package com.liuzhuan.app.data

import android.util.Log
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * 素材仓库 —— Android 端接收页的单一事实源。
 *
 * PC 端是权威状态源；本仓库是 Android 端的本地镜像，通过 StateFlow 暴露给 UI。
 * WebSocket 回调只负责 decode 后调用这里的方法（route），不直接改 UI state。
 *
 * 关键职责：
 * - snapshot（replaceAll）：首次连接 / 重连后全量替换
 * - add / update / delete / clear：增量事件
 * - 去重：以 id 为唯一键（Map），避免 snapshot + delta 叠加产生重复
 * - 素材排序：time 降序，同秒保持 snapshot 次序并记住近期删除位置
 * - 全部增量共用 sequence 验序，丢包不应用后续增量，等待 resync snapshot
 */
object MaterialRepository {

    /** 同步状态（轻量，供接收页顶部显示） */
    enum class SyncState { IDLE, SYNCING, SYNCED, RESYNCING, ERROR }

    private val _materials = MutableStateFlow<List<MaterialItem>>(emptyList())
    val materials: StateFlow<List<MaterialItem>> = _materials.asStateFlow()

    private val _syncState = MutableStateFlow(SyncState.IDLE)
    val syncState: StateFlow<SyncState> = _syncState.asStateFlow()

    /** 最近一次收到的服务器序号（用于 gap 检测） */
    @Volatile
    private var lastSequence: Long = 0

    /** 建连或 gap 后先等权威快照，不让增量改动旧连接的画面。 */
    private var awaitingSnapshot = true

    /** 本地镜像（id → item，LinkedHashMap 保序） */
    private val map = LinkedHashMap<String, MaterialItem>()

    private val displayOrder = HashMap<String, Long>()
    private val removedOrder = LinkedHashMap<String, Long>()
    private var nextDisplayOrder = -1L
    private val ordered = java.util.TreeSet<MaterialItem>(
        compareByDescending<MaterialItem> { it.time }
            .thenBy { displayOrder[it.id] ?: Long.MAX_VALUE }.thenBy { it.id })

    /** gap 检测回调（连接层注册：发现丢包时重新请求 snapshot） */
    @Volatile
    var onSequenceGap: (() -> Unit)? = null

    /** 全量快照：替换本地镜像 */
    @Synchronized
    fun replaceAll(items: List<MaterialItem>, sequence: Long) {
        map.clear()
        ordered.clear()
        displayOrder.clear()
        removedOrder.clear()
        nextDisplayOrder = -1L
        items.forEachIndexed { index, item ->
            map[item.id] = item
            displayOrder.putIfAbsent(item.id, index.toLong())
        }
        ordered.addAll(map.values)
        // 包括空库和服务器重启后的 0，不能沿用上个连接的高序号。
        lastSequence = sequence.coerceAtLeast(0)
        awaitingSnapshot = false
        emitSorted()
        _syncState.value = SyncState.SYNCED
        Log.d(TAG, "snapshot received count=${items.size} sequence=$sequence")
    }

    /** 新增/去重置顶也会携带新序号；更新同 ID 摘要并消费该次变化。 */
    @Synchronized
    fun add(item: MaterialItem) {
        if (!acceptSequence(item.sequence)) return
        val existing = map.containsKey(item.id)
        map[item.id]?.let { ordered.remove(it) }
        val previousOrder = removedOrder.remove(item.id)
        // 同秒的新素材/重复收集置于前面；撤回保留删除前位置。
        displayOrder[item.id] = if (existing || previousOrder == null) nextDisplayOrder-- else previousOrder
        map[item.id] = item
        ordered.add(item)
        emitSorted()
        Log.d(TAG, "material added id=${item.id.take(8)} sequence=${item.sequence}")
    }

    @Synchronized
    fun update(item: MaterialItem) {
        add(item)
    }

    @Synchronized
    fun delete(id: String, sequence: Long) {
        if (!acceptSequence(sequence)) return
        val removed = map.remove(id)
        if (removed != null) {
            ordered.remove(removed)
            displayOrder.remove(id)?.let { rememberRemovedOrder(id, it) }
            emitSorted()
            Log.d(TAG, "material deleted id=${id.take(8)}")
        }
    }

    @Synchronized
    fun clear(sequence: Long) {
        if (!acceptSequence(sequence)) return
        map.keys.forEach { id -> displayOrder[id]?.let { rememberRemovedOrder(id, it) } }
        map.clear()
        ordered.clear()
        displayOrder.clear()
        emitSorted()
        Log.d(TAG, "materials cleared")
    }

    @Synchronized
    fun markSyncing() {
        lastSequence = 0
        awaitingSnapshot = true
        _syncState.value = SyncState.SYNCING
    }

    @Synchronized
    fun markError() {
        awaitingSnapshot = true
        _syncState.value = SyncState.ERROR
    }

    /** 所有增量先消费序号，再决定是否有实际素材变化。 */
    private fun acceptSequence(sequence: Long): Boolean {
        if (awaitingSnapshot || sequence <= lastSequence) return false
        if (sequence != lastSequence + 1) {
            Log.w(TAG, "sequence gap expected=${lastSequence + 1} actual=$sequence")
            awaitingSnapshot = true
            _syncState.value = SyncState.RESYNCING
            onSequenceGap?.invoke()
            return false
        }
        lastSequence = sequence
        return true
    }

    private fun rememberRemovedOrder(id: String, order: Long) {
        removedOrder.remove(id)
        removedOrder[id] = order
        // 仅保留最近删除的同秒排序辅助信息，避免长时间运行累积无界历史。
        while (removedOrder.size > MAX_REMEMBERED_REMOVALS) removedOrder.remove(removedOrder.keys.first())
    }

    /** 序号代表变更，撤回不能因此把旧素材推到列表顶部。 */
    private fun emitSorted() {
        _materials.value = ordered.toList()
    }

    private const val TAG = "MaterialRepo"
    private const val MAX_REMEMBERED_REMOVALS = 512
}
