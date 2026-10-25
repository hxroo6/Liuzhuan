package com.liuzhuan.app.clipboard
object ClipboardPrivacy {
    fun blockReason(event:ClipboardEvent):String?=null
    fun runIfAllowed(event:ClipboardEvent,action:()->Unit):Boolean {action();return true}
}
