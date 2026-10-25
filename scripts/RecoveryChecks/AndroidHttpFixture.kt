package com.liuzhuan.app.net

/** Real HTTP with a small deterministic link delay, so pause tests do not race localhost throughput. */
object HttpClients {
    val transfer=okhttp3.OkHttpClient.Builder().readTimeout(180,java.util.concurrent.TimeUnit.SECONDS)
        .addInterceptor {chain->
            if(chain.request().url.queryParameter("op")=="chunk")Thread.sleep(35)
            chain.proceed(chain.request())
        }.build()
}
