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
  - 分享菜单（SEND intent）：文字走 `LanClient.sendText`，文件走 OkHttp POST `/upload?name=&auth=`（HTTP 端口为所选电脑 WS 端口 + 1）。

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
| `Services/Lan/FileHttpServer.cs` | GET `/file/{id}?auth=`、GET `/thumbnail/{id}?auth=`、POST `/upload?name=&auth=`；上传先写 `.part`，收齐声明字节数后转为正式文件并触发 FileUploaded；三种端点使用同一口令鉴权 |
| `Services/Lan/UdpDiscovery.cs` | 绑定 Port+2(8901)；收到 `LIUZHUAN_DISCOVER` 应答 `LIUZHUAN_OFFER|ip|wsPort|设备名` |
| `Services/Lan/LanConfig.cs` | lan.json 持久化(键 Enabled/Port/Password)；`GeneratePassword()` 6 位纯数字；**Enabled 默认 false**（新环境首次部署需在 lan.json 置 true 或经 UI 开启） |
| `Models/MaterialItem.cs` | 素材模型（INotifyPropertyChanged，含缩略图懒加载） |

## 4. 协议参考

### 界面动效与视觉反馈（M28，2026-09-12）

- PC `Utils/UiMotion` 统一点击反馈（110ms）、内容过渡（160ms）与面板展开（240ms）；使用 RenderTransform/Opacity，按 Windows `ClientAreaAnimation` 开关立即落到最终状态。没有持续装饰动画。
- 面板复用 `_panelTranslate`，反向操作从当前画面位置接续；`_panelTransition` 阻止旧完成回调覆盖新目标。收起完成隐藏 MainPanel 并将 PanelColumn 设为 0，让 8px 触发条实际位于窄窗内；展开时先恢复布局。固定、菜单打开、左锚定保留原有保护。
- 按钮与卡片只在内部 MotionSurface 做按压缩放、悬停提亮，不变更外部布局或接管点击。拖出前清理悬停预览和按压状态；分类滑动指示条、内容轻入场、搜索焦点边框与反馈提示沿用同一配色和节奏。
- GridView 的 MaterialTemplateSelector 仅初始化一次；缩略图直接绑定 ThumbnailSource，异步完成仅更新图片并淡入，不重建整页。Duration 增加属性变更通知，保留异步时长更新；缩略图和列表更新保留容器与选择。
- Android `FlowDesign.kt` 集中定义线性导航图标、180–220ms 折叠/箭头及平滑进度（M29 已移除整页位移与透明度动画）。仅组合当前页，不叠加两份生命周期与业务回调；草稿和各页滚动位置保留，切页收起键盘。
- Android 图片淡入、任务列表重排采用 Compose 标准动画，跟随系统 MotionDurationScale；结束/失败进度立即显示真实值。接收行按素材 ID、缩略图按 URL 保持组件身份，避免列表增量插入时短暂错图。
- DesktopUiChecks 采用无业务启动的 Application 和真实资源字典、MainWindow，推动 Dispatcher 检查连续反向/快速三连、关闭动画同步落地、窄条布局与绑定更新；离屏截图不能证明真实帧率、多屏 DPI 或手机触控效果，仍需设备验收。

### 收发任务与快捷预览（M27，2026-09-12）

- PC 底部「收发」打开 `Views/TransferWindow`，由进程内 `TransferJournal` 汇总文件上传/下载与接收文字。Android 顶部「收发」打开 `TransferTaskDialog`，`TransferTasks` 跟踪文件和 `sendText` 手动/分享文字。两端最多保留 40 条最近记录，超额时仅淘汰已结束任务；重启清空。
- Android 失败任务支持手动重试，重新完整传输，不做断点续传。上传确认丢失时不能判断电脑是否已登记，因此提示先检查电脑，避免重复。分享 URI 权限过期需要重新选择文件；下载重试沿用该次地址，换电脑或改口令后请重新获取素材。
- `sync_text` 的请求 `id` 对应任务 ID。PC 在 `TextReceived` 同步登记返回后回 `ack`，`data={status:"stored",requestId:<请求 id>}`；Android 收到该确认才显示「电脑已接收」，20 秒未确认提示检查后重试。确认表示已登记素材，不是 JSON 延迟持久化完成。自动 `pushClipboard` 送达保证仍见 KNOWN_ISSUES DEF-2。
- HTTP 上传中断/长度不足时删除 `.part` 并返回 400；成功文件与素材登记完成后才回 200。PC 下载任务「已发送」只表示服务器已写出声明字节，不能证明手机保存成功。
- Android 下载以 64KB 缓冲流式写入 MediaStore，校验响应长度，成功关闭文件并清除 `IS_PENDING` 才显示「已保存到手机」；失败删除不完整文件。上传也使用 64KB 缓冲，任务进度约 200ms 更新一次。
- 接收页 Image 行请求鉴权 `/thumbnail/{id}`：PC `NetworkThumbnailService` 使用独立 STA 线程编码 256px 宽 PNG，并发上限 2；Android 并发 3、8MB 内存 LRU，限制单响应 2MB/解码边长 512px。离线、旧电脑端或不支持的图片显示占位。原文件下载地址保持原义。
- PC 选中图片/文字后按空格打开 `QuickPreviewWindow`，支持上一项/下一项、图片缩放与适应窗口、复制和 Esc 关闭。图片预览最多解码至 1600px 宽，复制仍使用原素材；不修改原文件。
- 新能力需两端一起升级。`scripts/TransferChecks` 覆盖真实 HTTP/WS 的上传完整性、鉴权缩略图、文件一致性及文字回执顺序，`TransferTasksCheck.kt` 覆盖失败重试与任务保留。Android 相册落盘及实际界面仍需真机验收。

### 电脑端素材栏交互（2026-09-12）

- 两端视觉统一为深色青绿、薄荷绿操作色和暖金辅助标签。电脑端增加流转标记、素材类型及大小/日期信息；Android `FlowDesign.kt` 集中管理主题与页面介绍卡片。
- Android 连接页优先展示搜索/扫码、上次电脑快捷连接，手动输入和日志可折叠；暂停重连始终可访问。连接时保留用户的自动发送开关。
- Android 发送页区分空输入、提交发送与上传进度；`LanClient.sendText` 返回 WebSocket 是否接受发送（不等同对端确认），对端确认结果在 M27 收发任务中显示，保持进程级协程作用域。
- Android 接收页在 Repository 数据上派生名称搜索/类型筛选，不另建数据源；离线时显示缓存提示并禁用获取操作。三个页面分别保留滚动位置，文字草稿与筛选条件使用 `rememberSaveable`。

- 380 DIP 宽侧栏：标题/固定/设置，独立搜索，添加文件/粘贴与连接数，七类筛选，素材区，反馈/计数/撤回/更多。图片保持双列，文字分类使用整行阅读。
- `RefreshView` 原位增删/移动集合，避免异步缩略图完成时清空列表、丢失选择；空素材、无收藏与搜索无结果分别给出提示。
- 单击复制，Ctrl/Shift 多选不复制；Ctrl+A 全选，Ctrl+C 复制同类多选文件或文字；Esc 依次清搜索、清选择、收起。清空保留原确认逻辑，入口移到「更多」。
- 拖动窗口限定为标题区域，只有窗口拖动结束才触发贴边吸附；固定展开为本次运行状态。菜单、拖出和正在编辑时不自动收起；图片悬停 400ms 后预览，移开取消。
- 新「添加文件」复用 `DragDropService.CreateFromPathRecursive` 与原有素材登记/广播流程。
- `scripts/DesktopUiChecks` 使用独立测试数据目录进行 WPF 离屏渲染及交互回归：两列布局、刷新保留选中、固定防收起、搜索与收藏空状态、文字视图。真实窗口工具在当前 Windows 上无法截图（`SetIsBorderRequired` / `0x80004002`），视觉检查使用实际 XAML 离屏渲染，非真实桌面截图。

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
| ack | P→A | status="stored", requestId=请求帧 id（sync_text/clipboard_push 登记后回执；Android sendText 任务关联确认，自动 pushClipboard 尚未利用） |
| bye | A→P | — |

## 5. 并发模型（全线进程级）

- 三个协程作用域均为 `CoroutineScope(SupervisorJob() + Dispatchers.x)` 默认参数：CaptureManager(Default)、ActionPipeline(IO)、LanClient(IO)。**不存在任何绑定 Activity/ViewModel 的 scope**，后台功能依赖这一点。
- OkHttp WS 回调在 OkHttp 线程池执行 → 通过 LanHub(@Volatile)、MaterialRepository/@Synchronized 方法、StateFlow 收敛到主线程。
- 互斥点集中：Repository/Deduplicator/Queue 各自 @Synchronized。
- 队列补发有两条并发路径会调用 `drain()`：Coordinator.onWsConnected（MainActivity onState 回调线程）与 PushToPcAction.execute 连接态前置 drain（pipeline IO 协程）。drain 原子所以不会重复发送同一事件，但两条路径的补发相对顺序可能交错（现状无害，重构注意）。

## 6. 测试资产

- Python E2E：`scripts/test_m1_sync.py`、`test_m1_lan.py`、`test_m1_download.py`、`test_m4_multi.py`（多设备并发）、`demo_shot.py`/`gen_manual.py`。协议联调可不开真机直跑。
- 独立检查工程：`scripts/HeicConversionChecks`、`scripts/DesktopUiChecks`、`scripts/TransferChecks`；自动剪贴板核心链路仍缺少单元测试（见 KNOWN_ISSUES）。

### 局域网发现与 Android 页面减负（M29，2026-09-13）

- `LanNetUtil` 排除隧道、虚拟适配器、链路本地地址与点对点地址；候选地址按手机子网、网关及 Wi-Fi 稳定排序。二维码只使用候选地址，并允许用户选择网卡；没有有效 LAN 时不生成误导二维码。多网卡环境仍需选择与手机相通的网络。
- `UdpDiscovery` 根据请求来源选择地址；Android 使用 UDP 回包的实际源地址，兼容旧电脑端 OFFER 中误填虚拟 IP。消息字段未变。
- `LanDiscovery.discover` 为 suspend 函数，socket 创建、发送、接收全部在 IO 调度器；150ms 接收超时提供取消检查机会，use 保证关闭。搜索按钮阻止重复触发并处理异常，取消异常继续传播。
- 主页面使用 LazyColumn，接收素材按稳定 id 分项组合，保持每页滚动状态；移除整页动画。顶部收发计数只订阅去重后的运行任务数，避免进度更新扩大重组范围。
- `scripts/LanAddressChecks` 验证网卡选择与实际二维码解码；同目录 DiscoveryCheck.kt 验证真实 UDP 回包、界面调度器不阻塞、取消释放 socket 和异常传播。无 adb 设备，未完成手机闪退堆栈、扫码互通与帧率验收。
### 电脑设置菜单可读性（2026-09-25）
- App.xaml 统一 ContextMenu / MenuItem / 子菜单 Popup 模板：深色底、13 号字、36 DIP 最小行高，勾选/悬停/展开状态明确；长文字换行，长菜单可滚动。菜单分隔符同时覆盖 MenuItem.SeparatorStyleKey，避免系统主题回退。
- HEIC 模式和保存逻辑保持原状；设置菜单去掉 10 号字号，关于窗口显示程序集版本。已按 150% DPI 离屏渲染检查主菜单及 HEIC 子菜单，不等同于真实桌面截图。

### 迁移与备份（电脑端 v1.3.0 本地开发版）
- `MigrationWindow` 提供导出、导入预览、后台复制进度、取消及退出入口；设置菜单接入。导出在 UI 线程获取素材与设置快照，文件 IO 在后台运行。
- `MigrationService` 定义 v1 `.liuzhuan.zip`（manifest.json + files/<guid>/<name>）。按原路径去重复引用，打包文件字节、文字、收藏、时间和缩略图；不打包程序、日志、旧绝对目录配置或非索引历史文件。逐文件 SHA-256，临时输出成功后才替换目标 ZIP。
- 导入严格校验版本、清单、文件大小、SHA-256 与引用路径，拒绝重复/穿越路径；创建默认 data/imports/<guid> 独立目录，把素材路径写为新机器绝对路径。索引上限 32 MiB、10 万条；文件总量上限 1 TiB，并检查磁盘可用空间。
- 配置通过强类型白名单迁移：ClipboardMonitorEnabled、HeicConversion、LAN Enabled/Port/Password、AutoStart。开机自启在新机器使用当前 exe 路径重新应用，不复制注册表命令。
- `migration-pending.json` 仅记录待切换库的生成 ID；`App.OnStartup` 在构造 MainWindow/DataStore、加载静态配置之前调用 ApplyPending，备份旧 config.json 后原子更新数据目录指针。原库保持不动，避免运行中 DataStore 延迟保存写进新库；生效后移除 pending。
- 如需人工回退：先退出流转，备份当前默认 data/config.json，再将对应 config.before-import-*.json 复制为 config.json；保留所有 imports 目录。配置备份只保存路径指针，原素材文件仍在原位置。
- 检查入口 `scripts/MigrationChecks` 使用独立临时目录，验证旧原路径不可用下的迁移、同名文件/重复引用、收藏文字和配置、生产 DataStore 加载、取消清理、损坏/路径攻击拒绝；不触碰实际用户素材或自启注册表。
