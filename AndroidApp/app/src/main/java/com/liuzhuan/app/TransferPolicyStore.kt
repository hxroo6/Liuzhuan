package com.liuzhuan.app

import android.content.Context
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.channels.Channel

/** Preferences contain no credentials, payload or capture permission. Publish only after disk commit. */
object TransferPolicyStore {
    private var preferences:android.content.SharedPreferences?=null
    private val gate=Mutex()
    private val scope=CoroutineScope(SupervisorJob()+Dispatchers.IO)
    private val loaded=CompletableDeferred<Unit>()
    private val changes=Channel<(TransferPolicy)->TransferPolicy>(64)
    private val errors=MutableStateFlow("")
    val error=errors.asStateFlow()
    val state=TransferPolicyRuntime.state
    init {scope.launch {loaded.await();for(change in changes)try {update(change(state.value))}catch(_:Exception){errors.value="传输偏好未能保存，原设置仍然生效"}}}
    @Synchronized fun init(context:Context) {
        if(preferences!=null)return
        preferences=context.applicationContext.getSharedPreferences("transfer_policy",Context.MODE_PRIVATE)
        TransferPolicyRuntime.audioActive={com.liuzhuan.app.audio.MicrophoneState.status.value.let {it.connected && it.forwarding}}
        scope.launch {gate.withLock {
            try {val p=preferences!!;TransferPolicyRuntime.apply(TransferPolicy(p.getString("priority","balanced")?:"balanced",p.getInt("upload",0),p.getInt("download",0),p.getBoolean("audio",false)))}
            catch(_:Exception){errors.value="传输偏好无法读取，暂用不限速；请重新保存"}
            finally {loaded.complete(Unit)}
        }}
    }
    suspend fun update(value:TransferPolicy)=withContext(Dispatchers.IO){gate.withLock {
        val policy=value.normalized();val p=preferences?:error("传输偏好尚未初始化")
        check(p.edit().putString("priority",policy.priority).putInt("upload",policy.uploadMiB).putInt("download",policy.downloadMiB).putBoolean("audio",policy.yieldToAudio).commit()){ "传输偏好未能保存，原设置仍然生效" }
        TransferPolicyRuntime.apply(policy);errors.value=""
    }}
    fun change(transform:(TransferPolicy)->TransferPolicy){if(!changes.trySend(transform).isSuccess)errors.value="设置操作较多，请稍后重试"}
    fun save(value:TransferPolicy)=change {value}
}
