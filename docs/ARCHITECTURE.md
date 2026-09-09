# ARCHITECTURE — 当前真实架构

> 基线：M13（HEAD `ebc3da4`，2026-08-27），本文以当前源码为准逐文件核实。
> 维护约定：改动下述任一链路的结构（新增/删除组件、改协议、改端口）时**必须同步更新本文**。
> 冲突裁决顺序：源码 > 本文 > `logs/项目迁移交接文档_20260827.md` > 其他历史资料。

---

## 1. 总览

```
┌─────────────────┐   WS 8899 控制面(握手/心跳/文字/列表/广播)    ┌───────────────┐
│  Android 端 N 台  │◄──────────────────────────────────────────►│   PC 端流转     │
└─────────────────┘   HTTP 8900 数据面(/file /upload,带鉴权)      │ (WS+HTTP+UDP) │
                  UDP 8901 发现(DISCOVER/OFFER 广播应答)          └───────────────┘
```

- **PC = Hub 服务器**：WPF (.NET 7)，Fleck WebSocket + 手写 TcpListener HTTP。
- **Android = 客户端**：Kotlin 2.0.21 + Compose（BOM 2024.12.01）+ OkHttp 4.12 + DataStore；`minSdk=compileSdk=targetSdk=36`，只支持 Android 16+。namespace `com.liuzhuan.app`。
- 端口实现有个间接层：`LanServer.Start()` 把 `LanConfig.Port`(8899) 传给三个子服务，HTTP 在 `Start(port+1)`，UDP 在 `UdpDiscovery.Start(wsPort)` 内部再 `+2` → **实际 8899/8900/8901**。Android 的 `LanDiscovery.DISCOVER_PORT=8901` 是硬编码（见 KNOWN_ISSUES 耦合项）。
- 认证：口令 6 位数字，WS 握手 data 带 `auth=sha256(口令)` + `ts`(Unix 秒)，服务器校验 ts ±300s 与哈希匹配；HTTP 文件上传/下载同规则。传输为局域网明文（Manifest `usesCleartextTraffic="true"`）。
- 数据目录：PC 运行数据在 `App.DataDir = <exe>/../data`，即工作区根 `data/`（data.json、lan.json、appsettings.json、uploads/、thumbnails/）；`app/logs/run.log` 是运行日志。Android 配置在 DataStore "settings"。

## 2. Android 端模块

### 2.1 发送链（手机复制 → PC，核心链路）

```
系统无障碍事件
→ ClipMonitorService.onAccessibilityEvent        # 薄触发器：打点日志后统一转交
→ ClipboardCaptureManagerImpl.onAccessibilityEvent
   ├ AccessibilitySourcePolicy.classify/shouldInspect   # SELF/UNKNOWN → 直接忽略（回环防护第一道）
   └ CopyEventDetector.evaluate(event, category) → Level
       NONE/LOW  → 忽略
       MEDIUM    → 协程 launch { tryCaptureSelection }         # 仅轻量 selection 捕获，不读剪贴板
       HIGH      → 协程 launch { tryCaptureAfterCopy }          # selection 优先 + 剪贴板兜底，
                   │                                          # 短延迟重试 80ms→80ms→100ms
       捕获成功 → ClipboardEventDispatcher.dispatch
                    normalize(trim) → validate(len≥2)
                    → LanHub.autoSendClipboard 开关过滤
                    → ClipboardDeduplicator.shouldProcess       # LRU 64 条指纹 / 2s 时间窗
                    → ClipboardActionPipeline.process           # 进程级 IO scope 异步串行执行 Action
                    → PushToPcAction.execute
                         Connected  → 先 drain 队列补发积压，再 pushClipboard 当前事件
                         未连接     → ClipboardEventQueue.enqueue   # FIFO max 50
```

| 组件 | 职责要点 | 关键方法 |
|---|---|---|
| `ClipMonitorService` | 无障碍服务薄触发器；`isRunning` 三态检测信号之一；诊断通知 | `onAccessibilityEvent` |
| `AccessibilitySourcePolicy` | 来源五分类 SELF/SYSTEM_UI/LAUNCHER/THIRD_PARTY/UNKNOWN | `classify`、`shouldInspect` |
| `CopyEventDetector` | 强弱信号评分；HIGH=第三方 selection 变化/命中「复制」按钮文案点击；MEDIUM=第三方窗口内容变化；LOW 不单独触发 capture | `evaluate` |
| `ClipboardCaptureManagerImpl` | 捕获编排；selection 走 `AccessibilityNodeInfo.textSelectionStart/End`（限深 2 层 ×16 子节点）；剪贴板走 `getPrimaryClip` best-effort | `onAccessibilityEvent`、`tryCaptureAfterCopy`、`tryReadClipboard`、`captureOnForeground` |
| `ClipboardEventDispatcher` | object 单例，捕获事件的唯一出口 | `dispatch` |
| `ClipboardDeduplicator` | 指纹去重 + 本机写入标记（回环防护） | `shouldProcess`、`markLocal` |
| `ClipboardEventQueue` | 断线缓存 FIFO max 50，@Synchronized | `enqueue`、`drain` |
| `PushToPcAction` | pipeline 中唯一动作；发送成功失败均写诊断 StateFlow | `execute` |

统一事件模型 `ClipboardEvent`：text/timestamp/sourcePackage/source/mimeTypes/fingerprint(SHA-256)/type/diagnosticId(`COPY-xxxxxx` 贯穿全链日志)。

### 2.2 入口与装配（谁在何时接线）

- **`ClipboardMonitorCoordinator`**：object 进程级单例，唯一装配点。持有 deduplicator/queue/pipeline/captureManager + appInForeground(ActivityLifecycleCallbacks) + diagnostic/diagnosticHistory(StateFlow)。`init(context)` 幂等双检锁，被两处调用：
  - `ClipMonitorService.onCreate`（无障碍已启用时）
  - `MainActivity.onCreate`（无障碍未启用时前台测试可用）
- **`MainActivity.onCreate`**：创建 `LanClient`（7 个回调全部内联注册）存入 `LanHub.client`；注册 `MaterialRepository.onSequenceGap → client.requestSnapshot`。onState 回调里管理前台服务启停（Connected→startForegroundService+`Coordinator.onWsConnected()` flush 队列；AuthFailed/Paused→stopService；**Disconnected 不停**）。Activity 重建会先 disconnect 旧 client 再新建（launchMode=singleTask 缓解频率）。
- **ON_RESUME 兜底**：接收页 Compose `DisposableEffect` 生命周期观察里刷新三态无障碍状态 + `captureManager.captureOnForeground()` 直接读剪贴板走同一 Dispatcher。
- **直连路径（不经 Dispatcher/Pipeline）**：
  - `ProcessTextActivity`（ACTION_PROCESS_TEXT 选中文本直达）：pushClipboard 直发 + 写剪贴板 + markLocalText；
  - 分享菜单（SEND intent）：文字走 `LanClient.sendText`，文件走 OkHttp POST `http://ip:8900/upload?name=&auth=`（端口硬编码 8900）。

### 2.3 连接层 `LanClient`

- 状态机：`Idle → Connecting → Connected / AuthFailed`；意外断开（onClosed/onFailure 且非 manuallyClosed）设 `Disconnected` 并指数退避重连 1s→30s 上限；主动 `disconnect()` 设 `Paused` 不重连（与 Disconnected 的语义区分是 M11 保活修复的关键）。
- 回调七参数：onState/onLog/onListData/onItemAdded/onItemDeleted/onItemCleared/onItemData，全部由 MainActivity 内联提供。
- 防护细节：`webSocket !== ws` 早退防旧连接回调触发重连死循环；connect/disconnect/doConnect 前都关闭旧 WS；心跳双层维持（OkHttp pingInterval 30s + 应用层 heartbeat 每 30s）。
- 构造参数默认 `CoroutineScope(SupervisorJob() + Dispatchers.IO)`——进程级。

### 2.4 接收页数据层

- `MaterialRepository`：object 单例，StateFlow<List<MaterialItem>> + SyncState(IDLE/SYNCING/SYNCED/RESYNCING/ERROR)。内部 LinkedHashMap 以 id 为键。
- 同步语义：`replaceAll`(快照)、`add`/`delete`/`clear`(增量)。**gap 检测只在 add 做**：收到 sequence > lastSequence+1 → RESYNCING + onSequenceGap 回调请求新快照。排序按 sequence 降序 fallback time。
- UI：MainScreen `collectAsStateWithLifecycle` 主线程 collect；列表仅展示 take(50)。
- 快照序列号语义注意：PC `list_data` 里每条 item 的 sequence 都是**全局计数器同一时刻的值**（见 §3.3），逐条单调递增只对 item_added 增量成立。

### 2.5 保活与配置

- `ForegroundService`：常驻通知 + START_STICKY + PARTIAL_WAKE_LOCK(无超时 acquire) + WIFI_MODE_FULL_LOW_LATENCY WifiLock；manifest 声明 `foregroundServiceType="dataSync"`。职责只有保活，不碰剪贴板不碰连接逻辑。
- `SettingsStore`（DataStore "settings"）：serverIp/serverPort(默认"8899")/password/deviceName/autoSendClipboard。autoSend 开关运行时经 `LanHub.autoSendClipboard`(@Volatile) 直达 Dispatcher 过滤，DataStore 持久化由 UI 双写。

## 3. PC 端模块

| 组件 | 职责要点 |
|---|---|
| `MainWindow.xaml.cs` | 组合根：创建 DataStore/LanServer/ClipboardMonitorService；向 LanServer 注入 `ListProvider=()=>_dataStore.GetRecentSummaries(50)` 和 `ItemLookup`；订阅 DataStore 三事件做广播；`OnLanTextReceived`→`_dataStore.Add`(由此自动广播 item_added 给包括来源在内的所有设备)；PC 剪贴板监控开关(AppSettings.ClipboardMonitorEnabled)开启时把 PC 复制内容收入素材库 |
| `Services/DataStore.cs` | ObservableCollection 素材库 + JSON 持久化（500ms 防抖、tmp 文件替换、锁重试 3 次）；变更序号 `_sequence`(Interlocked 递增，**内存计数重启归零**)；事件 ItemAdded/ItemRemoved/ItemCleared；删除撤回 undo 栈 50 步；加载时校验源文件存在性 |
| `Services/Lan/WsHub.cs` | Fleck 会话管理 ConcurrentDictionary；消息 switch(hello/heartbeat/sync_text+clipboard_push/list_sync/get_item/bye)；welcome 前 AuthService 校验；**同名设备重连自动踢旧 session**；Broadcast* 三方法全局广播 |
| `Services/Lan/LanServer.cs` | 组合根子服务(WsHub/FileHttpServer/UdpDiscovery)，`IsListening` 用 TCP 自连 127.0.0.1:Port 自检（Fleck 静默失败检测） |
| `Services/Lan/AuthService.cs` | sha256 口令哈希比对 + Unix 秒 ts ±300s 防重放；对照目标为 `LanConfig.PasswordHash` |
| `Services/Lan/FileHttpServer.cs` | GET `/file/{id}?auth=`、POST `/upload?name=&auth=`；上传存 `<data>/uploads/<guid>_<name>` 后触发 FileUploaded；两端鉴权不通过即拒绝 |
| `Services/Lan/UdpDiscovery.cs` | 绑定 Port+2(8901)；收到 `LIUZHUAN_DISCOVER` 应答 `LIUZHUAN_OFFER|ip|wsPort|设备名` |
| `Services/Lan/LanConfig.cs` | lan.json 持久化(键 Enabled/Port/Password)；`GeneratePassword()` 6 位纯数字；**Enabled 默认 false**（新环境首次部署需在 lan.json 置 true 或经 UI 开启） |
| `Models/MaterialItem.cs` | 素材模型（INotifyPropertyChanged，含缩略图懒加载） |

## 4. 协议参考

### 手机上传 HEIC 的电脑端兼容转换（2026-09-10）

- `MainWindow.OnLanFileUploaded` 在登记素材之前调用 `HeicImageConverter`，使用独立 STA 线程解码/编码，界面线程只负责登记最终文件。HTTP 回执在转换和登记后返回，协议不变。
- 设置菜单「接收 HEIC 自动转换」提供 Off / Png / Jpeg，持久化到 `appsettings.json` 的 `HeicConversion`；旧配置默认 Png。仅影响后续上传。
- 按 ISO BMFF `ftyp` 中 HEVC 图像品牌识别，不能信任扩展名（手机可能提供 HEIC 内容和 `.png` 文件名）。PNG 保留分辨率，JPEG 质量 95、透明区域白底；多帧只转换主图。
- 输出独立文件，原始上传文件始终保留；素材路径、显示名、大小及广播均使用转换后的文件。失败时保留并登记原文件、记录错误并异步提示用户，不阻止上传回执。
- 使用 Windows WIC 解码器，目标电脑需具备 HEIF/HEVC 解码支持；本机已用两张问题原图验证。输出是兼容静态图片，不承诺保留原 HEIC 的动态内容、HDR 或全部元数据。
- 回归命令：`F:/dotnet7/dotnet.exe run --project scripts/HeicConversionChecks -- <HEIC样本路径> ...`，验证设置持久化、格式识别、PNG/JPEG 全分辨率解码、关闭/普通图片透传及损坏文件保留。

帧格式 `{v, type, id, ts, device, data}`（`LanMessage.cs` ↔ `Proto.kt` 对应，字段两端成对修改）。

| type | 方向 | data 关键字段 |
|---|---|---|
| hello | A→P | auth(sha256), ts |
| welcome | P→A | serverVersion, features, sessionId |
| auth_fail | P→A | reason |
| heartbeat | A↔P | t |
| sync_text | A→P | content, source="android" |
| clipboard_push | A→P | content, app |
| list_sync | A→P | （空）→ 触发 list_data 快照 |
| list_data | P→A | items[{id,type,name,size,time,sequence}], sequence(快照序号) |
| item_added | P→A | id, type, name, size, time, sequence |
| item_deleted | P→A | id, sequence |
| item_cleared | P→A | sequence |
| get_item | A→P | id |
| item_data | P→A | id, type, name, content 或 downloadUrl(+size), error |
| ack | P→A | status="ok"（sync_text/clipboard_push 处理前先回执；**Android 目前未校验 ack**） |
| bye | A→P | — |

## 5. 并发模型（全线进程级）

- 三个协程作用域均为 `CoroutineScope(SupervisorJob() + Dispatchers.x)` 默认参数：CaptureManager(Default)、ActionPipeline(IO)、LanClient(IO)。**不存在任何绑定 Activity/ViewModel 的 scope**，后台功能依赖这一点。
- OkHttp WS 回调在 OkHttp 线程池执行 → 通过 LanHub(@Volatile)、MaterialRepository/@Synchronized 方法、StateFlow 收敛到主线程。
- 互斥点集中：Repository/Deduplicator/Queue 各自 @Synchronized。
- 队列补发有两条并发路径会调用 `drain()`：Coordinator.onWsConnected（MainActivity onState 回调线程）与 PushToPcAction.execute 连接态前置 drain（pipeline IO 协程）。drain 原子所以不会重复发送同一事件，但两条路径的补发相对顺序可能交错（现状无害，重构注意）。

## 6. 测试资产

- Python E2E：`scripts/test_m1_sync.py`、`test_m1_lan.py`、`test_m1_download.py`、`test_m4_multi.py`（多设备并发）、`demo_shot.py`/`gen_manual.py`。协议联调可不开真机直跑。
- 两端工程内暂无单元测试（技术债，见 KNOWN_ISSUES）。
