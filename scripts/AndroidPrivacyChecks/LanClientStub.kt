package com.liuzhuan.app.net

import com.liuzhuan.app.clipboard.ClipboardEvent
import com.liuzhuan.app.clipboard.ClipboardPrivacy
import java.util.concurrent.CopyOnWriteArrayList

/** 仅替换网络副作用；捕获、分发、管线、队列、隐私和Receiver均编译生产源码。 */
class LanClient {
    sealed class State { data object Connected : State(); data object Disconnected : State() }
    var state: State = State.Disconnected
    val texts = CopyOnWriteArrayList<Pair<String, String>>()
    val files = CopyOnWriteArrayList<String>()
    fun pushClipboard(text: String, source: String): Boolean { texts.add(text to source); return true }
    fun pushFile(bytes: ByteArray, name: String, mime: String, taskId: String = "stub", automaticEvent: ClipboardEvent? = null) {
        if (automaticEvent == null) files.add(name)
        else ClipboardPrivacy.runIfAllowed(automaticEvent) { files.add(name) }
    }
}
