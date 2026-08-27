# AGENTS.md — Agent 工作规则

> 适用对象：任何接手本项目的 Coding Agent。
> 本文件只放**每次开发都必须遵守的规则**。
> 详细背景请查阅：`docs/ARCHITECTURE.md`（架构）、`docs/DECISIONS.md`（设计决策及原因）、`docs/KNOWN_ISSUES.md`（已知问题与技术债）。
>
> ⚠️ 历史资料辨析：`会话交接.md`、`logs/DEV_LOG_*.md`、`.workbuddy/` 是历史排查记录，其中部分结论已被后续修复推翻（如「无障碍未绑定」「runBlocking 卡死」），**一律不得当作当前现状依据**；现状以此仓库四份现行文档 + 源码为准。

## 一、项目速览

「流转」= 纯局域网跨设备素材中转站。PC 端（WPF/.NET 7，位于 `Liuzhuan/`）是服务器；Android 端（Kotlin + Compose，minSdk 36，位于 `AndroidApp/`）是客户端。两条核心链路必须同时可用：手机复制自动推电脑（无障碍+WS）；电脑素材实时同步手机接收页（WS 快照+增量）。

## 二、环境（全部已装于 F 盘，勿重复安装，详见 ENV.md）

| 项 | 值 |
|---|---|
| JDK | `F:/jdk-21.0.12+8`（必须 JDK 21） |
| Android SDK | `F:/Android-SDK` |
| Gradle | `F:/gradle-8.11.1`，GRADLE_USER_HOME=`F:/gradle-home` |
| 编译副本 | `F:/LiuzhuanApp`（工程路径含中文无法编译，**改完 `AndroidApp/` 后必须 cp 同步过去再编译**） |
| keystore | `AndroidApp/keystore/`（`liuzhuan.jks` + `keystore.properties`，gitignore 不入库，本地存在） |

## 三、开发流程铁律

1. **改码前先备份**：`cp -r` 目标目录到 `backup/<MMdd-HHmm>-主题/`（沿用既有惯例）。
2. **先读代码再动手**：理解调用方后再修改；不许凭现象直接下结论改代码。
3. **改完必编译**：Android 改动 → 同步到 `F:/LiuzhuanApp` 后 `assembleDebug` 通过才算完成；PC 改动 → publish 前**先停掉运行中的 Liuzhuan.exe**（锁 dll），发布后检查残留 dotnet 进程（锁 obj 会报 MSB4018）。
4. **不许凭现象宣布修好**：后台复制类问题先用诊断日志定位断点（ACC→DETECT→CAPTURE→DISPATCH→ACTION→WS）或做 A-B 对照（后台复制多条切前台：全发=队列积压，只发末条=平台焦点限制），再动手。
5. **禁止引入**：Shizuku / Root / 默认输入法方案；剪贴板轮询（2s 定时读取已被 M6 移除，勿恢复）。

## 四、代码红线（改动前的强制检查项）

以下行为有事故史或会导致静默故障，触碰前必须在 `docs/DECISIONS.md` 查原因：

1. **构造期严禁访问 Context**：任何 Service/Activity 的字段初始化里调 `getSharedPreferences` 等会 NPE 崩服务（M3 根因）。Context 访问只能在 `onCreate()` 之后。
2. **所有发送链协程作用域必须是进程级 `CoroutineScope(SupervisorJob() + Dispatchers.x)`**：改成 viewModelScope/lifecycleScope 会让后台复制失效，且编译期不会报错。涉及 `ClipboardCaptureManagerImpl`、`ClipboardActionPipeline`、`LanClient` 三处默认参数。
3. **评分模型基线不可调弱**（`CopyEventDetector.kt`）：第三方 App 的 `WINDOW_CONTENT_CHANGED` 必须 MEDIUM；SystemUI/Launcher 的弱信号必须 NONE。两个方向都改坏过/出过回归。
4. **循环回写防护不可删**：`copyToClipboard` 和 `ProcessTextActivity` 写剪贴板后必须调 `ClipboardMonitorCoordinator.markLocalText(text)`，少一处即 PC↔手机无限回环。
5. **`ClipboardMonitorCoordinator.init()` 幂等结构不可简化**：无障碍服务会被系统多次重建，去掉幂等会重复装配 pipeline 导致事件双发。
6. **前台服务停启条件**（`MainActivity.onState` 回调）：只有 AuthFailed / Paused 才能 stopService；`Disconnected`（意外断开自动重连中）停了就会释放 Wi-Fi/CPU 保活锁。
7. **协议两端成对修改**：`Proto.kt` ↔ `WsHub.cs`/`LanMessage.cs` 的 type 与 data 字段必须同步，JSON 解析失败是静默的（optString 全给默认值）。
8. **签名配置不动**：debug 使用 release 签名是刻意的（覆盖安装保住无障碍授权）。
9. **无障碍配置 XML 的事件类型集合**（`accessibility_service_config.xml`）：增删事件类型需同时核对 `CopyEventDetector` 与 `ClipboardEventDispatcher` 行为，该文件是裸配置，编译期无从校验。

## 五、Git 与交付

- **本地 commit，不 push**（用户明确要求，远程停在 GitHub v1.0.0）。commit message 用「做了什么 + 为什么」格式，每个可交付阶段一条。
- 版本演进史见 git log（M2~M13 对应提交哈希记录在 `logs/项目迁移交接文档_20260827.md` §3）。
- 交付物命名惯例：`AndroidApp/Liuzhuan-M{n}-signed.apk`（release 签名，apksigner 验签）；PC 产物 `app/Liuzhuan.exe`。历史版本保留可回退。
- 每次交付给用户的信息必须含：APK/exe **完整绝对路径 + 生成时间**，以及「覆盖安装后需到无障碍设置关闭→重新开启」提醒。

## 六、沟通约定

- 回复使用简体中文，简洁结构化。
- 用户的软件/工具一律安装到 F 盘。
- 不确定的事实标注【待确认】，不臆断。
