# KNOWN_ISSUES — 已知问题、技术债与注意事项

> 记录当前仍存在的问题。修复后请直接从本文移除对应条目（不要保留"已修复"历史，历史在 git log 与 logs/）。
> 编号规则：`LIMIT-`(平台限制) / `DEF-`(缺陷) / `COMP-`(兼容性) / `DEBT-`(技术债) / `OPS-`(运维易踩坑)。
> 标注【待确认】= 无法仅凭源码核实。

---

## 一、平台限制（代码无法绕过）

### LIMIT-1 微信等不暴露 selection 的 App 后台复制无法自动捕获
- **复现**：流转退后台 → 微信复制 → PC 收不到；切回前台 → 最后一条经 ON_RESUME 前台重读补发。
- **根因**：Android 10+ 剪贴板焦点限制（后台 `getPrimaryClip()` 返回 null）＋ 微信自定义 View 不向无障碍暴露 textSelectionStart/End（selection 捕获返回 null），两条路都断。
- **替代路径**：文本选择菜单（选中 → 系统菜单「流转」）——【待确认】微信的文本选择菜单是否实际显示「流转」入口（用户待测）；若 adb 可连，拉 logcat 确认 selection 失败的具体环节。
- **排错警示**：后台复制失败 ≠ 代码 bug。先确认 `[DETECT] candidate level=MEDIUM/HIGH` 后有无 `[CAPTURE] success`：有 DETECT 无 CAPTURE=App 不暴露/焦点限制；连 DETECT 都没有才是代码问题。

## 二、缺陷（源码核实，尚未修复）

### DEF-1 PC 端 Undo 不同步到手机
- `DataStore.Undo()` 把删除的素材恢复进列表，但既不递增 `_sequence` 也不触发 `ItemAdded` 事件 → 手机接收页不会恢复该条目，直到下次重连拉快照。
- **影响**：PC 上 Ctrl+Z 撤回删除后两端视图不一致。
- **位置**：`Liuzhuan/Services/DataStore.cs` `Undo()`。
- 附带同类观察：收藏状态变化（SetFavorite）也不广播，属设计内可接受；Undo 属真实盲区。

### DEF-2 连接态发送失败会丢事件（ack 未被利用）
- `PushToPcAction.execute` 仅在 `state != Connected` 时入队；状态机判定 Connected 但底层发送失败（`pushClipboard` 返回 false，或 OkHttp 缓冲后静默失败）时事件直接丢失。服务端对 sync_text/clipboard_push 有回 ack，但 Android 收到 ack 后不做送达校验。
- **影响**：极端情况下静默丢一条剪贴板事件。
- **候选修法**：send 失败也 enqueue 重试；或按 diagnosticId 校验 ack。

### DEF-3 UDP 发现端口隐式耦合，改 PC 端口即静默失效
- PC：`UdpDiscovery.Start(wsPort)` 内部 `port = wsPort + 2`；Android：`LanDiscovery.DISCOVER_PORT = 8901` 硬编码。用户修改 `LanConfig.Port ≠ 8899` 后，搜索功能两端都不报错但永远搜不到。
- **候选修法**：双端固定写死 8901，或在 OFFER 应答里带回发现端口并在 Android 提示端口错位。

### DEF-4 Coordinator 状态机是半套死枚举
- `ClipboardMonitorCoordinator.State.STARTING / DEGRADED / ERROR` 从未被赋值（只有 RUNNING/STOPPED 在用）。误导后来者以为有降级检测。
- **处理建议**：要么接入真实信号（如 DEGRADED=无障碍在跑但 WS 断开），要么收敛枚举。

### DEF-5 直连发送路径不受 Dispatcher 过滤约束
- `ProcessTextActivity` 直调 `pushClipboard`、分享菜单直调 `sendText`，绕过 Dispatcher 的 autoSend 开关过滤与 Deduplicator。属用户显式动作，语义上可辩护，但与 D10 决策口径存在偏差；改造时并入统一管线，勿再新增第三条直连路径。

## 三、兼容性问题

### COMP-1 minSdk=36 只支持 Android 16+
Android 11–15 无法安装。降级为独立专项（见 DECISIONS D02）。

### COMP-2 覆盖安装 APK 后无障碍服务可能被系统禁用
每次交付需提醒用户到无障碍设置「关闭→重新开启」。App 内已有三态检测（`MainActivity.accessibilityState`：开关字符串/进程内 isRunning/框架绑定列表）区分「真没开」与「残留误报」，勿退回只读 Settings 字符串的方式。

### COMP-3 一加 Ace 3 Pro adb 连接困难
Windows 设备管理器完全看不到设备 = 线材（仅充电线）或端口问题，非软件问题；排查前先换数据线+直连+改 MTP。【待确认】ColorOS 对后台 Toast/通知吞没、开发者选项「禁止权限监控」等具体行为。

## 四、技术债

| 编号 | 内容 | 影响 | 说明 |
|---|---|---|---|
| DEBT-1 | PC `DataStore._sequence` 为内存计数器，重启归零 | 目前靠「重启→断线→重连→replaceAll 快照重置 lastSequence」自然兜底，可用但脆弱；序列号持久化才能根治 gap 检测跨重启语义 | `DataStore.cs` |
| DEBT-2 | `CopyEventDetector.Candidate.preferSelection` 字段只生产不消费 | 无功能影响，纯遗留 | grep 全库仅在 Detector 内出现 |
| DEBT-3 | 心跳双层冗余：OkHttp pingInterval 30s + 应用层 heartbeat 每 30s | 无功能影响 | 若去重保留一层即可 |
| DEBT-4 | WakeLock/WifiLock acquire() 无超时 | 电量代价 + lint 告警 | 保活设计使然，改动需评估断连风险 |
| DEBT-5 | 两端工程内零单元测试 | 回归靠真机手工 A-B 对照 | 纯函数区（Deduplicator 时间窗 / Proto 编解码对称性 / Detector 评分矩阵）成本最低 |
| DEBT-6 | `LanDiscovery` 每次 discover 新建 DatagramSocket 且单次 socket 全程 150ms soTimeout 忙轮询收包 | 搜索体验略糙，功能正常 | 低优先级 |

## 五、运维与构建注意事项

### OPS-1 构建环境
- 工程路径含中文无法编译 Android → 必须用副本 `F:/LiuzhuanApp`，改完同步后再 assembleDebug（完整命令见 AGENTS.md §二/§三）。
- F 盘偶发文件锁（安全软件瞬时占用）：编译失败报「拒绝访问」时 `rm -rf app/build` 重试即可；PC publish 前停 Liuzhuan.exe，发布后杀掉残留大内存 dotnet.exe（否则下次 build 报 MSB4018 / project.assets.json denied）。
- D8 报「API level 36 not supported」为无害警告，不影响产物。

### OPS-2 部署要点
- `data/lan.json` 键为 `{Enabled, Port, Password}`，**Enabled 默认 false**（代码默认值）：全新环境部署 LAN 服务不启动且无明显报错，需先启用并配置口令。启动时的 TCP 自连自检 `LanServer.IsListening` 可用于判断 WS 是否真的在监听。
- 运行数据全部在工作区根 `data/`（非 `app/data/`），由 `ConfigService.GetEffectiveDataDir()` 解析 `exe/../data`。

### OPS-3 排查方法论速查
- **日志 tag 地图**（logcat 过滤用）：`ClipMonitor`(ACC 服务入口)、`ClipCapture`(DETECT/CAPTURE)、`ClipDispatch`(DISPATCH)、`ClipPipeline`、`PushToPc`(ACTION/WS/OUT_QUEUE)、`ClipCoord`(flush/VIS/装配)、`LanClient`(WS 状态/SYNC)。诊断不依赖 logcat 时看 UI 连接页「链路诊断/最近事件」（Coordinator 诊断 StateFlow，保留最近 6 条）。
- **A-B 对照法**：后台连续复制多条后切前台——全部发出 = 断线积压/队列问题（查 WS 连通性）；只发最后一条 = 平台焦点限制（属 LIMIT-1 范畴，不是 bug）。注意区分「前台重读剪贴板」（FG-fore 日志）与「队列 flush」（OUT_QUEUE flush 日志），两者现象相似、含义不同。
- 协议联调可不开真机：Python 模拟客户端直连 PC（scripts/ 下有现成脚本）。
