package com.liuzhuan.app.core
import android.content.Context
import java.io.File
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.util.concurrent.ConcurrentHashMap
/** Windows cannot replace a history file held open by the polling fixture. Keep all
 * fixture readers and atomic replacements on one path gate, matching serialized
 * record access rather than introducing a Windows-only sharing failure. */
object FixtureHistoryIo {
    private val gates=ConcurrentHashMap<String,Any>()
    private fun gate(file:File)=gates.computeIfAbsent(file.absoluteFile.normalize().path.lowercase(java.util.Locale.ROOT)){Any()}
    fun <T> withReader(file:File,action:(java.io.InputStream)->T):T=synchronized(gate(file)) {file.inputStream().use(action)}
    fun read(file:File):String?=synchronized(gate(file)) {if(file.exists())file.readText()else null}
    fun write(file:File,value:String)=synchronized(gate(file)) {
        file.parentFile.mkdirs()
        val temporary=File(file.path+".tmp")
        temporary.writeText(value)
        Files.move(temporary.toPath(),file.toPath(),StandardCopyOption.REPLACE_EXISTING,StandardCopyOption.ATOMIC_MOVE)
        Unit
    }
}
/** Plain isolated record IO only; does not stand in for device KeyStore acceptance. */
class RecoveryStorage(context:Context,name:String) {
    private val file=File(context.root,"$name.json")
    fun read():String?=FixtureHistoryIo.read(file)
    fun write(value:String)=FixtureHistoryIo.write(file,value)
}
