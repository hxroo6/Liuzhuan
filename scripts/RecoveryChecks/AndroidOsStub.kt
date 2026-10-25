package android.os
object SystemClock {fun elapsedRealtime()=System.nanoTime()/1000000}
open class ParcelFileDescriptor(val file:java.io.File):java.io.Closeable {
    override fun close(){}
    class AutoCloseOutputStream(pfd:ParcelFileDescriptor):java.io.FileOutputStream(pfd.file)
}
