package com.liuzhuan.app

/** Display classification only; never changes a transfer, retry callback, or durable descriptor. */
enum class TransferTaskStage { Active, Attention, Completed, Cancelled }
enum class TransferTaskFilter { All, Active, Attention, Finished }
enum class TransferTaskDirection { All, ToComputer, ToPhone }

data class TransferTaskSummary(val total:Int,val active:Int,val attention:Int,val completed:Int,val cancelled:Int) {
    val finished:Int get()=completed+cancelled
}
data class TransferTaskQueryResult(val visible:List<TransferTask>,val summary:TransferTaskSummary)

object TransferTaskQuery {
    fun stage(task:TransferTask):TransferTaskStage=when {
        // Pending stop still owns IO; a request to stop is not a completed cancellation.
        task.stopping -> TransferTaskStage.Active
        task.cancelled || task.state=="已取消" -> TransferTaskStage.Cancelled
        task.paused || task.failed -> TransferTaskStage.Attention
        task.running -> TransferTaskStage.Active
        task.pending -> TransferTaskStage.Attention
        TransferTaskStates.isCompleted(task.state) -> TransferTaskStage.Completed
        else -> TransferTaskStage.Attention // Waiting restore, expiry, and unknown states need inspection.
    }
    fun direction(value:String):TransferTaskDirection=when(value.filterNot(Char::isWhitespace)) {
        "手机→电脑","手机->电脑" -> TransferTaskDirection.ToComputer
        "电脑→手机","电脑->手机" -> TransferTaskDirection.ToPhone
        else -> TransferTaskDirection.All
    }
    fun evaluate(entries:List<TransferTask>,filter:TransferTaskFilter=TransferTaskFilter.All,
                 direction:TransferTaskDirection=TransferTaskDirection.All,search:String=""):TransferTaskQueryResult {
        val keyword=search.trim().take(256)
        val visible=ArrayList<TransferTask>()
        var active=0;var attention=0;var completed=0;var cancelled=0
        entries.forEach {task->
            val stage=stage(task)
            when(stage) {
                TransferTaskStage.Active -> active++
                TransferTaskStage.Attention -> attention++
                TransferTaskStage.Completed -> completed++
                TransferTaskStage.Cancelled -> cancelled++
            }
            val matchesStage=when(filter) {
                TransferTaskFilter.All -> true
                TransferTaskFilter.Active -> stage==TransferTaskStage.Active
                TransferTaskFilter.Attention -> stage==TransferTaskStage.Attention
                TransferTaskFilter.Finished -> stage==TransferTaskStage.Completed || stage==TransferTaskStage.Cancelled
            }
            // Error messages, local paths, IDs, and received contents are intentionally excluded.
            if(matchesStage && (direction==TransferTaskDirection.All || this.direction(task.direction)==direction) &&
                (keyword.isEmpty() || task.name.contains(keyword,ignoreCase=true) || task.direction.contains(keyword,ignoreCase=true) || task.state.contains(keyword,ignoreCase=true)))visible.add(task)
        }
        return TransferTaskQueryResult(visible.toList(),TransferTaskSummary(entries.size,active,attention,completed,cancelled))
    }
}
