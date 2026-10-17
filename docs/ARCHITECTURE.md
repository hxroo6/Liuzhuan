# ARCHITECTURE — 当前真实架构

> 基线：M13（HEAD `ebc3da4`，2026-08-27），本文以当前源码为准逐文件核实。
> 维护约定：改动下述任一链路的结构（新增/删除组件、改协议、改端口）时**必须同步更新本文**。
> 冲突裁决顺序：源码 > 本文 > `logs/项目迁移交接文档_20260827.md` > 其他历史资料。

---

## 1. 总览

### 手机麦克风实验链（M39–M45 / v1.9.0）

- 独立 TCP 8902 `/audio` WebSocket 承载音频，原素材 WS8899、HTTP8900、UDP8901 保留。M42在素材WS增加可选鉴权后配对协商，不混传原始音频；随机一次性音频邀请两分钟有效且不持久化，一次只接入一台手机。按用户要求暂不加密，仅接受私网 IPv4/本机，不提供公网模式。
- PC `Services/Audio/MicrophoneHost` 持有服务；设置页 `Views/MicrophoneWindow` 只操作/观察。新音频后端用 Kestrel 限制连接/消息并提供可取消发送，未改原 `FileHttpServer`。NAudio 2.2.1 用于显式设备枚举和共享 WASAPI；不引入竞品应用或复制其业务源码。
- Android `audio/MicrophoneService` 是单独的 microphone FGS，需用户前台扫码“连接全部功能”或显式点击连接麦克风、并获系统录音权限才启动采集。原 `ForegroundService(connectedDevice)`、LSPosed 与素材连接生命周期不变。UI 只订阅 `MicrophoneState`；结束本模块不结束素材同步，后台自动重连不会触发采音。
- PCM16LE mono16kHz、20ms帧，44字节网络序头；代次和递增序号限制迟到帧。M41为200ms声音开头缓冲放宽手机发送队列到约300ms阈值，电脑有界200ms、60ms起播缓冲，溢出丢旧、欠载补静音，停止清队列，不写录音文件。
- M41按用户要求，手机网络会话开启后持续待命，取消15分钟/闲置60秒限制；需求租约仍5秒，断线不恢复开麦。系统静音/录音策略阻断/未批准路由会结束会话；非引用计数CPU锁单次30分钟、活跃会话每20分钟续期，清理释放，避免无限锁或重复引用累加。本机试音仍60秒/锁65秒，PC锁定/睡眠关闭音频服务。
- M41保留手动转发，并增加默认关闭的实验自动跟随。独立MTA线程订阅新增录音会话、每200ms核对已跟踪状态；原始GetProcessId必须S_OK，核验官方Codex包身份、进程创建时间与路径，拒绝跨进程成功码/未知归属。先完成空闲→活动→空闲校准，再由用户启用；仅控制已由手机确认的本次连接转发，不是鼠标按钮Hook。
- `FollowDemandPolicy`聚合已验证会话需求，最后空闲400ms后停止；`MicrophoneHost`串行执行手动/自动命令。连接编号与手机停止修订在服务端锁内再次校验；手机暂停、断线、观察失效、手动停止关闭自动跟随，不因重新连接恢复。设备ID偏好独立保存在本机`microphone.json`，不保存许可、邀请、运行/自动状态，不随素材迁移。
- M42常规二维码只保留稳定的旧顶层ip/port/pwd。手机连接页扫码一次，LanClient等待对应目标素材welcome，协商`microphone_pairing_v1`，经同一已认证socket请求`microphone_invite`，以requestId接收`microphone_invite_result`。连接变化立即废止待答复；待启动邀请仍绑定原socket，权限回调再次核验。无第二次扫码、无正常流程的额外麦克风确认页。
- `WsHub.MicrophoneInviteProvider`由MainWindow注入`MicrophoneHost.MaterialInvitationAsync`。仅已认证会话可请求，每会话单在途、两秒限频；回复不广播，断开后的回复丢弃。Host按已保存设备ID开启音频服务，首次仅识别标准CABLE Input/Output，不回退扬声器；缺设备/忙碌/诊断模式返回明确状态，素材连接不受影响。
- 该邀请在服务端带一次性“准备后转发”意图：手机实际ready后请求start_forward，仍需当前代次ACK与合法PCM才打开输出。暂停、结束或提前停止废止意图，不因重新ready再次自动开传。旧专用音频码保留手动语义；已连接素材时手机可从麦克风页直接重取邀请，不用重扫。
- Android `MicrophoneMiniControl`放在顶部左侧，复用`MicrophoneBars`的实际音量历史，独立Compose重组边界、可见生命周期内≤10Hz。显示待命/传音/安静/暂停，点击展开麦克风页；返回时到连接页，收音和传输不随页面关闭。没有以合成动画冒充声音。
- M45素材鉴权成功后由WsHub.ClientAuthenticated通知LanServer与MainWindow，异步回UI展开面板并显示连接成功，不激活窗口抢焦点。ConnectionNoticePolicy限制15秒一次，自动收起至少等待6秒；身份失败/仅TCP建立不触发。VB-CABLE仅提供官网下载和独立许可入口，不捆绑分发。
- M44电脑标题区`MicrophoneEntry`直接进入现有设置单例；只读取实际PCM电平，200ms后失效，连接时可见更新≤10Hz，断开/隐藏清历史，隐藏/最小化停止UI计时。Android独立`MicrophoneOptionsStore`只保存省流量与规范化三档门限；各前台启动入口读取最新持久值，不保存录音许可/运行状态。会话运行中改设置仅用于下次会话，界面区分已保存偏好与当前实际模式。
- M43安卓麦克风与收发面板复用`SecondaryPanel`（Material3 ModalBottomSheet），默认完整展开，状态栏安全间距使把手与首页标题接近同高，使用系统拖动把手和嵌套滚动阈值，下拉/返回只关闭UI，不结束采音或传输。PC新增会话累计接收帧和播放回调实际读取PCM字节统计；补零不计数、重启转发不清会话累计、断线保留最近会话。脱敏诊断显式复制，日志只在首次有效PCM/结束写阶段计数，不记录音频、地址、设备标识或凭据。播放回调读取不等同录音端或ASR成功。
- Android `SilenceGate`按PCM RMS门限检测声音：标准-42dBFS，可选灵敏-48/抗噪-36，40ms开启、6dB迟滞、500ms尾音及有界200ms开头缓存。安静时不发送PCM，状态/租约仍1Hz；安静或无需求后台读取批量100ms。有声恢复原20ms帧；换代/停止清缓存，批量读取跨代数据不发送。旧电脑未声明能力时回退连续发送。
- PC用当前代次`status.quiet`区分安静心跳与失联：安静清输出缓存，恢复必须实际收到合法PCM；声称恢复但无PCM仍2秒超时，心跳缺失仍6秒断开。持续本地采音与限时唤醒锁保留；网络/读取优化不能等同于已测得省电比例。
- M40新增Android `PcmLevelMeter`、独立 `MicrophoneLevels`：从既有PCM读取RMS/峰值，按≤10Hz发布36点音量历史。M41声音门控运行时也计算RMS，波形仍只在可见订阅时发布；不保存或修改音频、不影响1Hz业务状态/通知。60秒本机试音复用同一合法FGS和采集器，但不建网络连接。
- PC `PcmSignalMeter`统计真实收到的PCM电平；`DiagnosticAudioSink`是用户显式选择的只接收模式，测量后即丢弃，不创建WASAPI输出。界面明确此模式不能供Codex使用；正常模式继续选定虚拟端点输出。
- 详细协议与使用边界见 [M39 实现记录](performance/PHONE_MIC_M39_2026-10-16.md)。规划文档中的 TLS/长期信任和采集器按需重开为后续阶段；M41自动联动仅为当前端点可校准时的实验实现。
- 最新实机范围与设备方向见 [M40 现场记录](performance/PHONE_MIC_M40_2026-10-17.md)。

```
┌─────────────────┐   WS 8899 控制面(握手/心跳/文字/列表/广播)    ┌───────────────┐
│  Android 端 N 台  │◄──────────────────────────────────────────►│   PC 端流转     │
└─────────────────┘   HTTP 8900 数据面(/file /upload,带鉴权)      │ (WS+HTTP+UDP) │
                  UDP 8901 发现(DISCOVER/OFFER 广播应答)          └───────────────┘
```

- **PC = Hub 服务器**：WPF (.NET 7)，Fleck WebSocket + 手写 TcpListener HTTP。
- **Android = 客户端**：Kotlin 2.0.21 + Compose（BOM 2024.12.01）+ OkHttp 4.12 + DataStore；`minSdk=compileSdk=targetSdk=36`，只支持 Android 16+。namespace `com.liuzhuan.app`。
- 端口：`LanServer.Start()` 使用 `LanConfig.Port`(默认 8899) 作为 WS，HTTP 为 port+1；UDP 发现固定 8901，在 OFFER 中返回实际 WS 端口。修改连接端口不改变发现端口。
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
                         → AutomaticClipboardDelivery.submit → ClipboardEventQueue # FIFO max 50
                         → 单一进程级发送器 → LanClient → 等待 stored 回执
                         → 支持幂等回执时有限重试；断线等待，过期/拒绝/隐私变更明确结束
```

| 组件 | 职责要点 | 关键方法 |
|---|---|---|
| `ClipMonitorService` | 无障碍服务薄触发器；`isRunning` 三态检测信号之一；诊断通知 | `onAccessibilityEvent` |
| `AccessibilitySourcePolicy` | 来源五分类 SELF/SYSTEM_UI/LAUNCHER/THIRD_PARTY/UNKNOWN | `classify`、`shouldInspect` |
| `CopyEventDetector` | 强弱信号评分；HIGH=第三方 selection 变化/命中「复制」按钮文案点击；MEDIUM=第三方窗口内容变化；LOW 不单独触发 capture | `evaluate` |
| `ClipboardCaptureManagerImpl` | 捕获编排；selection 走 `AccessibilityNodeInfo.textSelectionStart/End`（限深 2 层 ×16 子节点）；剪贴板走 `getPrimaryClip` best-effort | `onAccessibilityEvent`、`tryCaptureAfterCopy`、`tryReadClipboard`、`captureOnForeground` |
| `ClipboardEventDispatcher` | object 单例，捕获事件的唯一出口 | `dispatch` |
| `ClipboardDeduplicator` | 指纹去重 + 本机写入标记（回环防护） | `shouldProcess`、`markLocal` |
| `ClipboardEventQueue` | 待确认缓存 FIFO max 50，原子更新；保留 5 分钟 | `enqueue`、`head`、`update`、`remove` |
| `PushToPcAction` | pipeline 中唯一动作；提交进程级回执发送器 | `execute` |
| `AutomaticClipboardDelivery` | 同一请求最多 3 次；协商能力、隐私、目标身份与期限约束 | `submit`、`wake`、`acknowledge`、`changeTarget` |

统一事件模型 `ClipboardEvent`：text/timestamp/sourcePackage/source/mimeTypes/fingerprint(SHA-256)/type/diagnosticId(`COPY-xxxxxx` 贯穿全链日志)，另有稳定 requestId(UUID) 与本地 privacyRevision。

### 2.2 入口与装配（谁在何时接线）

- **`ClipboardMonitorCoordinator`**：object 进程级单例，唯一装配点。持有 deduplicator/queue/pipeline/captureManager + appInForeground(ActivityLifecycleCallbacks) + diagnostic/diagnosticHistory(StateFlow)。`init(context)` 幂等双检锁，被两处调用：
  - `ClipMonitorService.onCreate`（无障碍已启用时）
  - `MainActivity.onCreate`（无障碍未启用时前台测试可用）
- **`MainActivity.onCreate`**：调用幂等 `LanController.init(applicationContext)`，连接、7 个回调、网络监听及 `MaterialRepository.onSequenceGap → client.requestSnapshot` 由进程控制器持有。控制器管理前台服务启停（Connected→startForegroundService+`Coordinator.onWsConnected()` 唤醒回执发送器；AuthFailed/Paused→stopService；**Disconnected 不停**）。Activity 重建只重新订阅状态。
- **ON_RESUME 兜底**：接收页 Compose `DisposableEffect` 生命周期观察里刷新三态无障碍状态 + `captureManager.captureOnForeground()` 直接读剪贴板走同一 Dispatcher。
- **直连路径（不经 Dispatcher/Pipeline）**：
  - `ProcessTextActivity`（ACTION_PROCESS_TEXT 选中文本直达）：pushClipboard 直发 + 写剪贴板 + markLocalText；
  - 分享菜单（SEND intent）：文字走 `LanClient.sendText`，文件走 OkHttp POST `/upload?name=&auth=`（HTTP 端口为所选电脑 WS 端口 + 1）。

### 2.3 连接层 `LanClient`

- 状态机：`Idle → Connecting → Connected / AuthFailed`；意外断开（onClosed/onFailure 且非 manuallyClosed）设 `Disconnected` 并指数退避重连 1s→30s 上限；主动 `disconnect()` 设 `Paused` 不重连（与 Disconnected 的语义区分是 M11 保活修复的关键）。
- 回调七参数：onState/onLog/onListData/onItemAdded/onItemDeleted/onItemCleared/onItemData，全部由进程级 LanController 提供。
- 防护细节：`webSocket !== ws` 早退防旧连接回调触发重连死循环；connect/disconnect/doConnect 前都关闭旧 WS；心跳由共享 OkHttp 客户端 pingInterval 30s 维持（应用层 heartbeat 仅兼容解析，无周期发送）；失败计数跨任务保留，退避上限 30s；无局域网路径时等待网络回调，恢复即重试。
- 构造参数默认 `CoroutineScope(SupervisorJob() + Dispatchers.IO)`——进程级。

### 2.4 接收页数据层

- `MaterialRepository`：object 单例，StateFlow<List<MaterialItem>> + SyncState(IDLE/SYNCING/SYNCED/RESYNCING/ERROR)。内部 LinkedHashMap 以 id 为键。
- 同步语义：`replaceAll`(快照)、`add`/`delete`/`clear`(增量)。三类增量统一先验序：陈旧/重复忽略，缺口保留当前画面并一次性请求新快照。同 ID 新序号更新摘要，不可见 ID 删除也消费序号；markSyncing 等待新连接快照，replaceAll 包括序号 0 都重置基线。排序按 time 降序，同秒保持快照次序并恢复最多 512 个近期删除 ID 的原位置。
- UI：MainScreen `collectAsStateWithLifecycle` 主线程 collect；列表仅展示 take(50)。
- 快照序列号语义注意：PC `list_data` 里每条 item 的 sequence 都是**全局计数器同一时刻的值**（见 §3.3），逐条单调递增只对 item_added 增量成立。

### 2.5 保活与配置

- **日常交付构建**：`assembleDistribution` 继承 Debug 的应用源码与签名，关闭 `isDebuggable`，不启用代码混淆；Debug 保留供开发，依然使用原正式签名（D01/D33）。`scripts/check_android_distribution.py` 检查实际 APK 的包名、调试标记、ClipReceiver action 和 Xposed 入口；CI 编译 Debug + Distribution，上传后者。无私人 keystore 的 CI 包使用测试证书。

- `ForegroundService`：常驻通知 + START_STICKY + 幂等 PARTIAL_WAKE_LOCK + WIFI_MODE_FULL_LOW_LATENCY WifiLock；默认即时模式维持后台基线，均衡模式按工作信号和传输数续期 / 释放；manifest 声明 `foregroundServiceType="connectedDevice"` 和网络前置权限。职责只有保活，不碰剪贴板不碰连接逻辑。
- `SettingsStore`（DataStore "settings"）：serverIp/serverPort(默认"8899")/password/deviceName/autoSendClipboard。autoSend 开关运行时经 `LanHub.autoSendClipboard`(@Volatile) 直达 Dispatcher 过滤，DataStore 持久化由 UI 双写。

## 3. PC 端模块

| 组件 | 职责要点 |
|---|---|
| `MainWindow.xaml.cs` | 组合根：创建 DataStore/LanServer/ClipboardMonitorService；注入 SnapshotDispatcher/SnapshotProvider，在一次 UI 调度中捕获最近 50 项和 CurrentSequence 并排入 WS 发送，避免取快照后增量抢先；订阅 DataStore 三事件广播；OnLanTextReceived→Add 广播给所有设备；ItemLookup 提供详情，剪贴板开关控制复制收集 |
| `Services/DataStore.cs` | ObservableCollection 素材库 + JSON 持久化（500ms 防抖、tmp 文件替换、锁重试 3 次）；变更序号 `_sequence`(Interlocked 递增，**内存计数重启归零**)；事件 ItemAdded/ItemRemoved/ItemCleared；删除撤回 undo 栈 50 步；加载时校验源文件存在性 |
| `Services/Lan/WsHub.cs` | Fleck 会话管理 ConcurrentDictionary；消息 switch(hello/heartbeat/sync_text+clipboard_push/list_sync/get_item/bye)；welcome 前 AuthService 校验；**同名设备重连自动踢旧 session**；Broadcast* 三方法全局广播 |
| `Services/Lan/LanServer.cs` | 组合根子服务(WsHub/FileHttpServer/UdpDiscovery)，`IsListening` 用 TCP 自连 127.0.0.1:Port 自检（Fleck 静默失败检测） |
| `Services/Lan/AuthService.cs` | sha256 口令哈希比对 + Unix 秒 ts ±300s 防重放；对照目标为 `LanConfig.PasswordHash` |
| `Services/Lan/FileHttpServer.cs` | GET `/file/{id}?auth=`、GET `/thumbnail/{id}?auth=`、POST `/upload?name=&auth=`；上传先写 `.part`，收齐声明字节数后转为正式文件并触发 FileUploaded；三种端点使用同一口令鉴权 |
| `Services/Lan/UdpDiscovery.cs` | 固定绑定 UDP 8901；收到 `LIUZHUAN_DISCOVER` 应答 `LIUZHUAN_OFFER|ip|wsPort|设备名` |
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

#### M38 成功连接历史（2026-10-14）

- LanClient welcome 分支新增默认空的 onConnected 回调；LanController 在进程级 IO 作用域写入 ConnectionHistoryStore，不阻塞握手和素材快照。历史最多 20 个，按 host + port 去重，元数据与 AES-GCM 密文存于独立 DataStore。
- ConnectionHistoryUi 展示最近目标、时间与连接/删除操作；clear 有确认。恢复先解密历史口令并读取最新 Settings，再进入原连接路径。历史存储错误与当前连接独立，Android Keystore 由首次保存时的 IO 工作初始化，不在 Service/Activity 构造阶段读取 Context。

#### M37 手机热点可用性（2026-10-14）

- 手机作为热点提供方时，ConnectivityManager 可能只报告蜂窝上游网络；热点本身由 TetheringManager.TetheringEventCallback.onTetheredInterfacesChanged 单独通知。LanController 在进程级幂等初始化中注册两个监听，LanRouteState 合并它们，只有实际可用性改变时才通知 LanClient。
- 不依赖热点是否有外网，不申请新的存储/网络权限；普通蜂窝数据独自存在仍不视为局域网。热点已开启的冷启动、热点与普通 Wi-Fi 切换均由回调驱动，没有新增定时轮询。

#### M35 收发控制与保存位置（2026-10-11）

- Android `TransferTaskDialog` 使用全屏 Dialog、任务状态筛选；M36 起所有类型通过 MediaStore.Downloads 写入固定 `Download/Liuzhuan`，成功清除 IS_PENDING 后将实际相对目录记入任务。顶部按钮直接浏览统一目录，逐项按钮兼容旧位置。`ReceivedFiles` 只用目录 ACTION_VIEW；默认限定系统处理器并优先 DocumentsUI，也可指定兼容的管理器。不调用 ACTION_OPEN_DOCUMENT_TREE，不额外获取存储授权；目录由首次文件写入创建。
- Android 文件暂停取消当前 I/O，保留来源与重试回调，清理结束后显示暂停；继续沿原回调从头传输，取消释放缓存并移除继续入口。进程级 TransferWork 的旧任务清理/同 ID 重试等待机制保留，已提交文字不可撤回。
- PC `TransferWindow` 为 840 × 680 的可调整窗口；`TransferControl` 在每次读写边界等待异步门闩，暂停当前连接最多 90 秒；取消中断读写/门闩并关闭远端流，上传部分文件删除后才显示已取消。停止 LAN 也取消门闩。HTTP / WS 字段与旧端点不变，不支持跨断线字节续传。
- PC 完成接收条目保存原始上传路径；`FolderLauncher` 通过 ArgumentList 调用系统 Explorer 或用户指定 EXE，参数模板按引号拆分后替换 `{folder}`，不经 shell。当前电脑偏好保存为 `data/file-browser.json`，不把可执行程序路径迁移到新电脑。
- 两端任务记录仍限本次运行；暂停记录视为未结束，和活跃记录一样不被历史淘汰。超过保留数量时只淘汰普通结束记录。断点续传需另加文件身份、分块/Range 与偏移校验，当前暂停不冒充此能力。

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

### 迁移与备份（电脑端 v1.3.0）
- `MigrationWindow` 提供导出、导入预览、后台复制进度、取消及退出入口；设置菜单接入。导出在 UI 线程获取素材与设置快照，文件 IO 在后台运行。
- `MigrationService` 定义 v1 `.liuzhuan.zip`（manifest.json + files/<guid>/<name>）。按原路径去重复引用，打包文件字节、文字、收藏、时间和缩略图；不打包程序、日志、旧绝对目录配置或非索引历史文件。逐文件 SHA-256，临时输出成功后才替换目标 ZIP。
- 导入严格校验版本、清单、文件大小、SHA-256 与引用路径，拒绝重复/穿越路径；创建默认 data/imports/<guid> 独立目录，把素材路径写为新机器绝对路径。索引上限 32 MiB、10 万条；文件总量上限 1 TiB，并检查磁盘可用空间。
- 配置通过强类型白名单迁移：ClipboardMonitorEnabled、HeicConversion、LAN Enabled/Port/Password、AutoStart。开机自启在新机器使用当前 exe 路径重新应用，不复制注册表命令。
- `migration-pending.json` 仅记录待切换库的生成 ID；`App.OnStartup` 在构造 MainWindow/DataStore、加载静态配置之前调用 ApplyPending，备份旧 config.json 后原子更新数据目录指针。原库保持不动，避免运行中 DataStore 延迟保存写进新库；生效后移除 pending。
- 如需人工回退：先退出流转，备份当前默认 data/config.json，再将对应 config.before-import-*.json 复制为 config.json；保留所有 imports 目录。配置备份只保存路径指针，原素材文件仍在原位置。
- 检查入口 `scripts/MigrationChecks` 使用独立临时目录，验证旧原路径不可用下的迁移、同名文件/重复引用、收藏文字和配置、生产 DataStore 加载、取消清理、损坏/路径攻击拒绝；不触碰实际用户素材或自启注册表。

### 拖窗空白与贴边稳定性（电脑端 v1.3.1 本地修复）
- 已复现旧版 UiMotion 的同帧竞态：收起动画排队后立即展开/归零，旧 Render 回调仍启动，最终 X=372，窗口保留展开宽度却只显示背景。每个对象/属性现在使用独立版本，同时拒绝过期启动与 Completed；同步归零也会使旧动画失效。
- BeginWindowDrag 暂停热区与收起定时器、取消主面板动画并恢复完整布局；拖动中不响应伸缩，EndWindowDrag 统一贴边并恢复定时器。右侧释放保持展开，移开鼠标后按原延迟收起。
- 贴边工作区按窗口所在显示器及其当前缩放换算为 DIP；热区只响应同一显示器，不因鼠标换屏移动窗口。显示器变化时重新定位，清除旧左锚定状态。
- DesktopUiChecks 新增排队后归零、同帧收起展开、属性间动画独立、拖动中保持可见等回归，完整 43 项通过；150% 离屏渲染完整。真实鼠标跨屏、混合 DPI 和显卡合成仍需实机验证。

### 批量操作与内容另存（电脑端 v1.4.0 本地版本）
- MainWindow.Selection 管理可见列表的选择栏和操作；按当前视图顺序取得选中项。搜索及分类只操作当前结果；卡片按下保留多选以拖出，普通松开转为单项复制。
- DragDropService.TryCreateSelectionData 供批量复制和拖出共用；全部文件提供 FileDrop，全部文字按顺序组合 UnicodeText，混选/缺失文件拒绝整体操作并提示另存。
- MaterialExportWindow 捕获不可变快照后调用 MaterialExportService。后台生成独立子目录，顺序流式复制；有 FilePath 的文字素材复制全文原文件，纯文本按 UTF-8 编码。每项状态和节流字节进度反馈至 UI，取消按缓冲检查；仅清理记录在案的输出，不递归删除目标目录。
- DataStore 撤回记录原始顺序及位置；恢复逐条递增 sequence 并发 ItemAdded。批量删除逐条序号；保留其他项的清空使用 ItemRemoved，真正删空才使用 ItemCleared。现有 Android 协议兼容，删除后的 gap 可触发既有快照重同步。
- 自动检查入口：DesktopUiChecks（界面/选择/拖拽数据/动画）、MaterialExportChecks（临时文件字节与路径/取消）、DataStoreChecks（增量镜像及撤回顺序）。均使用隔离内容，不启动实际素材库。

### 分组、标签和备注（电脑端 v1.5.0 本地版本）
- MaterialItem 持久化 GroupName、Tags、Note；旧索引缺字段/null 对应空值。Tags getter 返回副本，派生摘要不写入 JSON。编辑不改原文件、添加时间或素材顺序。
- MaterialOrganization 统一校验编辑与迁移：分组 48 字符、标签 20×32 字符、备注 4000 字符；组名为单行，单个标签不含编辑器分隔符或控制字符，备注保留换行。不截断超限内容。
- DataStore.UpdateOrganization 以 null 保留字段、空值清除字段；支持标签追加/替换/移除，先校验整个批次再应用并延迟保存。本地整理不触发 LAN 增量或消耗 sequence，Android 协议不变。
- MainWindow.Organization 与 MaterialOrganizationWindow 提供单项/批量编辑、分组/标签筛选；分组由现有素材归纳，不单独保存空分组。GetFiltered/Search 将元数据条件与分类、收藏、内容搜索组合。
- 清空当前结果先捕获可见 ID；一般视图仅移除非收藏，收藏视图只取消收藏。工具窗口打开期间暂停自动收起，独立标志避免右键菜单关闭时解除保护。
- 迁移包仍为格式 v1，新增可选组织字段，快照与导入包含它们；旧包兼容。旧程序重新保存索引会丢弃未知字段，因此要保留信息需使用 v1.5.0 或更新程序。普通内容另存不携带内部元数据。
- 自动检查：OrganizationChecks 验证批量事务、持久化、搜索与兼容；DesktopUiChecks 增加编辑/筛选/移除/撤回与模态窗口保护；MigrationChecks 验证组织字段往返及无效输入拒绝。

### 常用筛选与可靠同步（两端 v1.6.0 本地版本）
- SavedMaterialView 保存 Name/Tab/Keyword/GroupName/Tag 条件，不保存素材 ID；null 分组不限、空字符串未分组。SavedViewStore 在当前有效素材目录保存 savedviews.json：最多 30 条、名称 40 字符、搜索 256 字符，编辑与迁移共用校验；原子写入后才更新内存，损坏文件不自动覆盖。
- MainWindow.SavedViews 提供一键应用、保存、更新、重命名和删除；顶部入口不增加额外高度。实时筛选继续使用 DataStore.Search，规则自然包含后来匹配的素材。保存命名窗口使用模态收起保护。
- MigrationService.Settings 增加可选 SavedViews，快照深复制设置，导入在独立库生成 savedviews.json；缺字段/null 视为空，超限/重复名/无效分类在激活前拒绝。
- UdpDiscovery 固定 UDP 8901，OFFER 保持原字段并返回实际 WS 端口；局部捕获 socket/token 加生命周期锁，Stop/重启不让旧任务操作新 socket。Android 单调截止时间限制接收预算，保留 IO 与取消。
- LanClient 删除/清空回调透传现有 sequence；MaterialRepository 对全部增量验序，更新同 ID 摘要并消费每次变化。PC SnapshotProvider 显式给全局序，包括空库；SnapshotDispatcher 将捕获与发送放在素材变更的同一 UI 调度，协议字段不变。
- 检查入口：SavedViewChecks、DesktopUiChecks、MigrationChecks、SnapshotChecks（临时端口实际 WS）、LanAddressChecks/UdpDiscoveryChecks（固定 8901 实际 UDP，先停用户程序）、AndroidSyncChecks（链接生产 Repository 和模型，只有日志/Proto 类型为桩）。真机后台复制和物理跨屏仍单独验收。

### 来源管理、隐私与图片动作（两端 v1.7.0 本地版本）
- MaterialItem 缓存 IsSourceMissing（不持久化），DataStore.Load 保留失效索引。SourceRelinkService 捕获只读快照，后台检查或查找候选；DataStore 在所属线程按 ID+旧路径应用检查结果。重新定位先验证整个批次，再更新路径/大小/缩略图；保留 ID、时间、位置及组织字段，每条更新有 fresh sequence。
- SourceRepairWindow 在具体文件夹后台扫描，不扫描盘符根目录或链接；文件名、类型及已知大小只用于候选提示，歧义/扫描受限不当作唯一结果。批量应用前提供路径审阅与明确确认。已被旧程序删除的索引不在新版本自动恢复范围内。
- DeferredMaterialLoader 以源路径、类型、SourceRevision 和每项加载代数校验 UI 回写；过期任务不能覆盖新缩略图/时长或触发完成回调。
- PC ClipboardMonitorService 在读内容前检查暂停与 owner 进程排除，读取前后校验 clipboard sequence。名单非空时未知 owner 保守跳过；自身复制不回采，不同内容快速复制不因旧 600ms 节流丢失。恢复启动监听不导入现有剪贴板内容。
- AppSettings 原子保存 ExcludedClipboardApps/ClipboardPausedUntil，损坏隐私配置阻止自动采集并保留原文件；隐私窗口与素材栏显示暂停状态，以有界一次性期限任务刷新，不轮询剪贴板。迁移仅保存排除名单，不带临时暂停；Text 文件有源路径时打包完整字节。
- Android ClipboardPrivacyPolicy 为无 Context 的纯策略，ClipboardPrivacyPreferences 负责设备本地持久化；版本标记限制旧设置仅在首次迁移时生效，配置先在 IO 线程确认写入再发布，损坏或保存失败时保守阻止自动采集。捕获时间、来源及运行期版本约束 Dispatcher、异步 Action、两处 flush、ClipReceiver 文件旁路与自动文件重试。设置在连接页自动同步卡片，显式分享/发送不受自动规则影响；核心 scope、检测评分、LSPosed Hook 和服务保活不变。
- ImageProcessingService 独立 STA 使用 WIC 首帧解码、EXIF方向转换、只缩小和 PNG/JPEG 编码；JPEG 白底，输出不携带拍摄元数据。限制 6400 万像素/32768 单边，逐项失败可继续，输出新建独立目录，取消只清理自身文件。原生编解码期间取消需等待当前操作结束；HEIC 实际兼容取决于系统解码器。
- 检查：SourceRelinkChecks、DataStoreChecks、DeferredMaterialChecks、PrivacyChecks、AndroidPrivacyChecks（可复跑 PowerShell runner）、ImageProcessingChecks、DesktopUiChecks 与 MigrationChecks；真实手机、GPU/混合 DPI 和不同路由器仍独立验收。

### 回执、完整性与图片预设（两端 v1.8.0 本地版本）

- 自动文字保留稳定请求编号，等待电脑登记回执；welcome 协商幂等回执支持。兼容服务器按请求重发回执，冲突载荷拒绝；旧端仅提交一次，明确未确认。发送状态、隐私规则和目标变化约束有限重试，进程内待发记录与服务器幂等缓存均有界；登记成功不等于持久化事务。
- MaterialItem 的可选 MaterialIntegrityBaseline 保存 SHA-256、长度、确认时间与源路径/类型；SourceRevision、IntegrityRevision 使异步结果失效。IntegrityService.Snapshot → CheckAsync 只读后台文件，DataStore.ApplyIntegrityResults 应用当前结果，SetIntegrityBaselines 在确认后再次验证来源和元信息。
- MaterialIntegrityWindow 打开不扫描，用户开始后读取当前列表；取消不应用部分结果。初次记录、接受外部编辑和移除基线均明确确认；“上次检查正常”只代表当次字节，不持续监控。
- ImageProcessingPresetStore 原子保存素材库 imagepresets.json，严格校验版本、20 个上限与格式/尺寸/质量。ImageProcessingWindow 选择预设仅填参数；损坏预设保留原文件，显式恢复先保留并校验副本。MigrationService 可选 Settings.ImagePresets 写入独立新库，旧包无此字段仍可导入。
- 迁移包的 payload SHA-256 校验传输字节；素材内容基线记录用户以前确认的内容。两者不能互相替代：来源已变化时导入保留旧基线，使后续检查仍报告变化。
- 检查：AutoReceiptChecks 包含实际 LanClient/OkHttp ↔ WsHub、幂等与状态机，AndroidPrivacyChecks 保留捕获隐私回归；IntegrityChecks 检查实际文件、取消与迁移，ImagePresetChecks/ImageProcessingChecks 检查参数持久化及真实图片/UI，DesktopUiChecks/迁移/快照/HTTP 检查共同回归。物理安卓、后台 ROM 行为和真实 GPU/DPI 单独验收。

## Android v1.8.2 性能与功耗

连接由进程级 LanController 持有，Activity 仅订阅状态；客户端 / 连接池共享。传输由 TransferWork 限制为 2 个活跃、最多 16 个在途任务，支持取消底层 Call。Hook 文件读取上限为 512 KiB，在有界工作线程执行，字节广播保留原复制时间；接收兼容旧 Base64，异步解码并短窗口去重，重试持有缓存路径。MEDIUM 选中文本捕获使用单消费者合并最新候选，保留尾沿读取；评分和进程级发送作用域不变。素材仓库维护有序索引，仍保留完整序号与撤回语义。
