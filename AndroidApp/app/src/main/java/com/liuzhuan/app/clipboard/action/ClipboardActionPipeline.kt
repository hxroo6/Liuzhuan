package com.liuzhuan.app.clipboard.action

import com.liuzhuan.app.clipboard.ClipboardEvent
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch

/**
 * 动作管线 —— 串行执行注册的 ClipboardAction。
 * 使用独立协程作用域（IO），不阻塞捕获链路，不阻塞无障碍主线程。
 */
class ClipboardActionPipeline(
    private val scope: CoroutineScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
) {
    private val actions = mutableListOf<ClipboardAction>()

    fun add(action: ClipboardAction) {
        actions.add(action)
    }

    /** 分发事件给所有已注册动作（异步） */
    fun process(event: ClipboardEvent) {
        scope.launch {
            actions.forEach { action ->
                try {
                    action.execute(event)
                } catch (e: Exception) {
                    android.util.Log.d("ClipPipeline", "action ${action.javaClass.simpleName} failed: ${e.javaClass.simpleName}")
                }
            }
        }
    }
}
