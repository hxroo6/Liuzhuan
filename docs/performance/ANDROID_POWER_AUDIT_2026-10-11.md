# Android 性能与功耗完整审查

审查日期：2026-10-11。源码基线：`7df78c842b7ba35dc99b82edba5b0f3adc416f77`，Android `1.8.1 / versionCode 6`，minSdk / targetSdk 36。

后续实施：用户确认后，已在 Android v1.8.2 / M34 落实本报告的优化，结果见 [优化交付与验证记录](ANDROID_POWER_OPTIMIZATION_2026-10-11.md)。本文保留审查时的基线问题，不代表优化后的源码现状。

本报告检查手机端生产源码的 31 个 Kotlin 文件、Manifest、构建配置及无障碍配置，沿调用链分析 CPU 保活、网络、剪贴板、文件 I/O、内存和 Compose 更新。结论以当前源码为依据，不把历史排查记录当作现状。

**结论：先优化保活锁的生命周期、失败重连和图片 Hook，再处理传输与界面更新。** 这些比缩短动画更有价值。部分问题可以在保留现有行为的前提下修复；取消长期保活等设计调整必须先做锁屏实机对照，确保后台复制仍能及时送达。

本次只做分析与文档，没有修改 Android 业务代码、安装 APK 或发布版本。检查时 `adb devices -l` 没有已连接设备；因此没有本轮电流、耗电百分比、深睡时长或帧率测量。下文数量计算是代码推导，不是实测省电幅度。

## 1. 优先级总表

P1：建议优先修复；P2：随后优化；P3：低收益细节。功耗收益均需测量，不把代码风险等同于已发生故障。

| 编号 | 优先级 | 已确认的代码问题 | 主要影响 | 实施边界 |
| --- | --- | --- | --- | --- |
| A01 | P1 | 重连成功反复创建保活锁，覆盖旧引用 | 锁缺少确定性释放，生命周期混乱 | 可先做幂等修复，保持保活策略 |
| A02 | P1 | 前台服务长期持有无超时 CPU 锁，断网也保持 | 在系统允许锁生效时压缩休眠机会 | 改锁策略须验证后台秒达 |
| A03 | P1 | 失败回调重建重连任务，退避计数回到零 | 离线期间重复连接、日志和状态刷新 | 保持服务，修复退避及网络感知 |
| A04 | P1 | Hook 先完整读取文件，再检查 512 KiB 上限 | 原 App 调用线程阻塞、内存峰值与 GC | 先改为有上限读取，仍只旁路观察 |
| A05 | P1 | 常驻 `dataSync` 服务缺少超时处理 | 长时间后台运行兼容性风险 | 核对 FGS 类型与用途，补超时清理 |
| A06 | P2 | 文件广播主线程解码，缺少文件内容去重 | 瞬时卡顿、重复上传、失败数据驻留 | 有限异步处理，保留隐私规则 |
| A07 | P2 | 高频无障碍候选重复分配、并发遍历 | 滚动第三方 App 时额外 CPU / Binder 工作 | 不降低检测评分，不丢最后一次复制 |
| A08 | P2 | 进度节流发生在整表遍历之后 | 大文件传输时无效分配和扫描 | 先判断节流，再发布进度 |
| A09 | P2 | 文件传输逐次新建 HTTP client / Thread | 连接池碎片、并发 I/O、不可及时取消 | 进程级客户端及有界传输管理 |
| A10 | P2 | 缩略图协程取消未绑定 HTTP 取消，缺少请求合并 | 离屏下载、重复解码、占用槽位 | 取消底层 Call，缓存二次检查 |
| A11 | P2 | 网络客户端随 Activity 重建，回调持有页面 | 不必要重连、连接生命周期耦合 UI | 进程级连接控制器，UI 只订阅 |
| A12 | P2 | 主界面订阅过宽，素材仓库增长后每次全量排序 | 接收素材 / 日志引起额外界面计算 | 缩小状态读取范围，保持序号语义 |
| A13 | P2 | 重试新建 SettingsStore，DataStore 委托位于实例内 | 多实例同文件冲突风险、失败重试 | 单例 DataStore，属于可靠性优先项 |
| A14 | P3 | 诊断日志、摘要编码、已完成任务超时器等 | 零散分配和额外唤醒 | 核心问题修完后再处理 |

## 2. 后台保活与连接

### A01：保活锁获取不幂等

位置：`AndroidApp/app/src/main/java/com/liuzhuan/app/ForegroundService.kt:36,44,69`；调用方 `MainActivity.kt:96`。

每次进入 `Connected` 都会调用 `startForegroundService()`；每次 `onStartCommand()` 又分别创建新的 WakeLock / WifiLock 并获取。字段直接覆盖旧对象，`onDestroy()` 只能释放最后一组对象。

`setReferenceCounted(false)` 只影响同一个 WakeLock 对象，不能解决每次创建新对象的问题。旧对象可能由 GC / 框架最终回收，不能据此断言“永远泄漏”；但代码已经失去明确的释放权。多个锁也不会让 CPU 功耗按数量线性增加，真正的问题是释放与持有时长不受业务准确控制。

建议：每个 Service 实例创建一组锁；获取前检查 `isHeld`；重复启动只更新状态；在停止和销毁时释放并清空引用。异常路径也要保留已获取资源的所有权。

验收：连续多次断开并重连，锁对象与有效持有数不增长；主动暂停后本服务持有的锁归零。此步不改变断线期间前台服务继续存在的规则。

### A02：长期 CPU 锁是待机功耗的首要候选

位置：`ForegroundService.kt:47`；`MainActivity.kt:92`。

`PARTIAL_WAKE_LOCK.acquire()` 没有超时，连接成功后一直持有。连接暂时断开时不停止前台服务，所以锁也保留。代码没有区分正常空闲、短时发送、持续大文件传输和长期离线。

这是一种以后台及时性换取休眠机会的策略，实际代价受 ROM、系统电源状态和豁免设置影响。Doze 对唤醒锁与网络另有限制，不能把“持锁”理解为任何环境下都能维持后台连接。[Android Doze 文档](https://developer.android.com/training/monitoring-device-state/doze-standby)

建议分两步：先修 A01 / A03，保持现有即时性基线；之后对照测试“即时模式”和“均衡模式”。均衡模式可对复制发送、回执等待和文件传输采用有期限且可续期的锁，长期离线不无限持锁。续期、超时和释放必须避免中断正在发送的任务。

**不要通过在 `Disconnected` 时直接 `stopService()` 实现省电。** 本项目已有明确后台链路约束：保留服务与改变锁策略是两个独立问题。也不应新增剪贴板轮询、周期唤醒闹钟、Root / Shizuku / 默认输入法方案。[Android 唤醒策略](https://developer.android.com/develop/background-work/background-tasks/awake)

### A03：指数退避被失败回调重置

位置：`net/LanClient.kt:280,291,306`。

`scheduleReconnect()` 先取消旧任务，然后在新任务内部执行 `var attempt = 0`。一次连接失败后，`onFailure()` 再次调用这个方法，因此又从等待 1 秒开始。通常 5 秒连接超时发生在重连任务的 8 秒等待期间，旧循环来不及进入下一次退避就被取消。

因此注释中的 `1 → 2 → 4 → 8 → 30 秒`，在常见持续连接失败路径上无法正常累计。此前“每次都是第 1 次重连”的现象与该路径一致；本轮没有设备日志复现。

量级示例：若每次都用满 5 秒超时再等待 1 秒，10 分钟约产生 100 次连接尝试；到达 30 秒退避上限后，稳态约为每 35 秒一次。数字仅是时序推导，真实次数还受失败方式与调度影响。

建议由一个任务拥有重连状态；失败只唤醒或更新状态，不重建并清零计数；只有成功连接、用户明确切换目标等情况重置。增加有上限的随机抖动，避免移位次数长期增长溢出。

可增加 `ConnectivityManager` 网络回调：没有可用局域网路径时等待网络变化，有路径时才尝试目标服务器。**不要要求互联网 `VALIDATED` 才允许连接**，流转支持没有外网的局域网；网络回调也不能证明某个电脑一定可达。保留当前旧 WebSocket 回调身份校验，避免旧连接扰动新连接。

### A05：Android 16 上常驻 dataSync 的设计需要调整

位置：`AndroidManifest.xml:67`；`ForegroundService.kt`。

Manifest 将服务标为 `dataSync`，服务长期运行且没有覆盖超时回调。Android 对符合目标版本条件的后台 `dataSync` 有每 24 小时累计 6 小时时限，用户将应用切回前台会重置预算；当前 target 36 在适用范围内。超时后需要在系统宽限时间内停止服务，否则可能触发系统异常。这是长时间后台稳定性风险，不是本轮已经复现的崩溃原因。[前台服务时限](https://developer.android.com/develop/background-work/services/fgs/timeout)

建议先补超时回调、资源释放及明确状态，避免 `START_STICKY` 形成超时后的反复启动。再评估局域网连接与持续外部设备交互是否应采用 `connectedDevice`，以及所需权限和前置条件；真正的数据传输任务单独管理。

不能只改 Manifest 类型绕过预算。必须核对实际用途、权限、系统启动限制与锁屏行为。[前台服务类型及 connectedDevice 条件](https://developer.android.com/develop/background-work/services/fgs/service-types#connected-device)

### Wi-Fi 锁注释与现行平台行为不一致

位置：`ForegroundService.kt:57`。

代码使用 `WIFI_MODE_FULL_HIGH_PERF`，注释认为它比 `LOW_LATENCY` 更省电，且可以防止锁屏 Wi-Fi 休眠。官方说明 HIGH_PERF 从 API 34 起弃用并映射到 LOW_LATENCY；后者的适用条件包含连接 AP、屏幕开启和应用前台等。项目 minSdk 36，不能继续沿用旧 API 的功耗与锁屏假设。[WifiManager API](https://developer.android.com/reference/android/net/wifi/WifiManager)

建议用目标手机的 `dumpsys wifi` 与 A/B 待机测试确认有效持有和实际作用，再决定是否只在前台快速传输时使用。不要未经验证换一个同样已弃用的 Wi-Fi 模式。

当前只找到 OkHttp 的 30 秒 WebSocket ping；`Proto.buildHeartbeat()` 没有生产调用。没有证据支持“同时存在两个每 30 秒心跳”的判断。保持一个清晰的心跳来源，先修失败重连再考虑延长间隔；延长间隔会影响断线发现和路由器空闲连接，需要两端验收。

## 3. 剪贴板、Hook 与无障碍

### A04：512 KiB 上限没有限制读取量

位置：`xposed/ClipHook.kt:44,74`。

Hook 在原 App 的 `setPrimaryClip` 调用线程执行；URI 分支同步调用 `readBytes()`，完整读取后才检查文件是否超过 512 KiB。复制一张大图片或视频，即使最终跳过，仍已经发生全部 I/O 与内存分配。该调用线程经常可能是 UI 线程，但源码不能证明所有 App 都在主线程复制。

建议立即采用最多读取 `MAX_FILE_BYTES + 1` 的方式，超限停止读取，不进入 Base64。可用可信度有限的长度信息提前拒绝，但仍要保留流读取硬上限。异常捕获只能避免错误传播，无法撤销已经发生的阻塞或内存峰值。

进一步异步化必须使用有界队列，并验证 URI 授权在异步执行时仍可用；不能在每次复制时无限开线程。始终保持旁路观察、不改原方法返回值、不向原 App 抛异常。

Base64 还会增加约三分之一的数据体积，并产生字符串和跨进程复制。512 KiB 原文件不是 Binder 事务可靠成功的证明，必须按实际序列化与并发事务测试。图片链路耗电也可能计入被 Hook 的微信等进程，仅看流转自身 UID 会漏掉这部分。

自动复制关闭 / 暂停规则在接收端拦截时，Hook 端可能已完成文件读取与编码。未来可评估文件 Hook 单独能力开关，但不要为此增加轮询或越权读取其他应用私有配置。

### A06：文件接收在主线程解码，且没有复用文本去重链

位置：`ClipReceiver.kt:26,77`；`net/LanClient.kt:156`；`TransferTasks.kt:17`。

文件分支在普通广播 `onReceive()` 内进行 Base64 解码，再直接调用文件上传；没有像文本一样通过 Dispatcher 的内容去重。重复的同文件广播可以形成重复解码与上传。

失败任务的重试闭包持有 `ByteArray`。历史记录通常保留最多 40 条，40 份 512 KiB 数据相当于约 20 MiB 原始字节；这是可能的驻留上界示例，不表示当前手机已占用这么多。所有记录都在运行时，淘汰条件还可能让数量超过 40。

建议使用 `goAsync()` 搭配进程级有界 I/O 处理，保证 `finish()`、超时及隐私规则复核；不要把长上传全部塞进广播执行预算。文件采用有限窗口的事件身份 / 内容去重，允许用户有意再次复制，并保留可追踪的重试语义。后续可评估授权 URI 或本应用缓存文件传递，减少 Base64，但需要保留授权失败的兼容路径。

### A07：高频无障碍事件仍有重复工作

位置：`clipboard/ClipboardCaptureManager.kt:84,118,297,321`；`AccessibilitySourcePolicy.kt:22,38`；`ClipboardEvent.kt`。

发现以下明确开销：

- 检测前创建完整 `ClipboardEvent` 做隐私判断，默认字段生成 UUID；很多事件之后会被判为 NONE / LOW。
- 同一事件先 `classify()`，再 `shouldInspect()` 内重新分类。
- MEDIUM 有 source 时每次启动协程抓取选中文本；没有并发任务总量上限。
- 先检查 `event.source`，抓取时又读取一次；节点查询可能涉及跨进程工作。
- 节点遍历实际最多 `1 + 16 + 16² = 273` 个节点，注释里的“64 节点”不准确；命中选中文本会提前退出。
- MEDIUM 兜底已采用 500 毫秒尾沿防抖，但此前的节点遍历不受这个防抖保护。

建议先做不改变检测能力的优化：隐私检查用轻量元数据，真正接受捕获后再生成请求身份；来源只分类一次；取得节点后在合法生命周期内有界使用；合并 MEDIUM 工作并保证尾沿最终捕获，HIGH 不被大量弱候选排队阻塞。

**不把第三方 `WINDOW_CONTENT_CHANGED` 从 MEDIUM 降为 NONE；不恢复会漏最后一次复制的固定节流；不全局停用无障碍。** Hook 可能没有激活、没有覆盖某个 App 或广播未到，仍需要兜底。只有在实际能力可观测且可回退时，才考虑对已确认 Hook 事件减少重复捕获。

API 33 起 `AccessibilityNodeInfo.recycle()` 已弃用且不再使用对象池，不能把当前缺少 recycle 的路径简单判断为对象池泄漏；应关注节点查询次数、持有时长与取消行为。[AccessibilityNodeInfo API](https://developer.android.com/reference/android/view/accessibility/AccessibilityNodeInfo)

### 已有机制应保留

`AutomaticClipboardDelivery` 使用单消费者与合并信号，空闲时挂起等待，回执超时有有限重试；队列有容量与时效控制。这里的 `while (true)` 是等待事件的消费循环，不是 CPU 忙轮询。

文本去重、隐私修订号、同一请求重试身份、目标隔离、`markLocalText()` 回写防环、Coordinator 幂等初始化，以及进程级 `SupervisorJob` 作用域都属于正确性保障。不能为了缩短代码或降低活跃协程数删除这些机制。

`ClipboardActionPipeline` 在同一事件内部依次执行 action，但每个事件用独立 `launch`，不同事件可以并行；源码注释不应被理解成所有事件严格串行。自动发送层另有单消费者约束。

## 4. 传输、HTTP、缩略图与内存

### A08：进度节流的位置太晚

位置：`TransferTasks.kt:25`；`MainActivity.kt:970,1041`。

每个 64 KiB 数据块都调用 `progress()`。虽然有 200 毫秒更新条件，函数仍先遍历任务列表并创建新列表，之后才判断是否需要更新某项。相等列表通常被 StateFlow 抑制，不代表每个数据块都会触发 UI 重组；但遍历和分配已经发生。

1 GiB 文件有 16,384 个 64 KiB 数据块；列表含 40 条记录时约有 655,360 次条目检查，仅用于说明代码量级。建议把活跃任务进度计数与展示列表分开，先判断节流，再发布；成功、失败和最后一个进度立即发布。保留约 200–500 毫秒的可读刷新即可。

### A09：客户端复用、并发和取消可以统一

位置：`MainActivity.kt:921,976,1009,1017`；`net/LanClient.kt:53`。

普通文件上传 / 下载每次创建 Thread 和 OkHttpClient。文件数量增加时会建立多个独立连接池，并发读写没有统一上限，也没有统一的底层 Call 取消入口。

建议建立进程级传输管理器：复用基础 OkHttpClient，用 `newBuilder()` 创建不同超时的派生配置；任务使用有界 I/O 并发，初始可测试同时 2 个传输，不把这个数量当作通用最佳值。保存 Call，取消时取消网络请求并清理半成品。不要在取消某个任务时关闭共享连接池。[OkHttp 4.12 客户端源码说明](https://github.com/square/okhttp/blob/parent-4.12.0/okhttp/src/main/kotlin/okhttp3/OkHttpClient.kt)

保留现有 64 KiB 流式传输，不能改成完整文件进内存。`MainActivity.kt:951` 的 `runBlocking` 位于专用 Thread 内，不是当前 Activity 主线程启动死锁的证据；后续可以统一改为挂起式流程以简化取消。

未知长度 URI 为获取 Content-Length 先落缓存，有实际协议用途。当前 `tmp = f` 在复制完成后才赋值：如果复制中途异常，finally 不知道已经创建的文件，存在临时文件残留路径。建议创建后立即登记所有权，处理磁盘空间和失败清理；不要取消这一兼容分支后让未知长度上传失效。

### A10：离屏缩略图仍可能继续下载

位置：`TransferUi.kt:36,45`。

已有 8 MiB LRU、3 个并发槽、2 MiB 响应读取上限和采样解码，基础限制合理。但 `produceState` 取消没有显式连接到同步 `execute()` 的 `Call.cancel()`；离屏后阻塞网络和解码可能继续占用槽位，直到完成或超时。

缓存只在等待槽位前查一次。多个相同 URL 同时未命中时，排队后也不重新查缓存，可能重复请求与解码。

建议取消时取消 Call，并正确传播取消异常；取得槽位后二次查缓存；按 URL 合并在途请求，明确多个消费者取消时的所有权。失败缓存只能短期且区分认证错误 / 网络故障，不可永久记住断网结果。

图片显示为 56 dp，解码目标可按密度匹配；但电脑侧缩略图服务已经限制到约 256 像素，不应把当前链路描述成“列表总在下载原始大图”。优先修取消与重复请求，再实测调整尺寸。增加内存压力回调释放缓存也有价值。

## 5. 页面、状态与数据存储

### A11：连接实例绑定 Activity，页面重建会重连

位置：`MainActivity.kt:61,71`；`LanHub.kt`；`net/LanClient.kt`。

`onCreate()` 先断开全局旧客户端，再新建客户端；回调引用 Activity 及其页面状态。Activity 重建会改变连接、触发新快照和保活服务启停；全局客户端也延长这些引用的存活。这里可以确认所有权耦合，不能仅凭它断言已经发生不可回收内存泄漏。

建议连接、发送和任务状态归进程级控制器 / 服务，以 applicationContext 为依赖；页面按生命周期订阅 StateFlow。配置变化不重建 WebSocket，显式切换目标才重新连接。保留进程级发送作用域，禁止改为页面销毁就取消后台自动发送的 lifecycleScope。

### A12：状态订阅太宽，素材增多后排序成本增长

位置：`MainActivity.kt:254,259,281,835`；`data/MaterialRepository.kt:40,142`。

MainScreen 根层订阅素材、同步状态、诊断和诊断历史，当前在其他面板时也可能受到更新影响。接收页过滤计算位于根层。现有 `collectAsStateWithLifecycle()` 已限制生命周期，不能说 App 退到后台仍在无限刷新界面。

建议把接收数据订阅移到接收页，诊断订阅移到连接页，历史仅在日志展开时订阅；顶部任务数等全局信息保持轻量独立流。过滤可用序列先筛选后取前 50 项，避免不必要中间列表。[Compose 性能建议](https://developer.android.com/develop/ui/compose/performance/bestpractices)

仓库每次增量执行全量排序，且活跃素材 map 没有数量上限。初始快照和 UI 的“50 项”不等于仓库始终最多 50 项，长会话持续收素材后成本会增长。

可考虑维护有序索引、合并短时间内的数据发布或设置明确展示窗口。**不能简单截断仓库并忽略序号**：删除、清空、同秒排序、撤回及快照缺口判断仍必须完整处理。小规模 50 项列表的排序不是首要功耗问题。

现有 LazyColumn 稳定 key / contentType、缩略图淡入及面板短时动画应保留。未找到 `rememberInfiniteTransition` 常驻动画；加载指示器只在相应操作界面显示。先减少状态更新和离屏工作，再用帧时间决定是否调整动画，避免牺牲交互品质却没有显著待机收益。

### A13：DataStore 需要真正的进程单例

位置：`core/SettingsStore.kt:12,14`；`MainActivity.kt:61,919`。

`preferencesDataStore` 委托定义在 SettingsStore 实例内部，上传重试又构造新 SettingsStore。这会产生多个委托管理同一 settings 文件的路径。官方要求同一进程同一文件只使用一个 DataStore，多个活跃实例可能在读写时抛异常。[DataStore 文档](https://developer.android.com/topic/libraries/architecture/datastore)

建议将委托移到顶层或单例持有者，使用 applicationContext；SettingsStore 包装器可以复用同一 DataStore，重试不要再创建文件级存储实例。该问题首先影响重试可靠性，省电收益来自减少重复失败与工作，不能直接量化。

### A14：后续小优化

- `ClipboardMonitorCoordinator.setDiagnostic()` 每次生成新的最多 12 条历史列表；HIGH / 兜底 / 回执路径仍有多次诊断更新。保持关键错误、发送回执，DEBUG 细节可采样或按需开启，不记录完整剪贴板与认证内容。
- Distribution 非 debuggable 不等于自动去掉 `Log.d`；当前没有证据表明这些日志被统一裁剪。高频字符串、编号格式化可以在日志关闭时跳过。
- 内容指纹和认证摘要的逐字节字符串格式化、重复连接身份摘要可在明确输入不变时缓存；收益低于锁与 I/O 优化。
- 手动文本发送的 20 秒确认超时任务，在提前收到回执后仍可能等到截止时间再检查；可按 requestId 取消已完成计时任务。
- 隐私持久化与修订校验具有正确性用途，不要为了少一次同步操作随意把提交语义改掉；先测锁等待，再优化锁范围。
- UDP 搜索只发送一次广播，最多等待约 3 秒，并在 150 毫秒接收超时之间检查取消。它不是常驻扫描。后续可用取消直接关闭 socket 减少退出延迟，但待机收益较小。

## 6. 全模块覆盖清单

下表路径均相对 `AndroidApp/app/src/main/java/com/liuzhuan/app/`。分组覆盖全部 31 个 Kotlin 文件；没有列出的“新增问题”不表示完成了设备侧性能验收。

| 模块 / 文件 | 重点与结论 |
| --- | --- |
| `ForegroundService.kt` | 锁获取、释放、长期持有、FGS 时限：A01 / A02 / A05 |
| `net/LanClient.kt`, `net/LanDiscovery.kt`, `net/Proto.kt`, `LanHub.kt` | 重连、心跳、HTTP、搜索取消、摘要、所有权：A03 / A09 / A11 / A14 |
| `xposed/ClipHook.kt`, `ClipReceiver.kt` | 原进程读取、广播、主线程解码、文件去重：A04 / A06 |
| `clipboard/ClipMonitorService.kt`, `clipboard/ClipboardCaptureManager.kt`, `clipboard/CopyEventDetector.kt`, `clipboard/AccessibilitySourcePolicy.kt` | 无障碍生命周期、评分、事件工作量、节点遍历：A07；保持检测基线 |
| `clipboard/ClipboardEvent.kt`, `clipboard/ClipboardEventDispatcher.kt`, `clipboard/ClipboardDeduplicator.kt` | 元数据与 UUID、指纹、去重和回环；轻量化不得改变事件身份 |
| `clipboard/ClipboardMonitorCoordinator.kt`, `clipboard/AutomaticClipboardDelivery.kt`, `clipboard/ClipboardEventQueue.kt` | 幂等装配、诊断、有限队列与有限重试；消费循环非忙轮询 |
| `clipboard/action/ClipboardAction.kt`, `clipboard/action/ClipboardActionPipeline.kt`, `clipboard/action/PushToPcAction.kt` | 进程级作用域、事件并发、自动发送边界；保留可靠传输链 |
| `clipboard/ClipboardPrivacy.kt`, `clipboard/ClipboardPrivacyPolicy.kt`, `ClipboardPrivacyControls.kt` | 规则修订、事件拦截、暂停截止计时；事件驱动，无新增轮询建议 |
| `MainActivity.kt`, `ProcessTextActivity.kt`, `FlowDesign.kt` | 网络与 UI 耦合、页面订阅、手动入口回写、防环与有限动画：A11 / A12 |
| `TransferTasks.kt`, `TransferUi.kt` | 进度分配、重试数据、缩略图取消与缓存：A06 / A08 / A10 |
| `data/MaterialRepository.kt`, `data/MaterialItem.kt` | 全量排序、长会话增长、序号 / 同秒稳定顺序：A12 |
| `core/SettingsStore.kt` | 文件级 DataStore 实例管理：A13 |

另检查 `AndroidManifest.xml`、`app/build.gradle.kts` 与 `res/xml/accessibility_service_config.xml`：target 36、服务类型与事件订阅决定了上述平台限制和事件入口。签名及 Distribution 配置本次不变。

## 7. 建议实施顺序

### 第一阶段：保守修复，维持现有后台策略

1. 保活锁幂等，明确异常路径释放；修复重连退避及长期计数。
2. Hook 读取硬上限，文件接收有限异步，修 DataStore 单例与临时文件所有权。
3. 进度先节流再分配，缩略图取消与同 URL 请求合并，复用 HTTP client。
4. 缩小诊断和素材订阅范围，高频无障碍轻量分类 / 元数据。

这阶段主要减少无效工作，不主动改变秒达保证。但所有剪贴板改动仍需验证 Hook → Receiver → Dispatcher → 自动发送 → 电脑登记回执，不能只以编译通过宣布完成。

### 第二阶段：连接与传输所有权整理

进程级连接控制器、可取消且有界的传输管理、合理的素材窗口 / 索引。先确保旋转 / 重建不重连、重试不丢 URI 权限、任务不因页面退出而停止。

### 第三阶段：功耗策略与平台兼容

补前台服务超时处理，核对服务类型；在目标 ROM 上对照测试锁策略与心跳。确认可靠性后再提供“即时 / 均衡”设置，并清楚说明后台到达延迟差异。长期离线可优先节省无效连接与 CPU 保活，而正常在线即时模式保留已验证的交付体验。

## 8. 实机测量与验收方案

### 采集条件

本轮 ADB 无设备，以下是下一轮方案，不是已经执行的测试。基线与优化版使用同一手机、ROM、签名、LSPosed 作用域、AP、屏幕亮度和后台限制，记录电池温度与测试时长。分开比较前台、后台和锁屏。

USB 充电 / 保持唤醒、无线调试本身都会干扰功耗。长待机对比应避免充电并统一调试状态，必要时本机录制后导出；不要把调试连接不断开当成真实用户待机结果。Hook 工作发生在来源应用进程，还要观察来源 App 的 CPU 与 GC。

### 只读采集示例

下面命令只用于读取当前状态，结果可能包含设备与网络信息，应仅保存在本地并脱敏后分享。包名需要与所测 APK 核对。不应直接清零全局电量统计、强制 Doze 或改变用户电源设置。

```powershell
& 'F:/Android-SDK/platform-tools/adb.exe' shell dumpsys batterystats com.liuzhuan.app
& 'F:/Android-SDK/platform-tools/adb.exe' shell dumpsys power
& 'F:/Android-SDK/platform-tools/adb.exe' shell dumpsys wifi
& 'F:/Android-SDK/platform-tools/adb.exe' shell dumpsys meminfo com.liuzhuan.app
& 'F:/Android-SDK/platform-tools/adb.exe' shell dumpsys gfxinfo com.liuzhuan.app
```

采集前后差值、进程重启、统计窗口、任务量和系统电源状态，避免直接比较不同累计窗口的数字。用 Perfetto / System Tracing 检查调度、唤醒和帧时间；设备支持时使用 Power Profiler / 电源轨数据。Battery Historian 已不再积极维护，不作为必须新安装的工具。[Android 功耗工具说明](https://developer.android.com/topic/performance/power/setup-battery-historian) / [dumpsys 文档](https://developer.android.com/tools/dumpsys)

### 场景矩阵

| 场景 | 要观察的指标 | 正确性验收 |
| --- | --- | --- |
| 连接电脑、前台静置 | CPU、网络包、界面更新次数 | 不出现无任务的重复请求 |
| 后台锁屏至少 1–2 小时 | WakeLock 持有、深睡机会、UID 耗电、断连次数 | 外部 App 复制仍可到达，记录端到端 P50 / P95 |
| 电脑关机 / 目标不可达 | 每分钟连接尝试、CPU、诊断更新 | 退避正常增长；电脑恢复后自动连接 |
| 手机断 Wi-Fi 再恢复 | 无路由期间尝试数、恢复时间 | 不依赖互联网验证、不积压无限任务 |
| 前后台分别连续复制至少 20 条文字 | Hook 与无障碍事件数、重复率、丢失率 | 顺序、去重、回执及隐私规则均正确 |
| 复制小图及明显超过 512 KiB 的图 / 文件 | 来源 App 主线程耗时、峰值内存、GC | 超限不完整读取；原 App 复制照常完成 |
| 大文件与多任务上传 / 下载 | 吞吐、CPU、进度分配、并发与内存 | 重试 / 取消 / 半成品清理与回执正确 |
| 快速滚动图片列表并连续切页 | 在途请求、缓存命中、帧时间 | 离屏请求及时取消，不串图，不重复下载 |
| Activity 重建、多次重连、主动暂停 | 连接与锁数量、旧回调 | 无旧连接扰动；暂停后释放所持资源 |
| 长时间后台前台服务 | 系统超时回调、停止后的状态 | 无异常退出 / 重启循环，恢复入口明确 |

每个场景至少重复基线 / 优化版对照，报告中位数、尾部延迟和失败样本。先通过交付正确性，再讨论省电；没有证据前不承诺“降低多少百分比”。

## 9. 文档校正提醒

当前部分注释和现行文档需要在后续实施时一并校正：

- 架构文档的 Wi-Fi 模式描述与代码不同，且旧 HIGH_PERF 功耗判断不适用于 API 36。
- 架构文档描述应用层 30 秒心跳，但生产代码只找到 OkHttp ping。
- 节点遍历“64 个节点”与实际最多 273 不符。
- ActionPipeline 的“串行”应限定为单事件内部，不代表事件之间串行。

本次保留原文，单独记录差异，避免把未经设备测量的策略写成已经完成的优化。
