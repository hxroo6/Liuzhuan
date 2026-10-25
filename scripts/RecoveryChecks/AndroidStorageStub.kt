package com.liuzhuan.app.core
import org.json.JSONObject
object FixtureDownloadBarrier {
    val entered=java.util.concurrent.CountDownLatch(1)
    val release=java.util.concurrent.CountDownLatch(1)
    val writes=java.util.concurrent.atomic.AtomicInteger()
}
/** Replaces AndroidKeyStore/AtomicFile with fsynced local test IO; encryption is not claimed by this fixture. */
class RecoveryStorage(context:android.content.Context,name:String) {
    private val file=java.io.File(context.noBackupFilesDir,"records/$name.json")
    fun read():String?=if(file.exists())file.readText() else null
    fun write(value:String) {
        if(System.getProperty("fixture.blockDownload")=="true" && value.startsWith("{") && JSONObject(value).optString("direction")=="download" && FixtureDownloadBarrier.writes.incrementAndGet()==1) {
            FixtureDownloadBarrier.entered.countDown()
            check(FixtureDownloadBarrier.release.await(5,java.util.concurrent.TimeUnit.SECONDS)){"Download reservation fixture timed out"}
        }
        if(System.getProperty("fixture.cancelOnPersist")!=null && value.startsWith("{") && JSONObject(value).optString("localId")==System.getProperty("fixture.cancelOnPersist")) {
            val marker=java.io.File(file.parentFile,"prepare-cancel-injected")
            if(!marker.exists()){marker.parentFile.mkdirs();marker.writeText("yes");com.liuzhuan.app.TransferTasks.cancel(JSONObject(value).getString("localId"))}
        }
        if(System.getProperty("fixture.failPublished")=="true" && value.startsWith("{") && JSONObject(value).optBoolean("published")) {
            val marker=java.io.File(file.parentFile,"published-crash-injected")
            if(!marker.exists()){marker.parentFile.mkdirs();marker.writeText("yes");throw java.io.IOException("injected failure after MediaStore publication")}
        }
        file.parentFile.mkdirs();val tmp=java.io.File(file.path+".tmp")
        tmp.outputStream().use {it.write(value.toByteArray());it.fd.sync()}
        java.nio.file.Files.move(tmp.toPath(),file.toPath(),java.nio.file.StandardCopyOption.REPLACE_EXISTING,java.nio.file.StandardCopyOption.ATOMIC_MOVE)
    }
    fun delete(){file.delete()}
}
