package com.liuzhuan.app.clipboard.action

import com.liuzhuan.app.clipboard.ClipboardEvent

/**
 * 剪贴板动作接口 —— 事件经过捕获、去重、过滤后进入 ActionPipeline，
 * 由注册的 Action 依次执行。第一阶段只实现 PushToPcAction，未来可扩展：
 * SaveHistoryAction / OpenUrlAction / TranslateAction / OcrAction / WebhookAction ...
 */
interface ClipboardAction {
    suspend fun execute(event: ClipboardEvent)
}
