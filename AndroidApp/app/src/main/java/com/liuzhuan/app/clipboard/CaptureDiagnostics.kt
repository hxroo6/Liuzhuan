package com.liuzhuan.app.clipboard

/** 高频候选细节仅在查看诊断时启用；捕获/发送/错误仍保留关键日志。 */
object CaptureDiagnostics { @Volatile var verbose = false }
