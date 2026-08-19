# 《流转》安卓端工程 — M1（文本同步）

> 对应开发文档：`../docs/流转安卓版开发文档.md`（v0.2 定稿）
> 阶段：M1 两端骨架 + 文本同步

## 目录结构
```
AndroidApp/
├── settings.gradle.kts          # 项目名 + 仓库
├── build.gradle.kts             # 根脚本
├── gradle.properties
├── gradle/libs.versions.toml    # 版本目录
└── app/
    ├── build.gradle.kts         # minSdk 36 / Compose / OkHttp / DataStore
    └── src/main/
        ├── AndroidManifest.xml  # 权限 + 无障碍服务声明
        ├── java/com/liuzhuan/app/
        │   ├── MainActivity.kt          # Compose UI：连接页 + 发送页
        │   ├── LanHub.kt                # 全局连接单例（供剪贴板服务共享）
        │   ├── net/LanClient.kt         # WS 连接：握手/心跳/指数退避重连
        │   ├── net/Proto.kt             # 协议消息构建/解析（org.json 零依赖）
        │   ├── core/SettingsStore.kt    # DataStore：IP/端口/口令/设备名
        │   └── clipboard/ClipMonitorService.kt  # 无障碍剪贴板监控
        └── res/
            ├── xml/accessibility_service_config.xml
            ├── drawable/ic_launcher_foreground.xml  # 闪电矢量图标
            └── mipmap-anydpi-v26/ic_launcher.xml    # 自适应图标
```

## 编译方式
1. 用 **Android Studio**（Ladybug 或更新）打开本目录
2. 等待 Gradle 同步（首次需下载依赖）
3. 连接 Android 16+ 真机（或模拟器）→ Run

> 本机无 JDK/Android SDK，工程未经编译验证。
> 若编译报错，多为依赖版本问题，按 Android Studio 提示修正即可。

## M1 功能清单
- [x] 连接页：IP/端口/口令输入 + 连接状态灯 + 断开
- [x] 握手认证（sha256(口令) + ts 防重放，与电脑端一致）
- [x] 心跳 30s + 指数退避重连（1s→30s cap）
- [x] 发送页：手动输入文字 → sync_text 到电脑
- [x] 剪贴板监控：无障碍服务 → clipboard_push（防抖 2s）
- [x] 配置持久化（DataStore）
- [ ] 前台服务保活（M3）
- [ ] 文件传输（M2）
- [ ] 分享菜单（M2）
- [ ] 图标/名称完善（M5）

## 使用流程
1. 电脑端流转：设置 ⚙ → 勾选「局域网服务器」→ 记录端口(8899) 和口令
2. 手机端：填电脑 IP + 端口 + 口令 → 连接（显示「已连接 ✓」）
3. 发送页输入文字 → 发送 → 电脑流转出现新卡片
4. 连接页点「开启监控」→ 无障碍设置授权 → 任意 App 复制文字自动同步

## 端口约定
- 控制面 WS：8899（与电脑端一致，可在手机端改）
- 数据面 HTTP：+1（M2 使用）
