package android.provider

object DocumentsContract {
    fun buildDocumentUri(authority:String,id:String)="content://$authority/document/$id"
    object Document { const val MIME_TYPE_DIR="vnd.android.document/directory" }
}
