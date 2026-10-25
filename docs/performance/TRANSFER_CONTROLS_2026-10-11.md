# M35 收发界面、保存位置与任务控制

## 后续修订

Android M36 / v1.8.4 已将所有新接收文件改为固定 `Download/Liuzhuan`，目录按钮改为直接调用文件管理器浏览，不再调用目录选择器。下文保留 M35 交付时的测试与设计记录，安卓目录行为以 [M36 修订记录](RECEIVE_DIRECTORY_2026-10-11.md)为准。

2026-10-11，本地 Android v1.8.3 / versionCode 8，PC v1.8.1。修改前备份：`backup/1011-1150-transfer-controls/`（含 Android 源码、PC 源码、原 app 发布目录、文档和相关检查脚本）。不更改 HTTP/WS 协议，不移动现有素材，不推送 GitHub 或发布 Releases。

## 完成的交互

- 安卓收发页改为全屏，可返回关闭，增加全部/进行中/已结束筛选；暂停任务归入进行中筛选，任务状态区分失败、暂停、取消和成功。
- 电脑收发窗口扩大到 840 × 680，最小 620 × 460，允许调整大小；按钮使用 36 DIP 最小高度及明确内边距。
- 两端顶部均可打开接收目录、设置文件管理器；已经成功接收的任务也有逐项「打开保存位置」。在真实写入/登记完成后记录位置，不把未完成数据当作已保存文件。
- 安卓仍保存到内部共享存储下 `Pictures/Liuzhuan`、`Movies/Liuzhuan`、`Music/Liuzhuan`、`Download/Liuzhuan`；目录入口按文件类型选择。系统默认打开目录浏览选择器，也能选已安装且声明目录/文档浏览 Intent 的应用。无需关闭 Android 文件 URI 安全检查，无需额外申请存储权限；单独保存偏好，旧连接设置快照不会覆盖它。
- 电脑接收目录根据当前有效数据目录解析 `uploads`，逐项位置使用实际原始上传文件的父目录。默认 Explorer；自定义 EXE 与参数模板支持 `{folder}`，例如 `/O /T /L={folder}`。参数先解析为独立项，再以 ArgumentList 传入目录，不经 cmd / PowerShell。模板必须包含目录占位符，引号需闭合。偏好在当前电脑 `data/file-browser.json` 保存，不向另一电脑迁移 EXE 路径。

## 暂停、取消与续传的实际范围

| 操作 | 安卓文件任务 | 电脑 HTTP 文件任务 |
| --- | --- | --- |
| 暂停 | 取消当前 Call/来源读取，等待清理并释放传输槽；保留重试来源，状态为暂停 | 在下一次 64 KiB 读写边界停住当前连接；已在途的一次读写可能先完成 |
| 继续 | 调用原任务完整重传，重置进度；同 ID 的旧 I/O 清理结束后才开始 | 在当前连接仍可用时打开门闩，继续读写；最大暂停 90 秒 |
| 取消 | 结束任务、删除由任务持有的临时缓存、移除继续入口；不删除用户已保存文件 | 取消读写及暂停等待、关闭远端流；删除未完成上传 `.part`，不登记素材 |
| 断线或进程重启 | 不能从已传字节继续；进程内失败可显式完整重传 | 不能从已传字节继续；超时/断线需要发送端重传 |

**真正断点续传尚未支持。** 当前没有 HTTP Range 偏移协商、分块上传、部分文件持久化和源文件变化校验；暂停与完整重试不会标示为这类续传。

已提交文字、已经进入电脑登记/转换的文件无法撤回。实际登记成功若与暂停/取消同时到达，界面按真实成功结果显示。电脑暂停 90 秒超时或停止 LAN 后门闩被释放，避免对端已离线但文件句柄长期占用；安卓暂停后的自动图片继续仍受原复制时间、目标和隐私规则约束。分享 URI 授权过期则需重新选择来源。

## 验证

**150 项本地检查通过：**

- 37 项 Android 功耗/传输检查：含真实阻塞 HTTP 取消、暂停释放槽位与保留来源、迟到进度不覆盖状态、重复继续只重传一次、取消暂停任务释放缓存、真实完成与暂停竞争、保存目录关联。
- 28 项生产素材仓库同步回归；排序/序号/撤回行为保留。
- 21 项真实 PC HTTP/WS 检查：原上传/下载/缩略图鉴权与字节完整性、实际网络上传暂停/继续/取消、未完成上传清理及不登记、有限暂停期限（从点击开始，包含阻塞在当前块的网络 I/O）、服务停止唤醒门闩、目录参数安全边界。
- 6 项实际 WPF 收发界面检查与离屏渲染：窗口尺寸、目录按钮、运行控制、完成条目保存位置、暂停后的继续按钮、管理器设置页保存入口。图片位于 `<local-temp>/liuzhuan-m35-ui/`；已检查渲染结果并调整按钮内边距。
- 44 项生产安卓自动回执与 HTTP 隐私边界回归、14 项生产电脑 WsHub 登记回执回归。

脚本分别为 `scripts/AndroidPowerChecks/Run-AndroidPowerChecks.ps1`、`scripts/TransferChecks`、`scripts/DesktopUiChecks`（输出目录后加 `--transfer-only`）、`scripts/AutoReceiptChecks/Run-AutoReceiptChecks.ps1`。日志保存在 `<local-temp>/liuzhuan-m35-*`。PC 构建、发布通过；Android Debug / Distribution / lintDebug 通过，Lint 无错误、18 警告与 2 信息（已有警告保留），D8 的 API 36 工具链警告仍存在。Android 53 个源文件与 ASCII 编译副本的摘要一致。

## 本地交付

| 文件 | 生成时间（Asia/Shanghai） |
| --- | --- |
| `AndroidApp/Liuzhuan-M35-signed.apk` | 2026-10-11 12:02:59 |
| `app/Liuzhuan.exe` | 2026-10-11 12:06:22 |

APK 大小 20,908,498 字节，SHA-256 `CD9B971E99F6205D7E12CFE9D466279817A0962CC98589A4DD70479D3ABC5825`；与 Distribution 输出摘要一致，APK v2 签名验证成功，证书与 M34 一致；不可调试标记、包名、版本、ClipReceiver action 与 Xposed 入口检查通过。电脑 app 为完整 self-contained win-x64 发布目录，EXE 需要同目录依赖，不是单文件包。旧 APK 与原 app 备份保留。

覆盖安装 APK 后，到无障碍设置关闭→重新开启流转服务。桌面程序替换前应退出旧程序，保留同级用户 `data`。

本轮 ADB 无已连接设备，**未安装新 APK，未验证真实手机目录跳转或第三方管理器行为**；不保证所有文件管理器理解目录定位 URI，未创建目录时可能定位上级目录。系统默认只是打开浏览选择器，不会授予或改变保存授权。依据 [Android 官方文档浏览说明](https://developer.android.com/training/data-storage/shared/documents-files) 和 [EXTRA_INITIAL_URI API](https://developer.android.com/reference/android/provider/DocumentsContract)。应用枚举按 [Android 包可见性说明](https://developer.android.com/training/package-visibility/declaring) 仅声明相关 Intent。

电脑控制已在隔离的真实 HTTP 服务验证，UI 为生产 XAML 的离屏检查；尚未用实际手机与新电脑发布目录做互操作验收，也未验证第三方 EXE 的具体启动参数。公开下载仍为已有 Release，不把本地交付冒充远端发布。
