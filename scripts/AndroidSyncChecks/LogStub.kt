package android.util

/** 隔离检查只编译素材仓库，不启动 Android 或写用户日志。 */
object Log {
    fun d(tag: String, message: String): Int = 0
    fun w(tag: String, message: String): Int = 0
}
