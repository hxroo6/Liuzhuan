# 流转 · Liuzhuan

**把手机和电脑连起来，让文字、图片、文件随手流转。**

电脑上是一个贴边的悬浮素材栏，手机上是它的发送与接收端。拖入暂存、拖出使用，也能把手机当作电脑的麦克风。

[![Build](https://github.com/hxroo6/Liuzhuan/actions/workflows/build.yml/badge.svg)](https://github.com/hxroo6/Liuzhuan/actions/workflows/build.yml)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**简体中文** · [English](README_en.md) · [下载](https://github.com/hxroo6/Liuzhuan/releases/latest) · [使用指南](docs/USAGE.md) · [反馈问题](https://github.com/hxroo6/Liuzhuan/issues)

## 能做什么

| 你想做的事 | 流转怎么帮你 |
|---|---|
| 临时收集素材，跨软件使用 | 把文件、图片、文字拖入电脑悬浮栏，使用时再拖出去；支持搜索、收藏与预览 |
| 手机发给电脑 | 手动发文字、分享文件给流转，或同步复制内容；后台自动复制的条件见下方说明 |
| 电脑发给手机 | 电脑素材实时出现在手机「接收」页，点击复制文字或下载文件 |
| 整理一批素材 | 多选、分组、标签、备注、常用筛选、批量另存，以及失效来源重新定位 |
| 处理手机照片 | 接收 HEIC 后自动另存为 PNG/JPEG；也可批量缩小图片、转换格式、保存处理预设 |
| 在新电脑继续使用 | 导出一个包含素材和配置的迁移包，在新电脑导入 |
| 电脑没有麦克风 | 用手机采音，经局域网送入 Windows 虚拟麦克风，供 Codex 等软件听写；目前为实验功能 |

**连接范围：同一 Wi-Fi，或电脑连接手机热点。** 素材和音频由设备直接传输，不经过流转云端。当前没有公网／手机移动数据远程连接模式；听写软件自身是否联网，取决于该软件。

## 下载与安装

当前版本 **v1.9.0**，建议两端一起更新。

| 设备 | 下载 | 系统要求 |
|---|---|---|
| 电脑 | [Windows x64 ZIP](https://github.com/hxroo6/Liuzhuan/releases/download/v1.9.0/Liuzhuan-v1.9.0-windows-x64.zip) | Windows 10/11，64 位；解压运行，无需另装 .NET |
| 手机 | [Android APK](https://github.com/hxroo6/Liuzhuan/releases/download/v1.9.0/Liuzhuan-v1.9.0-android.apk) | **Android 16 及以上** |
| 校验与更新内容 | [SHA-256](https://github.com/hxroo6/Liuzhuan/releases/download/v1.9.0/Liuzhuan-v1.9.0-SHA256SUMS.txt) · [版本说明](docs/releases/v1.9.0.md) | 安装包从 Releases 下载，源码 ZIP 不是可运行程序 |

普通文件收发和手动发送**不需要 Root**。手机麦克风需要另装虚拟音频驱动；只用素材功能无需安装驱动。

## 第一次使用：三步连接

1. **打开电脑端。** 解压 Windows ZIP，运行 `app/Liuzhuan.exe`。在设置或托盘菜单开启「局域网服务器」，打开「配对二维码（手机扫码连接）」。防火墙询问时允许可信的专用网络。
2. **连接手机。** 两端接入同一局域网。安卓打开「连接」页，点击「扫码连接全部功能」，扫电脑二维码。此入口会同时尝试开启手机麦克风，首次可能请求录音权限；仅用素材时可不授权，也可通过「搜索电脑」填入地址，再输入电脑显示的口令连接。
3. **试发一条文字。** 手机在「发送」页手动发送文字，确认电脑素材栏出现；再拖一个文件到电脑素材栏，确认手机「接收」页出现。

成功鉴权后，电脑素材栏会展开并提示连接成功。下次可在手机「历史连接」中选择电脑；切换 Wi-Fi 或热点后地址可能变化，需要重新扫码。

[连接、权限与热点的详细说明 →](docs/USAGE.md#connect)

## 常用操作

| 场景 | 操作 |
|---|---|
| 电脑暂存与取用 | 拖入素材栏 → 点击复制或拖出；图片／文字按空格预览 |
| 多选与整理 | Ctrl/Shift 多选 →「整理…」或 F2；Ctrl+Shift+S 另存 |
| 手机发文件 | 使用「发送」页的附件入口，或在其他应用中分享给「流转」 |
| 手机保存电脑文件 | 在「接收」页点击素材下载；文件保存到 `内部存储 / Download / Liuzhuan` |
| 查看传输 | 两端点击「收发」，查看进度、暂停、继续、取消；「打开接收目录」调用文件管理器 |
| 手机复制到电脑 | 普通模式：复制后切回流转；需要后台自动推送时见下一节 |

**暂停不等于断点续传。** 安卓继续时从头重传；电脑暂停暂时保留连接，超时或断线需重传。任务记录不跨应用重启恢复。[文件收发说明 →](docs/USAGE.md#files)

### 手机后台复制：先看这一区别

| 使用方式 | 条件 | 能做到什么 |
|---|---|---|
| 普通模式 | 无需 Root；自动捕获需启用无障碍与自动同步 | 复制后切回流转发送；也可用系统文字选择菜单「流转」或分享直接发送。受 Android 限制，不能保证任意应用后台复制都自动到达 |
| LSPosed 模式 | 已 Root，并启用流转模块及目标应用作用域 | 监听目标应用的剪贴板写入，实现后台复制自动推送；启用后需重启目标应用 |

不需要后台自动复制时，直接使用手动发送和分享即可。[设置步骤与隐私排除 →](docs/USAGE.md#clipboard)

## 手机麦克风：听写与外放同时使用

**实验功能，可选开启。** 流转负责送声音，语音识别由 Codex 等听写软件完成。

```text
手机麦克风 → 流转 → CABLE Input → CABLE Output → 听写软件
电脑其他声音 → 实体扬声器／耳机
```

1. 从 [VB-Audio 官网](https://vb-audio.com/Cable/)自行安装 VB-CABLE，按安装器要求重启。流转不捆绑驱动；驱动采用[独立许可](https://vb-audio.com/Services/licensing.htm)。
2. 电脑流转点击标题旁音波图，选择 **CABLE Input** 作为输出、**CABLE Output** 作为对应录音端；不要选择 `CABLE In 16ch`，也不要勾选「仅诊断收音」。
3. 手机通过常规二维码「扫码连接全部功能」，允许录音，一次扫码完成素材和音频连接；已连接素材时，也可从左上音波图打开麦克风页，点「连接当前电脑麦克风」。
4. 听写软件使用 **CABLE Output** 输入；若软件跟随系统输入，则将 Windows 默认录音设备设为它。**Windows 默认播放和默认通信播放保持实体扬声器／耳机**，不要改成 CABLE Input。

两端音波图都可点击进入设置。关闭手机麦克风面板不会停止采音；不用时请点「结束」。安静省流量会减少音频发送，手机仍在采音，不代表麦克风已关闭。

[完整设置、暂停与结束、无声排查 →](docs/USAGE.md#microphone)

## 升级、备份与使用边界

- **升级：** 电脑退出旧版后替换整个 `app` 文件夹，保留旁边的 `data`；手机用同签名 APK 覆盖安装，之后到无障碍设置将流转服务**关闭 → 重新开启**。不要为升级先卸载手机应用。
- **换电脑：** 设置 →「迁移与备份（配置 + 素材）」→ 旧电脑导出、新电脑导入 → 重启流转 → 手机重新扫码。迁移包未加密，包含素材与配对口令，请妥善保管。[换机指南 →](docs/USAGE.md#migration)
- **网络与隐私：** 当前为明文局域网传输，配对口令不等于传输加密，仅用于可信局域网；不要把端口直接暴露到公网。自动剪贴板采集可暂停或排除应用。
- **兼容性：** HEIC 解码依赖 Windows 编解码支持；文件管理器、不同 ROM、混合 DPI、自动跟随听写与实际功耗表现仍有待进一步验证。[常见问题 →](docs/USAGE.md#troubleshooting)

## 更多资料

[中文使用指南](docs/USAGE.md) · [English guide](docs/USAGE_en.md) · [所有版本](https://github.com/hxroo6/Liuzhuan/releases) · [构建指南](BUILDING.md) · [架构](docs/ARCHITECTURE.md) · [已知问题](docs/KNOWN_ISSUES.md) · [产品路线](docs/PRODUCT_ROADMAP.md)

源码：`Liuzhuan/` 为 Windows WPF/.NET 7，`AndroidApp/` 为 Kotlin/Compose；检查脚本位于 `scripts/`。流转采用 [MIT License](LICENSE)，第三方组件遵循各自许可；MIT 许可不涵盖外部虚拟音频驱动。
