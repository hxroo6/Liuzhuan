package com.liuzhuan.app.net

import okhttp3.OkHttpClient
import java.util.concurrent.TimeUnit

/** 派生配置共享连接池；取消单次请求不关闭整个进程的网络资源。 */
object HttpClients {
    private val base = OkHttpClient.Builder().connectTimeout(5, TimeUnit.SECONDS).build()
    val socket = base.newBuilder().pingInterval(30, TimeUnit.SECONDS).build()
    val transfer = base.newBuilder().connectTimeout(10, TimeUnit.SECONDS)
        .writeTimeout(300, TimeUnit.SECONDS).readTimeout(180, TimeUnit.SECONDS).build()
    val thumbnail = base.newBuilder().readTimeout(10, TimeUnit.SECONDS)
        .callTimeout(15, TimeUnit.SECONDS).build()
}
