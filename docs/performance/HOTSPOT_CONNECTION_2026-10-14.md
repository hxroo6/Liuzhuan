# M37 手机热点连接修复

## 现场证据

Android v1.8.4 / versionCode 9 的手机开启 Wi-Fi 热点，电脑已经加入该热点。USB ADB 保持调试；手机热点接口存在，电脑地址和扫码保存的目标在同一热点子网。手机直接访问电脑 TCP 8899、8900 均成功，电脑监听正常且已有防火墙放行。

ConnectivityManager 当时只报告蜂窝上游 NetworkAgent，流转日志却输出「暂无可用局域网」「等待局域网恢复后连接」。根因是 LanController 只把 Wi-Fi 客户端 / 以太网 / VPN 看作局域网，遗漏手机作为热点提供方的状态；扫码正确不代表能绕过这个门禁。

先前为了无线调试切回普通 Wi-Fi，手机一度保留旧热点地址造成连接超时；那是调试过程中切换子网的现象，不能代替此次热点现场根因。现场设备地址、配对码、Wi-Fi 名称、口令和完整诊断日志不进入公开记录。

## 实现

- 使用 Android 16 公共 TetheringManager 回调，合并当前 Wi-Fi 共享接口与原有 ConnectivityManager 网络状态；注册即得到已有热点，开启/关闭由事件更新。
- LanRouteState 串行合并回调、抑制重复发布；失去 Wi-Fi 客户端不覆盖仍开启的热点，热点关闭不覆盖仍有效的普通网络。普通蜂窝数据本身仍不启用局域网重连。
- 只使用已有 ACCESS_NETWORK_STATE 权限，保留进程级作用域、主动暂停、前台服务条件、剪贴板 Hook、签名与两端协议。不改手机/电脑防火墙，不引入轮询。

参考：[Android TetheringManager](https://developer.android.com/reference/android/net/TetheringManager)与[热点接口变化回调](https://developer.android.com/reference/android/net/TetheringManager.TetheringEventCallback)，API 36 与项目 minSdk 36 一致。

## 检查与交付

- 9 项新增网络状态回归，合计 46 项功耗/传输检查与 28 项生产素材仓库同步检查通过（74 项）。
- Android assembleDebug / assembleDistribution / lintDebug 通过（2m 22s），lint 0 错误、18 警告、2 信息；54 个源码文件与 ASCII 编译副本哈希一致。正式 APK v2 验签通过、证书与 M36 一致，非调试标记、模块与接收 action 检查通过。
- 新交付为 Android v1.8.5 / versionCode 10，M37 不可调试 Distribution，沿用正式签名。旧 M36 APK 保留可回退；电脑程序本轮不变。

本轮仅本地交付，不推送 GitHub 或发布 Releases。覆盖安装后，到无障碍设置关闭→重新开启流转服务。

## 实机结果

USB 覆盖安装 v1.8.5 / versionCode 10 成功，保留当前配对信息。在手机热点仍开启、电脑仍连接该热点的现场，使用已扫码保存的连接目标重新连接，手机显示已连接；电脑确认来自热点网关的 WebSocket 建立。这次验证走与扫码共享的连接入口，没有冒称重新使用摄像头扫码通过。

APK：`AndroidApp/Liuzhuan-M37-signed.apk`，生成时间（Asia/Shanghai）2026-10-14 20:22:48；SHA-256：`93BBF952715A41725C89F9EA93A9289DD5CAC6733D9DE4F126225370FCF62725`。未进行 GB 级速度、长时间功耗或所有 ROM 验收。
