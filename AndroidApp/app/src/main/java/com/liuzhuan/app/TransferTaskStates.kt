package com.liuzhuan.app

/** Shared display-state vocabulary. These labels do not replace durable protocol checkpoints. */
object TransferTaskStates {
    private val completed=setOf("已完成","电脑已持久登记","已校验并保存到手机","电脑已接收","电脑已登记文字","已保存到手机")
    fun isCompleted(state:String):Boolean=state in completed
    fun isTerminal(state:String):Boolean=isCompleted(state)||state=="已取消"
}
