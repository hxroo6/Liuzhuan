

# ===== LSPosed 模块（xposed_init 引用 + LSPosed 反射调用 handleLoadPackage，不可混淆）=====
-keep class com.liuzhuan.app.xposed.ClipHook { *; }
# ClipReceiver 在 Manifest 声明，R8 自动 keep；其 onReceive 走 BroadcastReceiver 回调，无需额外规则
