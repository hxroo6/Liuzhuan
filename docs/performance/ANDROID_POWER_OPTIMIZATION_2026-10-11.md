# Android v1.8.2 / M34 性能与功耗优化交付

日期：2026-10-11。审查基线为 Android v1.8.1，前置报告提交 `6c50a45`。本轮按用户确认实施，修改前备份位于 `backup/1011-1103-android-power-optimization/`。本轮仅修改 Android 与相关文档、回归脚本；电脑协议保持兼容，不需要替换电脑程序。

## 1. 功能与策略

连接页「自动同步」新增「均衡省电」，默认关闭。

- **即时同步（默认）**：保留此前验证过的后台 CPU 保活策略，修复重复建锁和生命周期释放。意外断开继续保持服务，只有认证失败或明确暂停才停止服务。
- **均衡省电（可选）**：空闲时释放 CPU / Wi-Fi 锁，实际发送或传输触发约 30 秒工作窗口；进行中的传输继续请求保活，CPU 锁另设 10 分钟保险期限，持续传输进度续期。系统休眠后到达可能变慢；这不是系统后台网络豁免，也不承诺锁屏秒达。
- 日常安装包仍为不可调试的 Distribution，同一现有正式签名，不开启混淆。Debug 仅用于开发编译。

## 2. 审查问题落实

| 审查编号 | 已实施 | 验证与边界 |
| --- | --- | --- |
| A01 | 保活锁由服务实例持有并复用，只释放自身持有的锁，切换策略先释放旧锁 | 编译与策略边界检查；实际 ROM 锁行为待实机 |
| A02 | 新增显式省电模式；默认即时策略保持 | 策略检查覆盖空闲、工作窗口、传输及暂停；不能宣称默认已取消长期 CPU 锁 |
| A03 | 持续退避计数，1/2/4/8/16/30 秒加有限抖动；握手超时；网络缺失暂停重试、恢复立即尝试 | 真实失败 socket、网络缺失及恢复回归；局域网监听不依赖外网验证 |
| A04 | 图片 Hook 在有限后台线程队列读取，512 KiB + 1 字节即停止超限读取，字节广播 | 巨大来源、边界与零字节读取检查；只旁路观察，原 App 复制不受干扰；超过上限仍需手动分享 |
| A05 | 按连接局域网外部电脑的实际用途声明 connectedDevice，补超时及销毁清理 | 最终 Manifest 检查；系统启动、ROM 限制需实机确认 |
| A06 | 广播采用 goAsync 和有限进程级工作；兼容旧 Base64，早期超限拒绝，来源/规则版本/内容短窗去重；失败图片缓存文件用于重试 | 自动隐私检查；原始复制时间在异步读取前记录，暂停/规则更改会阻止排队的旧事件实际发出 |
| A07 | MEDIUM 候选合并为单消费者、保留最后事件尾沿兜底，成功后取消对应兜底；HIGH 独立事件快照 | 隐私及事件回归；检测评分与事件配置不降低，不引入轮询；第三方真实选择行为待实机 |
| A08 | 进度先做 O(1) 节流判断，再查任务和重建列表；上传 UI 按 250 ms 发布 | 非最终进度不更换发布列表，最终进度立即发布 |
| A09 | 共用 OkHttp 连接池；文件传输最多 2 项活跃、16 项待运行；任务取消关闭 Call 与已打开来源流 | 真实阻塞 HTTP 取消、来源流取消、排队取消、槽位释放、重试等待旧 I/O 清理检查 |
| A10 | 同 URL 缩略图请求合并、取消底层 Call、排队后二次检查缓存、短暂失败缓存；内存压力清理 | 编译、Lint 和代码检查；实际快速滚动帧率及请求量待实机 |
| A11 | LanController 进程级持有连接、同步回调、网络监听，页面重建仅订阅状态 | 编译与客户端回归；活动传输的必要 UI 进度回调只保留到任务结束 |
| A12 | 隐藏面板不订阅素材/日志/诊断历史；过滤先于截取；素材增量 TreeSet 有序索引 | 28 项真实仓库同步回归；发布列表仍需 O(n) 复制，保持完整镜像，不截断素材库 |
| A13 | DataStore 委托移至顶层，SettingsStore 使用 applicationContext | 编译与调用链检查，保留原设置文件及默认值 |
| A14 | 高频诊断日志仅在连接页展开日志且 Activity 前台时打开；稳定诊断去重、摘要编码减分配、确认后取消超时任务 | 回执与隐私回归；关键故障与发送诊断仍保留 |

`connectedDevice` 适用于通过网络连接外部设备；本项目声明 `FOREGROUND_SERVICE_CONNECTED_DEVICE` 并满足 `CHANGE_NETWORK_STATE` 的 Manifest 前提。不是通过改类型规避无关用途的预算。[Android 官方服务类型说明](https://developer.android.com/develop/background-work/services/fgs/service-types#connected-device)。原 `dataSync` 的后台时间预算与目标 SDK 条件见 [Android 官方超时说明](https://developer.android.com/develop/background-work/services/fgs/timeout)。

## 3. 回归证据

总计 **198 项本地检查通过**，使用生产类与隔离测试环境，不以实现镜像替代传输取消及失败网络验证。

| 检查 | 结果 | 证据 |
| --- | --- | --- |
| 功耗策略、硬读取上限、退避、去重、进度、缓存及真实 HTTP 取消 | 30 项通过 | `scripts/AndroidPowerChecks/Run-AndroidPowerChecks.ps1`，日志 `F:/appdata/Temp/liuzhuan-m34-power-checks/` |
| 真实 MaterialRepository 快照、序号、增量与同秒撤回排序 | 28 项通过 | 同上 `sync-checks.log` |
| 自动复制隐私与 5 类损坏配置失效关闭 | 57 + 25 项通过 | `scripts/AndroidPrivacyChecks/Run-AndroidPrivacyChecks.ps1`，日志 `F:/appdata/Temp/liuzhuan-m34-privacy-checks/` |
| 自动回执、实际 HTTP 隐私边界、缓存重试、真实失败 socket 与恢复 | 44 项通过 | `scripts/AutoReceiptChecks/Run-AutoReceiptChecks.ps1`，日志 `F:/appdata/Temp/liuzhuan-m34-receipt-checks/android-checks.log` |
| 真实电脑 WsHub 登记回执与协议兼容 | 14 项通过 | 同上 `websocket-checks.log`，仅隔离检查，未替换电脑交付程序 |

保留的关键约束：后台发送作用域为进程级 SupervisorJob；Hook / Receiver action、模块入口与 intent-filter 不变；没有添加剪贴板轮询、Shizuku、输入法或 root 读取方案；本地回写仍标记防循环；Coordinator 初始化仍幂等；排除/暂停隐私规则会在网络实际入队时重新检查。

### 构建与安装包

- 主源码同步至 `F:/LiuzhuanApp`，52 个源文件逐项 SHA-256 一致，应用 Gradle 配置一致。
- JDK 21 下 `assembleDebug assembleDistribution lintDebug` 成功。Lint 为 0 错误、18 警告、2 信息；已有默认区域格式、私有 Xposed 反射、即时模式无超时 CPU 锁等警告仍保留。当前 D8 对 API 36 的兼容警告也仍存在，本轮未扩展修改工具链。
- 交付 APK：`AndroidApp/Liuzhuan-M34-signed.apk`。
- 生成时间：**2026-10-11 11:37:58（Asia/Shanghai）**；大小：20,859,206 字节。
- SHA-256：`E42564206AB3360140241C38A6F34BEFE5D7DC4A89B75C96076FB6DF0208561C`。
- 安装包与编译输出逐字节摘要一致。不可调试、包名、versionName 1.8.2 / versionCode 7、minSdk / targetSdk 36、ClipReceiver action、Xposed 入口和最终 connectedDevice 类型已检查。APK v2 签名验证成功，与 M33 签名证书一致；未改签名配置。
- aapt 不支持本机中文工程路径，最终 Manifest 检查在 ASCII 编译输出路径完成，再对交付副本核验摘要。原 M33 安装包保留可回退。

## 4. 安装与实机验收边界

覆盖安装后，到无障碍设置关闭→重新开启流转服务。重启 LSPosed 作用域内的来源 App，使新版 Hook 被重新载入；保留既有作用域配置。

本轮 `adb devices -l` 无连接设备，**未安装到手机、未测实际耗电、深睡时长、帧率或锁屏到达延迟**。建议实机用同一手机、同一局域网分别比较两种模式的前台/后台复制、锁屏 30 分钟后的复制、断网后恢复、页面切换及大文件取消。均衡模式不满足到达时效时可关闭开关回到即时策略。

手动 URI 的重试仍依赖来源授权与文件存在；缓存重试记录仅限本次进程，重启会清理旧进程的临时缓存。网络取消不能撤销已经在电脑登记的文件，重试前检查对端是否已收到。大型快照仍完整发布，未来超大素材库的分页须与电脑协议成对设计。

本轮仅本地提交，不执行 GitHub 推送或 Releases 发布；公开下载链接仍指向 v1.8.1。
