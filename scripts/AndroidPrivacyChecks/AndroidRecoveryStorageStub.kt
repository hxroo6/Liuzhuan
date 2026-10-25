package com.liuzhuan.app.core

/** JVM only: replaces AndroidKeyStore/AtomicFile side effects, not queue or receipt logic. */
class RecoveryStorage(context:android.content.Context,name:String) {
    private val file=java.io.File(context.applicationInfo.dataDir,"recovery/$name.json")
    fun read():String?=if(file.exists())file.readText() else null
    fun write(value:String){file.parentFile!!.mkdirs();file.writeText(value)}
}
