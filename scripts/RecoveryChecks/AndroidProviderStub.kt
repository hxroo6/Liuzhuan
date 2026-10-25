package android.provider
object OpenableColumns {const val DISPLAY_NAME="name";const val SIZE="size"}
object MediaStore {
    const val VOLUME_EXTERNAL_PRIMARY="external"
    object MediaColumns {const val DISPLAY_NAME="name";const val MIME_TYPE="mime";const val RELATIVE_PATH="path";const val IS_PENDING="pending";const val _ID="id"}
    object Downloads {fun getContentUri(volume:String)=android.net.Uri("content://downloads")}
}
