package com.liuzhuan.app

/**
 * 全局连接单例 — MainActivity 与 ClipMonitorService 共享
 * MainActivity 创建并持有 client；剪贴板服务直接调用 client.pushClipboard
 */
object LanHub {
    @Volatile
    var client: com.liuzhuan.app.net.LanClient? = null

    /** 后台自动发送剪贴板开关（@Volatile 供无障碍服务非阻塞读取） */
    @Volatile
    var autoSendClipboard: Boolean = true
}
