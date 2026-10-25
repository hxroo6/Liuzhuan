package com.liuzhuan.checks.locks

import com.liuzhuan.app.DurableTransfers
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicInteger

fun main() {
    val method=DurableTransfers::class.java.declaredMethods.single {it.name=="withTaskLock"}.apply {isAccessible=true}
    val locks=DurableTransfers::class.java.getDeclaredField("locks").apply {isAccessible=true}
    val inside=AtomicInteger();val overlap=AtomicInteger();val pool=Executors.newFixedThreadPool(4)
    try {
        val futures=(0 until 4).map {worker->pool.submit {
            repeat(500){index->
                method.invoke(DurableTransfers,"same-task",{if(inside.incrementAndGet()!=1)overlap.incrementAndGet();Thread.yield();inside.decrementAndGet();Unit})
                method.invoke(DurableTransfers,"retired-$worker-$index",{Unit})
            }
        }}
        futures.forEach {it.get()}
        check(overlap.get()==0){"same task acquired different simultaneous locks"}
        println("PASS 2000 contended acquisitions preserve one task monitor")
        check((locks.get(DurableTransfers) as Map<*,*>).isEmpty()){"unused lock nodes were retained"}
        println("PASS 2000 retired task locks leave no historical lock map entries")
    }finally {pool.shutdownNow()}
}
