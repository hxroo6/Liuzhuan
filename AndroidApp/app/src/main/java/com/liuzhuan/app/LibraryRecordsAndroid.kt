package com.liuzhuan.app

import android.content.Context
import com.liuzhuan.app.core.LibraryRecords
import com.liuzhuan.app.core.RecoveryStorage
import java.io.File

/** No Context access until LanController.init after application startup. */
internal class EncryptedLibraryRecords(context:Context):LibraryRecords {
    private val app=context.applicationContext
    private fun store(name:String):RecoveryStorage {
        require(Regex("library-r05-[a-zA-Z0-9-]{1,150}").matches(name))
        return RecoveryStorage(app,name)
    }
    override fun read(name:String)=store(name).read()
    override fun write(name:String,text:String)=store(name).write(text)
    override fun delete(name:String)=store(name).delete()
    override fun names()=File(app.noBackupFilesDir,"recovery").listFiles()?.filter {it.isFile&&Regex(".*\\.enc(?:\\.bak|\\.new)?").matches(it.name)}
        ?.map {it.name.substringBefore(".enc")}?.filter {it.startsWith("library-r05-")}?.distinct()?:emptyList()
}
