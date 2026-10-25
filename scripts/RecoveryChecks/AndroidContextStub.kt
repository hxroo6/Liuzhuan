package android.content
import android.net.Uri
import java.io.File
import org.json.JSONObject

/** JVM fixture models only platform IO. All recovery state machines and HTTP IO are production code. */
class Context(val noBackupFilesDir:File) {
    val applicationContext get()=this
    val contentResolver=ContentResolver(File(noBackupFilesDir,"media"))
    fun getSharedPreferences(name:String,mode:Int)=Prefs(File(noBackupFilesDir,"$name.json"))
    companion object {const val MODE_PRIVATE=0}
}
class Prefs(private val file:File) {
    private val d=if(file.exists())JSONObject(file.readText()) else JSONObject()
    fun getString(key:String,default:String?):String?=if(d.isNull(key))default else d.getString(key)
    fun edit()=this
    fun putString(key:String,value:String)=apply {d.put(key,value)}
    fun commit():Boolean {file.parentFile.mkdirs();file.writeText(d.toString());return true}
}
class ContentValues {
    val values=mutableMapOf<String,Any>()
    fun put(key:String,value:String){values[key]=value}
    fun put(key:String,value:Int){values[key]=value}
}
class Cursor(private val rows:List<Map<String,Any>>,private val columns:List<String>):java.io.Closeable {
    private var index=-1
    fun moveToFirst():Boolean {index=0;return rows.isNotEmpty()}
    fun getColumnIndex(name:String)=columns.indexOf(name)
    fun getString(column:Int)=rows[index][columns[column]] as? String
    fun getLong(column:Int)=(rows[index][columns[column]] as Number).toLong()
    fun getInt(column:Int)=(rows[index][columns[column]] as Number).toInt()
    fun isNull(column:Int)=rows[index][columns[column]]==null
    override fun close(){}
}
class ContentResolver(private val root:File) {
    init {root.mkdirs()}
    private fun source(uri:Uri)=File(java.net.URI(uri.value))
    private fun meta(id:String)=File(root,"$id.json")
    private fun data(id:String)=File(root,"$id.bin")
    private fun id(uri:Uri)=uri.value.substringAfterLast('/')
    private fun read(id:String):Map<String,Any> {val d=JSONObject(meta(id).readText());return mapOf("id" to id.toLong(),"name" to d.getString("name"),"pending" to d.getInt("pending"),"path" to d.getString("path"))}
    fun query(uri:Uri,projection:Array<String>?,selection:String?,args:Array<String>?,sort:String?):Cursor {
        val rows=if(uri.value.startsWith("file:")){val f=source(uri);listOf(mapOf("name" to f.name,"size" to f.length()))}
        else if(uri.value=="content://downloads")root.listFiles()!!.filter {it.extension=="json"}.map {read(it.nameWithoutExtension)}.filter {args==null || it["name"]==args[0] && it["path"]==args[1] && it["pending"]==1}
        else if(meta(id(uri)).exists())listOf(read(id(uri))) else emptyList()
        return Cursor(rows,projection?.toList() ?: listOf("name","size"))
    }
    fun openInputStream(uri:Uri)=if(uri.value.startsWith("file:"))source(uri).inputStream() else data(id(uri)).inputStream()
    fun openFileDescriptor(uri:Uri,mode:String)=android.os.ParcelFileDescriptor(data(id(uri)))
    fun insert(uri:Uri,values:ContentValues):Uri {
        val id=(root.listFiles()!!.filter {it.extension=="json"}.mapNotNull {it.nameWithoutExtension.toLongOrNull()}.maxOrNull() ?: 0)+1
        val d=JSONObject();values.values.forEach {(k,v)->d.put(k,if(k=="path")v.toString().trimEnd('/')+"/" else v)}
        meta(id.toString()).writeText(d.toString());data(id.toString()).writeBytes(ByteArray(0));return Uri("content://downloads/$id")
    }
    fun update(uri:Uri,values:ContentValues,selection:String?,args:Array<String>?):Int {
        val d=JSONObject(meta(id(uri)).readText());values.values.forEach {(k,v)->d.put(k,v)};meta(id(uri)).writeText(d.toString());return 1
    }
    fun delete(uri:Uri,selection:String?,args:Array<String>?):Int {meta(id(uri)).delete();data(id(uri)).delete();return 1}
}
object ContentUris {fun withAppendedId(uri:Uri,id:Long)=Uri(uri.value+"/$id")}
