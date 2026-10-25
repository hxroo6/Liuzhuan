package com.liuzhuan.app.core
class SettingsStore {
    data class Settings(val serverIp: String = "", val serverPort: String = "8899", val password: String = "", val deviceName: String = "isolated-phone", val autoSendClipboard: Boolean = true,val expectedDeviceId:String="",val serverName:String="",val serverLibraryId:String="")
}
