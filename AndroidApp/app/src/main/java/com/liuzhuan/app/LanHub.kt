package com.liuzhuan.app

/**
 * 全局连接单例 — MainActivity 与 ClipMonitorService 共享
 * LanController 进程级创建并持有 client；页面仅订阅状态，剪贴板服务共享发送入口。
 */
object LanHub {
    @Volatile
    var client: com.liuzhuan.app.net.LanClient? = null

    /** 后台自动发送剪贴板开关（@Volatile 供无障碍服务非阻塞读取） */
    @Volatile
    var autoSendClipboard: Boolean = true
}
