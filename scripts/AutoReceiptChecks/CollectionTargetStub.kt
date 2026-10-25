package com.liuzhuan.app
object FolderSubscriptions {
    fun target(s:com.liuzhuan.app.core.SettingsStore.Settings)="library:${s.expectedDeviceId}:${s.serverLibraryId}:${com.liuzhuan.app.net.Proto.sha256Hex(s.password)}"
    fun rebindVerifiedTarget(old:String,new:String){}
}
