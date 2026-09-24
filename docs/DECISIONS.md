# DECISIONS — 仍然有效的设计决策

> 只收录经源码核实**当前仍然有效**、且改动会引发回归或故障的决策。
> 每条包含：决策内容 / 原因（含历史依据）/ 当前源码锚点 / 改动约束。
> 已被推翻的历史方向不收录于此（见 `logs/项目迁移交接文档_20260827.md` §3 与 git log）。

---

## D01 debug 构建使用 release 签名

- **决策**：debug 与 release 共用同一 keystore 签名。
- **原因**：签名不同会导致覆盖安装需先卸载 → 无障碍授权被系统清除。同签名可直接覆盖升级。
- **锚点**：`AndroidApp/app/build.gradle.kts` 的 `buildTypes.debug.signingConfig`；keystore 在 `AndroidApp/keystore/`（gitignore）。
- **约束**：不能改回默认 debug 签名；keystore 文件丢失时新环境需重建或恢复。

## D02 minSdk = 36（只支持 Android 16+）

- **决策**：三端 SDK 版本锁定 36。
- **原因**：`AccessibilityServiceInfo` 在 API 36 从 `android.view.accessibility` 迁移到 `android.accessibilityservice` 包（无障碍三态检测代码已用到后者），锁定可免兼容分支。
- **约束**：降级是独立专项——需处理包路径分叉与各 API 差异，不允许顺手改。

## D03 双服务分工架构

- **决策**：无障碍服务（ClipMonitorService）只负责感知复制事件；前台服务（ForegroundService）只负责保活（Wi-Fi/CPU 锁）。
- **原因**：职责分离避免前台服务读剪贴板/轮询的合规问题，也让保活生命周期独立于监控状态。
- **锚点**：`ForegroundService.kt` 全文无剪贴板逻辑；`MainActivity.onState` 控制其启停。
- **约束**：两条边界都不能越——前台服务里加采集逻辑、无障碍服务里管连接都属于越界。

## D04 评分模型基线（M10→M13 的教训固化为规格）

- **决策**：
  1. 第三方 App 的 `WINDOW_CONTENT_CHANGED` = **MEDIUM**（触发 selection 捕获 + debounce 剪贴板兜底）；
  2. SystemUI/Launcher 的 `WINDOW_CONTENT_CHANGED` = **NONE**；
  3. 第三方 `TEXT_SELECTION_CHANGED` 与命中「复制」文案的点击 = HIGH。
- **原因**：微信等 App 复制只产生 WINDOW_CONTENT_CHANGED（不暴露 selection 变化事件），MEDIUM 是它们唯一的检测入口（M6 生效路径）；M10 曾把它降到 LOW 导致 capture 完全不执行，M13 恢复。SystemUI/Launcher 弱信号是高频假阳性主来源（反向教训同样成立）。
- **实测边界（2026-08-27 adb 确认）**：MEDIUM 的捕获在本机 ColorOS 上仅前台有效（焦点就绪时 selection/剪贴板都可命中）；流转后台时 selection 不暴露 + 剪贴板被焦点检查拒绝，两条捕获路都被关死。「不切回流转的后台自动发送」在严格执行焦点限制的 ROM 上无解（见 KNOWN_ISSUES LIMIT-1），微信场景走文本选择菜单或切回流转发。MEDIUM 基线保留的意义：前台事件驱动路径 + 对焦点限制宽松 ROM 的兼容。
- **锚点**：`CopyEventDetector.evaluate`。
- **约束**：调优评分时两个方向的基线都不可动；消除假阳性只能加更精确的分类规则，不许整体降级。

## D05 循环回写防护用 markLocalText（双入口）

- **决策**：本 App 写入手机剪贴板的每个入口都必须随后调用 `ClipboardMonitorCoordinator.markLocalText(text)`。
- **原因**：PC 推来的素材复制到手机后若无防护，无障碍会再读到相同内容回推 PC 形成死循环。指纹被记入 Deduplicator 后即可抑制回推。
- **锚点**：`MainActivity.copyToClipboard`（接收页点击素材复制）+ `ProcessTextActivity.onCreate`（选择菜单顺带写剪贴板）。两处缺一不可。
- **约束**：新增任何写剪贴板代码路径必须带 markLocalText。

## D06 发送链协程作用域全部进程级 SupervisorJob

- **决策**：`ClipboardCaptureManagerImpl` / `ClipboardActionPipeline` / `LanClient` 的 scope 一律 `CoroutineScope(SupervisorJob() + Dispatchers.x)` 默认参数。
- **原因**：后台复制的捕获→分发→发送必须不依赖 Activity/ViewModel 存活。
- **约束**：改成生命周期 scope 会静默失效（编译期无错）；新增后台组件沿用同一模式。

## D07 断线缓存 + 重连补发（承认双 flush 路径现状）

- **决策**：WS 未连接时捕获事件进 `ClipboardEventQueue`(FIFO max 50)；Connected 后由 `Coordinator.onWsConnected()` drain 补发；PushToPcAction 在连接态发送前也会前置 drain 积压再发当前（FIFO 保序）。
- **原因**：网络发送与捕获解耦，断线不丢件也不阻塞监听。
- **现状说明**：flush 绕过 Dispatcher/去重属刻意设计（事件在入队前已过 Dispatcher）；drain 原子性保证不会重复发送同一事件。
- **约束**：改 flush 逻辑时保持「drain 原子取走」模式，不要改成「peek+确认」而引入竞态。

## D08 接收页单一事实源：StateFlow + collectAsStateWithLifecycle

- **决策**：PC→Android 同步数据只落在 `MaterialRepository`（object 单例 StateFlow），UI collect 主线程呈现；WS 回调只调 Repository 方法。
- **原因**：旧方案（OkHttp 回调线程直接写 mutableStateListOf 作 UI 唯一数据源）脆弱且违反单向数据流，M7 重构废弃。
- **约束**：不允许在 Screen 层自建列表状态承接 WS 增量。

## D09 事件驱动取代轮询

- **决策**：无障碍事件驱动检测 + 捕获失败自然结束（最多三次短延迟重试共 ~260ms），无定时器。
- **原因**：后台读剪贴板受焦点限制，轮询既耗电又读不到，M6 已删除 2s 轮询。
- **约束**：「禁止恢复剪贴板 polling」；注意区分概念——HIGH 触发后的 80/160/260ms 短延迟重试是「事件后的等待更新」，不是轮询，勿当轮询清理。

## D10 捕获事件统一出口为 Dispatcher（含已知例外）

- **决策**：所有**捕获产生**的事件必须产 `ClipboardEvent` 经 `ClipboardEventDispatcher.dispatch` 过滤/去重后再进入 ActionPipeline；禁止捕获层直接调 WebSocket。
- **已登记例外**：`ProcessTextActivity`（选中文本直发）与分享菜单文字（`sendText`）是用户显式动作直连 LanClient，不经 Pipeline —— 属于「用户指令优先于自动链路开关」，不是缺陷模式；但改造它们时应并入统一管线而非扩散第三条直连路径。
- **约束**：给 Dispatcher 加过滤条件时明确它不影响上述直连路径（例如 autoSend 开关目前拦不住 ProcessText 直发，这是接受的行为还是 bug 见 KNOWN_ISSUES DEF-5）。

## D11 后台复制秒达的唯一解：LSPosed hook 剪贴板写入路径

- **决策**：接受 Android 10+ 后台剪贴板「读取」焦点限制（ColorOS 豁免链仅 IME/焦点 App/SystemUI/privileged/READ_CLIPBOARD_IN_BACKGROUND）。**后台复制秒达靠 LSPosed hook 剪贴板「写入」路径**（`ClipHook` hook `ClipboardManager.setPrimaryClip`，事件驱动、不读剪贴板、绕开全部读取豁免链）；前台主动重读 + 文本选择菜单（ACTION_PROCESS_TEXT）作无 LSPosed 环境的兜底。
- **原因**：ColorOS 对「读」做了 checkPackage/焦点多重拦截（root daemon 也被 `SecurityException: Package android does not belong to 2000` 拒死），但「写」不受这些限制——hook 写入路径是唯一能 100% 捕获任意 App 后台复制的方案。
- **锚点**：`xposed/ClipHook`、`ClipReceiver`、`ProcessTextActivity`、`captureOnForeground`。
- **约束**：LSPosed 需用户装框架（KernelSU + Zygisk + LSPosed，本机为 Irena fork）；ClipHook 只旁路观察、不改返回值、不抛异常；ClipReceiver 的 intent-filter（action=com.liuzhuan.app.ACTION_CLIPHOOK）不可删。

## D12 Hub 信任模型：口令即权限，明文局域网

- **决策**：电脑端=服务器；凭 6 位数字口令自动接入（无逐台弹窗确认）；传输明文（WS/HTTP/UDP），安全手段=sha256 口令哈希 + ts 防重放 + HTTP 层同套鉴权。
- **原因**：局域网场景下简单可靠优先（2026-08-03 开发文档定稿决策 #5/#6，其余仍有效部分一并沿用：Hub 模式、Fleck、分享菜单截图路径）。
- **约束**：升级加密/配对流程属协议级变更，两端同步设计。

## D13 文件数据面手写 TcpListener HTTP

- **决策**：8900 数据面是 `FileHttpServer` 手写极简 HTTP（TcpListener），不用 http.sys/Kestrel。
- **原因**：规避 http.sys URL ACL 权限坑与重依赖。
- **约束**：扩展端点时保持鉴权一致性（auth 参数校验放最前面）。

## D14 同步语义：全量快照 + 三类增量广播 + sequence gap resync

- **决策**：连接成功先拉 `list_data` 快照；此后 PC 侧 Add/Remove/Clear 分别广播 item_added/item_deleted/item_cleared；Android 对 add 做 sequence 连续性检测，跳变则 RESYNCING 并重新拉快照。
- **原因**：取代 v0.2 设计「仅新增」（M7 起实现删除/清空联动）；gap 检测兜底丢消息场景。
- **注意**：gap 检测只在 add 路径存在，delete/clear 不检测（丢失的删除靠下次快照纠正——接受的弱保证）。

## D15 多设备模型与同名设备会话替换

- **决策**：服务端全局广播所有变更给全部会话（多台手机天然一致）；同一 device 名重连时踢掉旧 session 再建新 session。
- **原因**：安卓 Activity 重建/闪退重连会产生叠加会话，导致设备列表脏数据与重复广播。
- **锚点**：`WsHub.HandleHello` 中 oldKey 清理逻辑。
- **副作用认知**：手机自己推送的文字也会经 DataStore.Add 广播回自己（含广播语义的自然结果，接收页能看到自己刚发的条目，属预期行为）。

## D16 收发状态以实际完成阶段为准（M27）

- **决策**：上传收齐文件并登记后才确认；手动文字在登记回调完成后返回带 requestId 的 ack。手机保存成功必须等 MediaStore 文件关闭及 IS_PENDING 清除，PC 写出文件只能显示「已发送」。
- **原因**：提交到网络缓冲、服务器接收与手机落盘是三个不同阶段，不能都显示为已送达；未收完整的文件不得进入素材库或相册。
- **约束**：保持 `Proto.kt` / `WsHub.cs` 字段同步，保留原剪贴板进程级作用域。任务记录限本次运行，失败由用户手动重传；未确认的上传可能已到达，重试前提示核对，不能承诺去重或断点续传。预览与缩略图独立于原素材，鉴权与文件端点一致。

## D17 动效服务于状态反馈，不改变业务与命中（M28）

- **决策**：反馈短、面板略长，所有动效可被新操作替换并跟随系统动画设置。关闭动画时同步设置最终状态并执行完成逻辑；连续反向不能让旧回调缩起新面板。
- **原因**：跟手与状态稳定优先，动画不能阻止点击、重复发起传输或重建整页素材。快速操作、低性能设备和关闭动画均需有明确结果。
- **约束**：PC 卡片只变换内部视觉层；Android 页面不使用双份内容承载业务副作用。禁止给所有卡片挂持续动画或以延迟完成提示模拟传输进度；真实帧率与触感须在设备上验证。

## D18 发现流程不能阻塞界面，连接地址必须来自可用 LAN（M29）

- **决策**：二维码过滤隧道和虚拟网卡并提供候选选择；发现响应考虑请求者子网，Android 优先采用回包源地址。所有 UDP 操作在 IO 调度器执行，异常显示在连接页，取消时释放 socket。
- **原因**：例如 vgate0 的 172.30.99.1/32 曾被选入二维码，手机实际在 192.168.50.0/24；原搜索直接在 Compose 主线程发送 UDP 且没有异常处理，存在闪退路径。
- **约束**：不把全部 172.* 地址当作虚拟网络，不自动改用户 VPN 或防火墙。二维码无法预知手机接入哪张网卡，多网卡需用户选择；接收列表采用懒加载，整页动画不作为切页的必要步骤。核心剪贴板作用域和服务保活保持原约束。

## D19 迁移先完整校验，再于下次启动切换独立库
- **决策**：导入不向现有 DataStore 逐条写入，也不在运行中直接切 ConfigService 路径；先在新目录解包并重建路径，再记录 pending，下次启动切换并保留原配置指针。
- **原因**：DataStore 保存路径是动态计算且有延迟保存，运行中切换会让旧内存索引覆盖新导入的数据。普通素材收集只保存绝对路径，迁移必须包含文件字节，不能直接复制旧 data.json。
- **约束**：不能复制旧电脑目录路径或自启命令；只能重新应用声明的设置。ZIP 只允许清单引用的文件，导入完成前必须验证大小和哈希，取消/失败不激活。迁移包包含配对口令，UI 必须明确告知。
