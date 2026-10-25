import com.liuzhuan.app.*

private var checks=0
private fun verify(ok:Boolean,label:String){check(ok){label};checks++;println("PASS $label")}

fun main() {
    fun task(id:String,state:String="传输中",running:Boolean=true,failed:Boolean=false,paused:Boolean=false,cancelled:Boolean=false,
             pending:Boolean=false,stopping:Boolean=false,direction:String="手机 → 电脑",name:String=id)=
        TransferTask(id,name,direction,state=state,running=running,failed=failed,paused=paused,cancelled=cancelled,pending=pending,stopping=stopping,started=1)
    val active=task("active",name="Document.PDF")
    val paused=task("paused",state="已暂停",paused=true)
    val failed=task("failed",state="等待恢复",running=false,failed=true,pending=true)
    val expired=task("expired",state="续传已过期",running=false,failed=true)
    val waiting=task("waiting",state="等待原电脑恢复",running=false,pending=true)
    val completed=task("completed",state="电脑已接收",running=false)
    val cancelled=task("cancelled",state="已取消",running=false,cancelled=true)
    val cancelling=task("cancelling",state="正在取消…",cancelled=true,stopping=true)
    val receiving=task("receiving",direction="电脑 → 手机",name="截图素材.png")
    val rows=listOf(active,paused,failed,expired,waiting,completed,cancelled,cancelling,receiving)
    val all=TransferTaskQuery.evaluate(rows)
    verify(all.summary==TransferTaskSummary(9,3,4,1,1),"summary uses disjoint stages instead of double-counting running/pending/failed flags")
    verify(all.summary.active+all.summary.attention+all.summary.finished==all.summary.total,"active, attention, and finished partition every task exactly once")
    verify(TransferTaskQuery.stage(paused)==TransferTaskStage.Attention,"paused tasks need attention even when an older row still has running set")
    verify(TransferTaskQuery.stage(cancelling)==TransferTaskStage.Active,"requested cancellation remains active until underlying stop finishes")
    verify(TransferTaskQuery.stage(paused.copy(stopping=true))==TransferTaskStage.Active,"requested pause remains active while IO cleanup is still stopping")
    verify(TransferTaskQuery.evaluate(rows,TransferTaskFilter.Attention).visible.map{it.id}==listOf("paused","failed","expired","waiting"),"attention includes paused, failed, expired, and waiting recovery in original order")
    verify(TransferTaskQuery.evaluate(rows,TransferTaskFilter.Finished).visible.map{it.id}==listOf("completed","cancelled"),"finished includes successful and cancelled rows without calling cancellation a success")
    verify(TransferTaskQuery.evaluate(rows,TransferTaskFilter.Active).visible.map{it.id}==listOf("active","cancelling","receiving"),"active filter excludes waiting and paused rows")
    verify(TransferTaskQuery.evaluate(rows,search=" document.pdf ").visible.map{it.id}==listOf("active"),"name search is trimmed and case insensitive")
    verify(TransferTaskQuery.evaluate(rows,search="截图").visible.map{it.id}==listOf("receiving"),"Unicode task names remain searchable")
    verify(TransferTaskQuery.evaluate(rows,search="等待恢复").visible.map{it.id}==listOf("failed"),"state search uses the status actually displayed to the user")
    verify(TransferTaskQuery.evaluate(rows,search="电脑 → 手机").visible.map{it.id}==listOf("receiving"),"direction text is searchable without indexing errors or paths")
    val privateRow=task("opaque-id-private-marker",name="ordinary",state="失败",running=false,failed=true)
        .copy(error="private-marker internal connection detail",savedFolder="private-marker/saved/path")
    verify(TransferTaskQuery.evaluate(listOf(privateRow),search="private-marker").visible.isEmpty(),"default search excludes private error, saved path, and opaque ID fields")
    verify(TransferTaskQuery.evaluate(rows,direction=TransferTaskDirection.ToPhone).visible.map{it.id}==listOf("receiving"),"receive-direction filter matches only computer-to-phone tasks")
    verify(TransferTaskQuery.direction(" 手机→电脑 ")==TransferTaskDirection.ToComputer && TransferTaskQuery.direction("电脑->手机")==TransferTaskDirection.ToPhone,"historical direction spacing is normalized")
    verify(TransferTaskQuery.evaluate(rows,TransferTaskFilter.Attention,TransferTaskDirection.ToPhone).visible.isEmpty(),"direction, status, and search constraints compose without changing tasks")
    verify(TransferTaskQuery.evaluate(rows,TransferTaskFilter.Finished,search="completed").summary==all.summary,"global summary stays accurate while visible results are filtered")
    verify(TransferTaskQuery.evaluate(rows,search="   ").visible==rows,"blank search preserves source order and every task")
    val unknown=task("unknown",state="unrecognized state",running=false)
    verify(TransferTaskQuery.stage(unknown)==TransferTaskStage.Attention,"unknown nonrunning states remain visible for inspection instead of appearing completed")
    verify(TransferTaskQuery.stage(completed.copy(failed=true))==TransferTaskStage.Attention,"inconsistent failed flags cannot be presented as successful completion")
    verify(TransferTaskQuery.stage(task("legacy",state="已保存到手机",running=false))==TransferTaskStage.Completed,"legacy receive success shares the central completed-state policy")
    verify(TransferTaskQuery.evaluate(emptyList()).summary==TransferTaskSummary(0,0,0,0,0),"empty library has an accurate zero summary")
    val snapshots=rows.toList();TransferTaskQuery.evaluate(rows,TransferTaskFilter.Attention,search="等待");verify(rows==snapshots,"queries never mutate task state or trigger control callbacks")
    val scale=List(1000){index->if(index%2==0)active.copy(id="scale-$index")else completed.copy(id="scale-$index")}
    val scaleResult=TransferTaskQuery.evaluate(scale,TransferTaskFilter.Finished)
    verify(scaleResult.summary.total==1000 && scaleResult.summary.completed==500 && scaleResult.visible.size==500,"retained 1000-row task history produces exact counts and filtered membership")
    println("All $checks transfer query checks passed")
}
