

# ===== root 变体（app_process 入口 + Class.forName 反射，不可混淆）=====
-keep class com.liuzhuan.app.root.ClipDaemon { public static void main(java.lang.String[]); }
-keep class com.liuzhuan.app.root.RootMonitorImpl { *; }
