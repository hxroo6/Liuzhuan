package com.liuzhuan.app

import android.content.ContentProvider
import android.content.ContentValues
import android.database.Cursor
import android.database.MatrixCursor
import android.net.Uri
import android.os.ParcelFileDescriptor
import android.provider.OpenableColumns
import java.io.FileNotFoundException

/** Non-exported, individually granted read-only access to completed private originals only. */
class OfflineOriginalProvider:ContentProvider() {
    override fun onCreate():Boolean {context?.let {OfflineOriginals.init(it)};return true}
    private fun key(uri:Uri):String {
        if(uri.authority!="${context?.packageName}.offline-originals"||uri.pathSegments.size!=1||uri.query!=null||uri.fragment!=null)throw FileNotFoundException()
        return uri.pathSegments.single().also {if(!Regex("[a-f0-9]{64}").matches(it))throw FileNotFoundException()}
    }
    override fun openFile(uri:Uri,mode:String):ParcelFileDescriptor {
        if(mode!="r")throw FileNotFoundException("只允许读取")
        val file=OfflineOriginals.authorizedFile(key(uri))?:throw FileNotFoundException("原件未固定或不可读取")
        return ParcelFileDescriptor.open(file,ParcelFileDescriptor.MODE_READ_ONLY)
    }
    override fun getType(uri:Uri)=OfflineOriginals.mime(key(uri))
    override fun query(uri:Uri,projection:Array<out String>?,selection:String?,selectionArgs:Array<out String>?,sortOrder:String?):Cursor {
        val id=key(uri);val file=OfflineOriginals.authorizedFile(id)?:throw FileNotFoundException()
        val pin=OfflineOriginals.state.value.pins.firstOrNull {it.key==id}?:throw FileNotFoundException()
        val columns=projection?:arrayOf(OpenableColumns.DISPLAY_NAME,OpenableColumns.SIZE)
        return MatrixCursor(columns).apply {addRow(columns.map {when(it){OpenableColumns.DISPLAY_NAME->pin.name;OpenableColumns.SIZE->file.length();else->null}}.toTypedArray())}
    }
    override fun insert(uri:Uri,values:ContentValues?):Uri?=throw UnsupportedOperationException("只读")
    override fun update(uri:Uri,values:ContentValues?,selection:String?,selectionArgs:Array<out String>?):Int=throw UnsupportedOperationException("只读")
    override fun delete(uri:Uri,selection:String?,selectionArgs:Array<out String>?):Int=throw UnsupportedOperationException("只读")
}
