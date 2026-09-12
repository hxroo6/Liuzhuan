import com.liuzhuan.app.TransferTasks

fun main() {
    var attempts=0
    fun begin() { TransferTasks.start("sample","upload","retry") { attempts++;TransferTasks.start("sample","upload","retry") } }
    begin()
    TransferTasks.progress("retry",10,100)
    TransferTasks.finish("retry",false,"network error")
    TransferTasks.retry("retry")
    check(attempts==1 && TransferTasks.running("retry"))
    TransferTasks.retry("retry")
    check(attempts==1)
    TransferTasks.finish("retry",true,"saved")
    check(!TransferTasks.canRetry("retry") && TransferTasks.tasks.value.first().state=="saved")
    TransferTasks.start("unavailable","upload","throwing") { error("file permission lost") }
    TransferTasks.finish("throwing",false,"failed")
    TransferTasks.retry("throwing")
    check(TransferTasks.tasks.value.first().failed && TransferTasks.tasks.value.first().error=="file permission lost")
    TransferTasks.start("active","upload","active")
    repeat(60) { val id=TransferTasks.start("done-$it","upload");TransferTasks.finish(id,true,"saved") }
    check(TransferTasks.tasks.value.size<=40 && TransferTasks.running("active"))
    println("PASS retry starts once, completion clears retry, history bounded without removing active tasks")
}
