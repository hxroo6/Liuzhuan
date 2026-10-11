# M36 安卓统一接收目录

用户纠正：接收文件统一保存到一个位置，按钮只调用系统文件管理器打开位置，不选择目录。

- 新接收的全部类型使用 MediaStore.Downloads，固定内部存储 `Download/Liuzhuan`；首次下载时创建目录。旧文件不自动搬动，任务保留原位置信息。
- 收发页显示固定路径，顶部「打开接收目录」直接调用系统目录浏览器。保留更换文件管理器的设置，但不更改保存位置。
- 默认 ACTION_VIEW + `vnd.android.document/directory`；查询系统处理器并优先 DocumentsUI，使用返回的活动组件，不硬编码组件名称。禁止 ACTION_OPEN_DOCUMENT_TREE / 目录授权选择器回退；不可用时明确提示更换应用。
- 保留流式写入、IS_PENDING、内容长度校验、暂停/取消清理。两端协议、剪贴板链路、电脑产物不变。

Android 参考：[MediaStore.Downloads 与共享存储](https://developer.android.com/training/data-storage/shared/media)、[AOSP DocumentsUI 的 FilesActivity 目录 ACTION_VIEW 声明](https://android.googlesource.com/platform/packages/apps/DocumentsUI/+/refs/heads/main/AndroidManifest.xml)。浏览处理器的支持与图库索引仍取决于设备 ROM。

## 验证

- `scripts/ReceiveDirectoryChecks/Run-ReceiveDirectoryChecks.ps1`：23 项通过。编译并调用生产 ReceivedFiles，Android API 测试替身捕获实际 Intent；检查固定路径调用点、系统处理器优先、不可用时反馈、旧位置兼容、绝不回退选择器。
- ASCII 编译副本 53 个源码文件及构建配置与仓库哈希一致；assembleDebug / assembleDistribution / lintDebug 全部通过（1m 45s）。lint 0 错误、18 警告、2 提示，原工具链仍有 D8 API 36 提示，未在本轮升级工具链。
- 当前 ADB 无设备，未安装到手机，未声称系统文件管理器定位实机通过。

## 交付

Android v1.8.4 / versionCode 9：`AndroidApp/Liuzhuan-M36-signed.apk`，生成时间（Asia/Shanghai）**2026-10-11 12:20:47**，20908450 字节。

SHA-256：`A6AE704ED51E50D435D87620768D6FAA1EE50952649CF032B1803146CD10BB26`。Distribution 禁止调试，APK v2 验签通过、与 M35 签名证书一致；包名、ClipReceiver action 和 Xposed 入口检查通过。交付副本哈希与构建产物一致，保留 M35 回退包。

覆盖安装后，请到无障碍设置关闭→重新开启流转服务。仅本地交付，不推送 GitHub，不发布 Releases。
