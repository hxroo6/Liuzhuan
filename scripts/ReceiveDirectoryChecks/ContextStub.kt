package android.content

import android.content.pm.PackageManager

data class ComponentName(val packageName:String,val className:String)
class ActivityNotFoundException:RuntimeException()
open class Context(val packageManager:PackageManager) {
    val launches=mutableListOf<Intent>()
    var deny:(Intent)->Boolean={false}
    open fun startActivity(intent:Intent) {
        launches+=intent
        if(deny(intent)) throw ActivityNotFoundException()
    }
}
class Intent(val action:String) {
    var data:String?=null
    var type:String?=null
    var component:ComponentName?=null
    var packageName:String?=null
    var flags=0
    fun setDataAndType(data:String,type:String)=apply { this.data=data;this.type=type }
    fun setComponent(component:ComponentName)=apply { this.component=component }
    fun setPackage(packageName:String)=apply { this.packageName=packageName }
    fun addFlags(flags:Int)=apply { this.flags=this.flags or flags }
    companion object { const val ACTION_VIEW="android.intent.action.VIEW"; const val FLAG_ACTIVITY_NEW_TASK=0x10000000 }
}
