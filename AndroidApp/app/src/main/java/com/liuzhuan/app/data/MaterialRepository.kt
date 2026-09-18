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
 * - 保序：按服务器 sequence + time 排序
 * - sequence gap 检测：发现丢包 → 请求 resync（由连接层重新拉 snapshot）
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

    /** 本地镜像（id → item，LinkedHashMap 保序） */
    private val map = LinkedHashMap<String, MaterialItem>()

    /** gap 检测回调（连接层注册：发现丢包时重新请求 snapshot） */
    @Volatile
    var onSequenceGap: (() -> Unit)? = null

    /** 全量快照：替换本地镜像 */
    @Synchronized
    fun replaceAll(items: List<MaterialItem>, sequence: Long) {
        map.clear()
        items.forEach { map[it.id] = it }
        if (sequence > 0) lastSequence = sequence
        emitSorted()
        _syncState.value = SyncState.SYNCED
        Log.d(TAG, "snapshot received count=${items.size} sequence=$sequence")
    }

    /** 增量新增（去重 + gap 检测 + 保序） */
    @Synchronized
    fun add(item: MaterialItem) {
        if (map.containsKey(item.id)) return
        map[item.id] = item
        emitSorted()
        // gap 检测：服务器序号应连续递增；跳跃说明丢包 → 触发 resync
        if (item.sequence > 0) {
            if (lastSequence > 0 && item.sequence > lastSequence + 1) {
                Log.w(TAG, "sequence gap expected=${lastSequence + 1} actual=${item.sequence}")
                _syncState.value = SyncState.RESYNCING
                onSequenceGap?.invoke()
            }
            if (item.sequence > lastSequence) lastSequence = item.sequence
        }
        Log.d(TAG, "material added id=${item.id.take(8)} sequence=${item.sequence}")
    }

    @Synchronized
    fun update(item: MaterialItem) {
        map[item.id] = item
        emitSorted()
        Log.d(TAG, "material updated id=${item.id.take(8)}")
    }

    @Synchronized
    fun delete(id: String) {
        if (map.remove(id) != null) {
            emitSorted()
            Log.d(TAG, "material deleted id=${id.take(8)}")
        }
    }

    @Synchronized
    fun clear() {
        map.clear()
        emitSorted()
        Log.d(TAG, "materials cleared")
    }

    fun markSyncing() {
        _syncState.value = SyncState.SYNCING
    }

    fun markError() {
        _syncState.value = SyncState.ERROR
    }

    /** 排序并发射（新→旧：sequence 降序，fallback time 降序） */
    private fun emitSorted() {
        val sorted = map.values.sortedWith(
            compareByDescending<MaterialItem> { it.sequence }
                .thenByDescending { it.time }
        )
        _materials.value = sorted
    }

    private const val TAG = "MaterialRepo"
}
