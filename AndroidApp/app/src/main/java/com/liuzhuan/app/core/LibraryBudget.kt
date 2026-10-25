package com.liuzhuan.app.core

/** Bounded retained summaries; search still queries the entire PC library. */
object LibraryBudget {
    const val MAX_ROWS=1000
    fun <T> retain(rows:List<T>):Pair<List<T>,Int> {
        val dropped=(rows.size-MAX_ROWS).coerceAtLeast(0)
        return rows.drop(dropped) to dropped
    }
}
