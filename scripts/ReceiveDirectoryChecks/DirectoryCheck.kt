package com.liuzhuan.app

import android.content.Context
import android.content.Intent
import android.content.pm.*
import java.io.File

private val messages=mutableListOf<String>()
fun toast(context:Context,message:String) { messages+=message }
private var checks=0
private fun expect(ok:Boolean,message:String) { check(ok){message};checks++ }
private fun handler(pkg:String,system:Boolean)=ResolveInfo(ActivityInfo(pkg,"$pkg.Files"),system,pkg)

fun main(args:Array<String>) {
    val pm=PackageManager(listOf(handler("third.party",false),handler("vendor.files",true),handler("com.android.documentsui",true)))
    val context=Context(pm)
    ReceivedFiles.open(context,ReceivedFiles.FOLDER,"")
    expect(context.launches.size==1,"default launches directly once")
    val direct=context.launches.single()
    expect(direct.action==Intent.ACTION_VIEW,"default uses browsing action")
    expect(direct.type=="vnd.android.document/directory","directory MIME")
    expect(direct.data=="content://com.android.externalstorage.documents/document/primary:Download/Liuzhuan","fixed folder target")
    expect(direct.component?.packageName=="com.android.documentsui","system DocumentsUI preferred")
    expect(pm.queries.single().second and PackageManager.MATCH_SYSTEM_ONLY!=0,"default excludes third-party apps")
    expect(direct.flags and Intent.FLAG_ACTIVITY_NEW_TASK!=0,"application context supported")

    val retry=Context(pm).apply { deny={it.component?.packageName=="com.android.documentsui"} }
    ReceivedFiles.open(retry,ReceivedFiles.FOLDER,"")
    expect(retry.launches.map { it.component?.packageName }==listOf("com.android.documentsui","vendor.files"),"fallback stays in system browsers")
    expect(retry.launches.all { it.action==Intent.ACTION_VIEW },"no picker fallback")
    val unavailable=Context(PackageManager(listOf(handler("third.party",false))))
    messages.clear()
    ReceivedFiles.open(unavailable,ReceivedFiles.FOLDER,"")
    expect(unavailable.launches.isEmpty() && messages.single().contains("更换应用"),"no system browser gives explicit remedy")

    val custom=Context(pm)
    ReceivedFiles.open(custom,ReceivedFiles.FOLDER,"third.party")
    expect(custom.launches.single().packageName=="third.party","explicit custom manager respected")
    val denied=Context(pm).apply { deny={true} }
    messages.clear()
    ReceivedFiles.open(denied,ReceivedFiles.FOLDER,"missing.app")
    expect(denied.launches.size==1 && messages.single().contains("无法打开"),"missing custom manager gives feedback without picker")
    listOf("Pictures/Liuzhuan","Movies/Liuzhuan","Music/Liuzhuan").forEach { folder ->
        val old=Context(pm)
        ReceivedFiles.open(old,folder,"")
        expect(old.launches.single().data!!.endsWith("primary:$folder"),"legacy location preserved: $folder")
    }
    val invalid=Context(pm)
    ReceivedFiles.open(invalid,"Download/../Other","")
    expect(invalid.launches.isEmpty(),"reject arbitrary folder target")
    expect(ReceivedFiles.managers(context).map{it.packageName}.toSet()==setOf("third.party","vendor.files","com.android.documentsui"),"compatible custom managers listed")

    // Check production call sites, not a duplicated storage implementation.
    val root=File(args.single())
    val src=File(root,"AndroidApp/app/src/main/java/com/liuzhuan/app")
    val save=File(src,"MainActivity.kt").readText().substringAfter("private suspend fun downloadToMediaStore(").substringBefore("private fun mimeFor(")
    expect(save.contains("MediaStore.Downloads.getContentUri") && !save.contains("when(type)"),"all incoming types use Downloads")
    expect(save.contains("put(MediaStore.MediaColumns.RELATIVE_PATH,ReceivedFiles.FOLDER)"),"storage uses unified folder")
    expect(save.contains("TransferTasks.savedTo(taskId,ReceivedFiles.FOLDER)"),"task shows actual shared folder")
    expect(save.contains("IS_PENDING,1") && save.contains("IS_PENDING,0") && save.contains("resolver.delete(uri,null,null)"),"pending visibility and incomplete cleanup kept")
    val ui=File(src,"TransferUi.kt").readText()
    expect(ui.contains("ReceivedFiles.open(context,ReceivedFiles.FOLDER,settings.fileManagerPackage)") && !ui.contains("folderPicker"),"button opens fixed directory without folder selector")
    val manifest=File(root,"AndroidApp/app/src/main/AndroidManifest.xml").readText()
    expect(!manifest.contains("OPEN_DOCUMENT_TREE") && !File(src,"ReceivedFiles.kt").readText().contains("OPEN_DOCUMENT_TREE"),"no tree-picker action or visibility query")
    println("PASS: $checks receive-directory checks (production intent dispatch with Android API test doubles; no device claim)")
}
