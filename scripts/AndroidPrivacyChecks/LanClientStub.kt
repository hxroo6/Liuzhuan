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
    fun automaticClipboardTransport(): com.liuzhuan.app.clipboard.AutomaticClipboardDelivery.Transport? {
        if (state != State.Connected) return null
        return object : com.liuzhuan.app.clipboard.AutomaticClipboardDelivery.Transport {
            override val identity = "isolated-privacy-test"
            override val supportsReceipts = true
            override fun send(event: ClipboardEvent): Boolean {
                texts.add((event.text ?: "") to (event.sourcePackage ?: "android"))
                com.liuzhuan.app.clipboard.ClipboardMonitorCoordinator.delivery.acknowledge(identity, event.requestId, "stored")
                return true
            }
        }
    }
    fun pushFile(bytes: ByteArray, name: String, mime: String, taskId: String = "stub", automaticEvent: ClipboardEvent? = null) {
        if (automaticEvent == null) files.add(name)
        else ClipboardPrivacy.runIfAllowed(automaticEvent) { files.add(name) }
    }
    fun pushFileCached(file: java.io.File, name: String, mime: String, automaticEvent: ClipboardEvent) {
        ClipboardPrivacy.runIfAllowed(automaticEvent) { files.add(name) }
        file.delete()
    }
}
