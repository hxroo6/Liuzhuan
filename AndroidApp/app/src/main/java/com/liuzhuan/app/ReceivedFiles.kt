package com.liuzhuan.app

import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.provider.DocumentsContract

/** 浏览保存目录不额外索取存储授权；目录定位由目标文件管理器实现。 */
object ReceivedFiles {
    val folders=listOf("Pictures/Liuzhuan", "Movies/Liuzhuan", "Music/Liuzhuan", "Download/Liuzhuan")
    data class Manager(val packageName:String, val label:String)
    private fun location(folder:String) = DocumentsContract.buildDocumentUri(
        "com.android.externalstorage.documents", "primary:$folder")
    private fun view(folder:String)=Intent(Intent.ACTION_VIEW)
        .setDataAndType(location(folder),DocumentsContract.Document.MIME_TYPE_DIR)
    private fun browse(folder:String)=Intent(Intent.ACTION_OPEN_DOCUMENT_TREE)
        .putExtra(DocumentsContract.EXTRA_INITIAL_URI,location(folder))

    fun managers(context:Context):List<Manager> {
        val pm=context.packageManager
        return (pm.queryIntentActivities(view(folders[0]),PackageManager.MATCH_DEFAULT_ONLY) +
            pm.queryIntentActivities(browse(folders[0]),PackageManager.MATCH_DEFAULT_ONLY))
            .map { Manager(it.activityInfo.packageName,it.loadLabel(pm).toString()) }
            .distinctBy { it.packageName }.sortedBy { it.label }
    }

    fun open(context:Context,folder:String,packageName:String) {
        if(folder !in folders) { toast(context,"保存目录无效");return }
        val intents = if(packageName.isBlank()) listOf(browse(folder),view(folder))
            else listOf(view(folder).setPackage(packageName),browse(folder).setPackage(packageName))
        for(intent in intents) {
            try {
                context.startActivity(intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                return
            } catch (_:android.content.ActivityNotFoundException) {
            } catch (_:SecurityException) {
            }
        }
        toast(context,if(packageName.isBlank()) "没有可用的文件浏览器" else "所选文件管理器无法打开目录，请更换或使用系统文件")
    }
}
