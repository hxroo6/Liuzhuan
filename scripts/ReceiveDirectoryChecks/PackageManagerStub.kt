package android.content.pm

import android.content.Intent

class ActivityInfo(val packageName:String,val name:String)
class ResolveInfo(val activityInfo:ActivityInfo,val system:Boolean,val label:String) {
    fun loadLabel(pm:PackageManager):CharSequence=label
}
class PackageManager(val handlers:List<ResolveInfo>) {
    val queries=mutableListOf<Pair<Intent,Int>>()
    fun queryIntentActivities(intent:Intent,flags:Int):List<ResolveInfo> {
        queries+=intent to flags
        return handlers.filter { flags and MATCH_SYSTEM_ONLY==0 || it.system }
    }
    companion object { const val MATCH_DEFAULT_ONLY=0x10000; const val MATCH_SYSTEM_ONLY=0x100000 }
}
