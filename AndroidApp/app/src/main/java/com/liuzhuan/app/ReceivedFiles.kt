package com.liuzhuan.app

import android.content.Context
import android.content.ComponentName
import android.content.Intent
import android.content.pm.PackageManager
import android.provider.DocumentsContract

/** 浏览保存目录不额外索取存储授权；目录定位由目标文件管理器实现。 */
object ReceivedFiles {
    const val FOLDER = "Download/Liuzhuan"
    // 旧任务仍可打开原位置；新接收文件一律写入 FOLDER，不搬动已有文件。
    private val readableFolders=setOf(FOLDER, "Pictures/Liuzhuan", "Movies/Liuzhuan", "Music/Liuzhuan")
    data class Manager(val packageName:String, val label:String)
    private fun location(folder:String) = DocumentsContract.buildDocumentUri(
        "com.android.externalstorage.documents", "primary:$folder")
    private fun view(folder:String)=Intent(Intent.ACTION_VIEW)
        .setDataAndType(location(folder),DocumentsContract.Document.MIME_TYPE_DIR)

    fun managers(context:Context):List<Manager> {
        val pm=context.packageManager
        return pm.queryIntentActivities(view(FOLDER),PackageManager.MATCH_DEFAULT_ONLY)
            .map { Manager(it.activityInfo.packageName,it.loadLabel(pm).toString()) }
            .distinctBy { it.packageName }.sortedBy { it.label }
    }

    fun open(context:Context,folder:String,packageName:String) {
        if(folder !in readableFolders) { toast(context,"保存目录无效");return }
        // 默认仅调用系统浏览器，绝不以目录授权/选择器代替浏览。
        val intents = if(packageName.isBlank()) {
            runCatching { context.packageManager.queryIntentActivities(view(folder),
                PackageManager.MATCH_DEFAULT_ONLY or PackageManager.MATCH_SYSTEM_ONLY) }
                .getOrDefault(emptyList())
                .sortedBy { if(it.activityInfo.packageName.endsWith(".documentsui")) 0 else 1 }
                .map { view(folder).setComponent(ComponentName(it.activityInfo.packageName,it.activityInfo.name)) }
        } else listOf(view(folder).setPackage(packageName))
        for(intent in intents) {
            try {
                context.startActivity(intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                return
            } catch (_:android.content.ActivityNotFoundException) {
            } catch (_:SecurityException) {
            }
        }
        toast(context,if(packageName.isBlank()) "系统文件管理器无法打开目录，请在文件管理器设置中更换应用" else "所选文件管理器无法打开目录，请更换或使用系统文件")
    }
}
