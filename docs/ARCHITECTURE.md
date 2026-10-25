# ARCHITECTURE — 当前真实架构

> 当前结构更新：M57 / v1.20.0 本地开发版（2026-10-25）；M13及后续分节保留各阶段背景，当前行为以最新分节和源码为准。
> 维护约定：改动下述任一链路的结构（新增/删除组件、改协议、改端口）时**必须同步更新本文**。
> 冲突裁决顺序：源码 > 本文 > `logs/项目迁移交接文档_20260827.md` > 其他历史资料。

---

## 1. 总览

### M57 / v1.20.0：恢复边界、离线原件、传输调度与音频配置保护

- PC现为WPF/.NET 10，`global.json`固定SDK基线；Android使用AGP 8.10.1、Gradle 8.11.1及JDK 21，API 36与既有签名不变。升级不改变剪贴板Hook、进程级发送作用域、FGS停启或录音许可边界。
- `MaterialRecord`先作为无文件访问副作用的DTO完整校验，之后才构造索引对象。`DataStore.DataRoot`在实例构造时固定；启动先发布索引，派生来源检查批次异步执行。`QueryRevision`仅随影响查询的索引/整理/来源变更推进，缩略图与来源状态通知不重复重建分类查询。大库使用不可变快照在后台查询；网络缩略图受长边、并发和缓存预算约束。
- `LibraryRecoveryService`保留旧索引历史：最多每5分钟尝试一次、10份/128 MiB总量/64 MiB单份。`LibraryHealthWindow`提供库位置、历史差异及启动计划；切库或索引恢复在下次启动、创建DataStore之前执行。恢复先保留现索引并复核历史指纹，只恢复条目/整理，不搬动原件、设置、身份或回执；与迁移计划冲突时拒绝执行，不热切当前根目录。
- `ProjectStagingService`显式扫描`project-imports`，区别已引用、完整未引用、不完整、阻塞和回收区。索引/Undo/续传保护、坏绑定或链接检查先于回收；每批最多200个原件，先落盘意图再同卷移入独立回收区，恢复不覆盖。准备和合入共用`.prepare.lock`租约，不递归删除目录，也不自动清理。
- 手机项目包能力`project_package_v1`沿用已鉴权素材连接，`project_begin`提交export/preview/apply，`project_status`查询结果。电脑后台串行作业、最多4项/15分钟、按电脑/库/操作指纹绑定；导出完成登记ZIP后走原可靠下载，预览返回有界令牌，合入重新校验并Flush索引后才确认。进度只在手机面板可见且生命周期活跃时查询，关闭面板不代表撤销已提交的电脑作业；此作业状态未做跨进程恢复。
- Android完整离线索引使用`serverRun`和有界变更日志做显式增量更新；跨服务运行、游标过旧、序号不一致或能力缺失退回完整保存。下载准备不长期占本地写锁，最终提交复核库/epoch/代次。`LibraryWorkspace`草稿只叠加真正编辑的字段，重新编辑仍保留原基线、反向编辑与未编辑的电脑字段。
- `OfflineOriginals`按电脑安装ID/库/素材/来源版本保存用户主动固定的原件，复用可靠下载并验证SHA-256；私有`noBackup`副本为明文文件，目录记录沿用加密恢复存储。最多200项，可选128/512 MiB、1/2 GiB全机预算，预留及孤立副本也计入。只读Provider仅为选中的原件临时授权，取消固定不删Downloads文件；服务器重启后旧副本明确视作历史快照，不冒充最新。
- 两端传输策略持久保存发送/接收优先、方向总限速及音频让行：最多2项实际传输，等待任务有老化保护；策略在实际数据块边界应用。音频转发时可将上下行各限制为1 MiB/s，既有分块校验、durable偏移、fsync与ACK边界不变。目录收集等待素材索引Flush之后才保存已收集指纹；手动待发即使保留待重试也受七天期限约束。
- `AudioConfigurationGuard`区分两类本机档案：收到当前非静音PCM且虚拟输出实际读取时，只自动保留已验证的虚拟端点选择；用户实际确认外放与听写正常后，才保存六种系统默认端点与外放静音/音量作为对照。坏记录、未知/静音/疑似错误配置不覆盖好档案。恢复只预检原ID并停止服务、保存流转设备选择，不恢复系统默认/音量、录音许可或自动跟随，不自动开麦。
- `AudioConfigurationMonitor`通过Core Audio设备/默认端点/音量事件合并检查，无空闲周期轮询、无试录音或试播放；原麦克风体检窗口可见时的10秒刷新独立保留。发现实体扬声器缺失、外放进虚拟线、静音或与基准不同时提醒并提供Windows入口，不静默修改系统设置。档案不随迁移导出，不含录音。

本阶段仅本地交付，未推送GitHub；隔离测试、构建与离屏布局检查不等同真机、声卡驱动恢复、实际外放/听写或功耗验收。详见[M57记录](performance/M57_RELIABILITY_OFFLINE_2026-10-24.md)。

### M56 / v1.19.0：项目包合入与手机常用搜索

- ProjectPackageService使用独立白名单DTO，项目ZIP不复用整库Migration的设置/MaterialItem反序列化。最多1000条、16MiB有界清单、64GiB原件；路径、成员、元数据和时间先校验，再构造本机材料。原件逐字节计数、SHA-256及ZIP CRC32检查；规范来源路径只打包一次，输出.partial强刷后公布。准备目录以包UUID绑定清单指纹，重试复验原ZIP及既有原件，不信mtime。
- project-imports目录不自动清理，预览只准备文件。DataStore专用Preview/ImportProjectBatch先完整验证root/sequence/范围，再单Reset加入新项，保持原库对象/顺序；内部project键规范为N格式，识别同包重试。同内联文字保留本机整理并追加内部键，跨包文件不自动按hash合并，不执行自动整理规则。窗口提交前重核原包及库代次，提交后禁取消，FlushAsync落盘后才报成功；失败保留原件和当前内存状态供重试。
- Android LibrarySavedSearches按电脑安装ID+库ID隔离，复用加密LibraryRecords/noBackup，最多10项命名条件，明确保存/更新/删除后才写盘；损坏保留且不影响素材库。普通清索引保留，显式移除整个库时清该库设置，不参与PC迁移或跨端同步。
- 分页actor改为collectLatest可替换的只读子job，旧await取消后释放请求锁；250ms尾沿输入合并，元数据同步actor/持久草稿仍独立进程作用域。外部应用查询时复核epoch与当前关键词，同文字输入不重建IME选区/组合态。详见[M56记录](performance/M56_PROJECT_PACK_SEARCH_2026-10-23.md)。

### M55 / v1.18.0：任务中心与只读重复素材检查

- 两端TransferTaskQuery只负责展示筛选，按名称/方向/状态搜索，四组互斥归类为进行中、待处理、成功、取消；已结束筛选含成功及取消，失败/暂停/过期/未知归待处理。PC独立ListCollectionView避免影响其他观察者；Android remember派生查询、横向筛选和可滚动工具，无新权限/协议/轮询。
- TransferJournal每ID只保留待显示的最新值，最多一个待处理Dispatcher更新；UI drain从Latest取值，更新和淘汰共用Gate。控制变更通知发布前核对原Latest对象及原Controller实例，EndControl后迟到通知不恢复控制。恢复控制仍存在的失败项不参与显示预算淘汰。
- Android TransferTaskStates统一成功/终态文案，历史恢复、可清理判断和查询共用；旧协议“已保存到手机”按成功恢复，持久描述和断点仍由原恢复引擎持有。
- DuplicateMaterialService显式启动，最多10万条索引、全进程一个扫描，规范路径去重、大小预筛后顺序SHA-256、100ms进度限频，末尾复核元信息及UI发布复核来源修订。只读预览，不改基线/文件/索引；逻辑重复字节不等于实际可释放空间。DataStore批量收藏以HashSet单次遍历替代逐ID扫描，保持实际变更的逐项修订与同步。详见[M55记录](performance/M55_TASK_CENTER_DUPLICATES_2026-10-23.md)。

### M54 / v1.17.0：并发准入、目标边界与异步整理

- 续传服务短锁维护持久任务索引与容量预留，合计最多1000任务；下载SHA256不持全局锁，仍持原任务Gate，提交前核对文件大小/mtime和素材来源绑定。失败/取消释放预留，校验与fsync/ACK不变。启动建立索引，后续请求按一分钟或容量压力核对；每轮过期检查最多128项，有游标避免坏记录阻塞后续候选。在途Gate优先，坏/null记录保留计数，Windows大小写共享Gate。
- DurableInbox按规范目录共享弱引用状态、容量索引和每请求Gate，文字WS/文件HTTP的两个实例不能重复登记同key或分别突破10000预算。慢登记不占目录Gate，可靠意图/回执仍分别原子强刷。正常登记只维护自身；一分钟/容量压力按业务事件核对，过期前核验合法记录、创建时间与元信息；坏/null/超预算记录保留，读取最多64KiB，无额外轮询任务。
- 手动文字捕获原目标和显式连接意图代次，异步提交不换绑；item_data携带原socket/身份票据，在UI动作执行时原子复核。已持久化待发允许原电脑重连。ACK的最终复核、回执落盘和出队与取消互斥，日志/任务状态只在确认应用后更新。
- MaterialLibrary本机草稿落盘后立即返回，进程级合并信号worker串行同步，网络await不占本地工作锁；syncing与索引保存workBusy分开。迟到saved ACK只推进可逐字段证明来自旧请求的基线，新token和真正远端冲突保留；最多4个revertedFields持久保留反向编辑，未知结果转显式核对。
- 分页/同步长期actor在边界区分自身取消与子请求超时：真正取消传播，内部超时显示错误并继续处理后续刷新/重试；没有空闲网络轮询。详见[M54记录](performance/M54_TRANSFER_SESSION_2026-10-22.md)。

### M53 / v1.16.0：审查修复、离线翻页与持久化预算

- 素材WS默认仅允许hello/heartbeat/bye匿名，其余功能要求当前socket已鉴权；get_item及异步快照回发再次复核。既有口令及一次扫码协议保持兼容。
- Hook发送用来源App的Application Context与平台shareIdentity选项；接收在读取复制内容前校验平台发送UID/包名及PM归属。固定token仅作协议标记，无法证明身份时跳过自动发送并显示诊断；保留exported receiver、action、旁路try-catch和前台/手动链路。
- DataStore在所有者线程捕获不可变持久化字段，单后台worker序列化并原子落盘；新pending覆盖旧pending，版本等待者只在覆盖自己的快照成功持久化后完成。失败拒绝ACK且可再次Flush，迟到旧快照不能覆盖新版本。顶层null索引走不可读取保护。
- 同路径重收集使用Add返回的实际索引对象，更新文字预览、推进SourceRevision并重建派生信息，保留手工整理。摘要新增可选sourceRevision，手机缩略图URL以来源版本和库运行epoch区分缓存。Shell/媒体预处理统一最多2并发，排队过期请求跳过。
- Android上传调度拒绝或排队前置取消保证一次准备结果回调；下载以目标+服务身份+素材ID原子预留。入队提示与持久登记终态分开；RecoveryHistory纯进度最多每3秒写入，状态转换即时，恢复描述/fsync/可靠回执语义不变。
- 旧FileHttpServer跟踪并关闭已接纳连接；默认16并发、请求头10秒/单行8KiB/总64KiB/100条限制，合法body不套用头部期限，停止后可重启。
- LibraryWorkspace单筛选缓存页内匹配位置（最多5万个Int约200KB），索引提交/ACK/草稿修改后失效；同条件翻页只读所需页。独立离线库管理可在旧电脑离线或第6库无法激活时释放无草稿旧缓存，拒绝删除待同步/不可读取记录；离线打开在IO后复核连接代次。
- 电脑选择统计使用HashSet且共享单次选择事件结果，保留视图顺序。CI加入DataStore、派生加载、整理与WS鉴权回归。实施与验证边界见[M53记录](performance/M53_AUDIT_OPTIMIZATION_2026-10-22.md)。


### M52 / v1.15.0：手机批量整理与离线工作区

- `LibraryWorkspace` 使用现有 AndroidKeyStore / RecoveryStorage，在noBackup目录分电脑安装ID+素材库ID存放加密分页索引及草稿。最近浏览摘要自动保存为“部分”；用户明确保存完整离线索引时，逐页写新代次，验证连续序号/去重/总数后原子提交manifest。最多5万项/64MB/5库，界面仍只保留1000行；不自动下载原件或缩略图，纯文字仅在完整保存且不超过16000字符时可离线复制。
- 离线查询逐页解密过滤，叠加本地草稿，不整库装入Repository。索引是保存时快照，手动刷新，不声称离线反映电脑实时状态。清除索引保留待同步草稿，移除离线库须先无草稿；旧加密记录不可读时停止离线写入并保留记录。
- `material_library_edit_v2`协商新功能；`merge_batch`最多100项、WS请求最多256Ki字符。手机先持久保存每项原基线/期望值/本地修订令牌，发送按10项切批，串行请求，断线不清草稿。服务端先验证全批字段，再按素材进行三方字段合并；冲突项返回当前状态，不影响同批可保存项。Flush索引后才回应，重复期望状态不产生重复修改；客户端先更新索引页再持久移除已确认草稿。
- 请求排队与真正发送时均核对当前会话、原电脑及库身份，不能因LanClient复用跨目标错投。重连原库自动提交无冲突草稿；冲突/电脑删除保留本地内容，显式核对后重新基准提交或放弃。批量收藏、分组、标签追加/移除/替换仅影响已选择字段，每次最多100项、待同步最多500项/库，不修改批量备注。
- 旧电脑保留原单项乐观编辑；未协商新功能不重放离线草稿。无新权限、后台轮询、额外保活锁或第三方依赖，音频与剪贴板链保持原有行为。详见[M52记录](performance/M52_OFFLINE_LIBRARY_2026-10-20.md)。

### M51 / v1.14.0：容量预览、保留策略与大库预算

- `StorageMaintenance`在后台扫描已知托管目录直接文件，UI线程捕获索引/撤回引用。区分索引、托管副本、缩略图、非托管来源、续传及回收区逻辑大小；默认30天/保护最新500条和全部收藏，没有自动清理。预览绑定库根、序号和来源元信息，扫描不完整时拒绝文件回收。
- 索引移除继续复用DataStore与Undo/既有LAN增量；文件回收仅针对未引用历史文件，保护HEIC原件、续传引用、最近一天创建文件和链接路径。每批最多200个，先Flush回收意图再同卷逐文件移动到cleanup-trash；恢复不覆盖，永久清空需先查看明细并确认，复核回收版本和文件元信息。永久清空/恢复在后台，非递归删除；根目录和UNC库不支持回收。
- `StorageRetention`作为迁移包可选参数，导入验证范围，不迁移trash、任务/回执或隐藏历史。Android `StoragePanel`只统计本机缓存/加密恢复占用、清缩略图内存和已结束展示历史，不删接收文件、任务来源或可靠回执。`RecoveryHistory`先持久加密隐藏ID，再保存展示历史；未变化的隐藏集不重复写盘，进行中任务不受隐藏集影响。
- DataStore复用排序缓存，批量移除单次集合Reset、批量Undo用邻居锚点链表合并；同筛选的大批视图变化使用一次Reset并恢复幸存选择。`MaterialLibraryService`串行查询，按库序号/数量缓存不可变摘要与筛选结果，同版本翻页不重复捕获全库。
- `ThumbnailBudget`两边最长320px/最多2个可见解码，离屏取消等待并丢弃过期结果，粘贴不在界面线程同步解码。手机保留8MB缩略图缓存/3并发，并用缓存代次阻止清空前请求回填旧缓存；`LibraryBudget`和Repository将分页/增量摘要限制为1000条滚动窗口，全库搜索仍在PC。
- 可复现脚本`StorageScaleChecks/Run-StorageScaleChecks.ps1`隔离运行1千/1万/5万条、96/144/192DPI导出、回收保护和128MiB续传中断+音频并发；真实设备与未验收矩阵见[M51记录](performance/M51_STORAGE_SCALE_2026-10-18.md)。

### M50 / v1.13.0：音频恢复、跟随兼容与节能连接

- `MicrophoneRecovery` / Android `MicIssue` 使用白名单故障分类。电脑原设备重试只解析原ID，端点不在时拒绝替换；锁屏/睡眠不自动恢复音频或手机录音许可。音频异常保留素材链。
- `FollowDemandPolicy` 增加Unknown/Calibrating/Compatible/ManualOnly。校准45秒超时转手动；不可信归属废止既有校准，开启时再核对最新观察，手动停止优先。兼容不代表ASR成功，完整实机听写验收见M50记录。
- 手机`microphone_options.energy_saving`只保存扫码行为偏好，不是开麦许可。明确切换节能时进程级串行保存完成后结束音频FGS，即使设置面板已关闭；后续扫码跳过音频邀请和权限请求，仅原素材WS运行。用户在前台明确开启可临时使用麦克风，结束后仍保留节能偏好；无后台自动冷启动。
- `VirtualAudioOutput`在首次有效PCM时创建/播放WASAPI输出，静音、手动停止或会话结束即Stop；再次有效PCM时Play，继续保留原200ms有界缓冲/60ms预热。回调计数用于实测，不记录音频。手机统计采集/发送帧、AudioRecord读取次数、采集时长和CPU锁状态，不据此宣称电池省电比例。

### M49 / v1.12.0：稳定身份、完整分页与整理同步

- `DeviceIdentity` 在安装默认数据根持久化电脑 UUID 与可改名称，独立于地址和每库 serverId；不进入迁移包。`UdpDiscovery` 增加有随机挑战和配对口令 HMAC 的 V2 应答，旧发现消息保持兼容。Android `IdentityProof` 核对证明后才跟随换址；welcome 再核对期望 ID，不把广播 ID 当信任。
- `ConnectionProfiles` v2 兼容读取 v1；成功鉴权后按设备 ID 归并，保留最新地址、五个历史地址和本机备注名。`SettingsStore` 保存已认证身份与库 ID，写入时复核当前目标；迟到旧连接不覆盖新配置。M48 发送描述/目录绑定仅在原库身份与旧凭据目标一致时升级。先完成绑定再恢复自动收集。
- `MaterialLibraryService` 通过已认证素材 WS 的 `library_request/library_result` 提供最多100条分页、全库筛选与乐观编辑，协商 `material_library_v1`。仅在素材所属线程捕获摘要和提交修改，过滤在后台进行；分页序号失效需重新获取。`LanItemSummary` 新增收藏、逐项版本、来源与缓存失效状态，不发送电脑路径或内部登记键。
- `DataStore.ItemUpdated` 持久递增 MetadataRevision，共用原连续序号广播 `item_updated`；收藏、组织和来源状态变化都可通知。元数据更新不移动列表顺序。手机编辑检查当前库和基准版本；冲突不修改，成功在索引 Flush 后响应。PC 视图刷新合并到背景调度。
- Android `MaterialLibrary` 只负责串行请求、分页代次和筛选状态；素材仍只存 `MaterialRepository`。搜索250ms合并、迟到页作废、事件触发重新加载已浏览范围，无空闲轮询。旧服务端沿用50条镜像。相同电脑的新运行代次会废止旧编辑界面；离线只保留本进程已加载摘要，不提供离线修改或完整原件下载。
- 本轮保持明文 LAN、口令鉴权及一次扫码，未接入外部服务、强认证证书或新增依赖。采用范围和验证边界见 [M49记录](performance/M49_IDENTITY_LIBRARY_2026-10-18.md)。

### M48 / v1.11.0：虚拟化、持久收发与登记回执

- `ResettableCollection` 合并分类切换通知，视图未变不触发集合重排；网格使用 MIT VirtualizingWrapPanel 2.5.4，三个列表均回收容器。MaterialItem 仅为已实现卡片申请后台缩略图，2并发/320像素；DataContext回收与离屏释放成对处理。
- `ResumableTransferServer` 复用现有 ASP.NET Core 运行时，在控制端口+4（默认8903）提供每次鉴权的成对分块协议；旧FileHttpServer不变。协商 `resumable_transfer_v1` 和 `durable_receipts_v1`，welcome含serverId/resumePort，hello含本机clientId，普通二维码仍扫一次。
- 上传：准备稳定私有副本 → 查服务端持久偏移 → 1MiB块/分块SHA-256 → 数据Flush后原子推进偏移 → 完整SHA-256 → 移入uploads → HEIC/素材登记 → 索引与成功回执落盘。下载：固定源指纹与大小/mtime → 块校验 → 私有部分文件落盘/描述推进 → 完整校验 → 固定接收目录MediaStore发布 → 手机保存回执。重复完成不重复登记或创建文件。
- `DurableInbox` 写前意图以稳定登记键关联DataStore素材；提交索引和回执后才发送durable stored ACK。DataStore在所有者线程捕获序列化快照，在后台串行原子保存，旧快照不覆盖新快照；损坏索引拒绝覆盖和可靠确认。
- Android `DurableTransfers` 描述持久化，仍由进程级 `TransferWork` 双并发执行。`RecoveryStorage` 在noBackupFilesDir中用AndroidKeyStore AES-GCM保存状态；正文不进入新诊断消息。`ClipboardRecovery` 持久FIFO/稳定请求号/收据，自动文字5分钟，手动文字7天；手动自动重试耗尽后仍保留显式重试与取消。
- PC `TransferJournal` 与 Android `RecoveryHistory` 保存界面历史，协议描述才是恢复权威。电脑控制通过owner绑定的 `transfer_state` 通知，持久控制修订抵御迟到命令，重连查一次状态补漏；手动暂停不自动解除。离线取消和断线都关闭正在进行的IO，任务在原电脑重新鉴权后恢复；不新增空闲轮询、唤醒锁或开麦恢复。
- UUID仅关联任务，不等于认证；同名的新客户端不会互踢。M49补上换网重发现与按身份归并历史；长期证书信任管理仍未完成。迁移不携带任务、回执、UUID或登记关联键；新手机/新电脑需重新配对。
- 版本边界、容量/过期、额外空间及平台替身测试范围见 [M48记录](performance/M48_RECOVERY_2026-10-18.md)；参考采用范围见 [开源参考](research/RECOVERY_REFERENCES_2026-10-18.md)。

### M47 / v1.10.0：整理、收集与竖屏扫码

- `AutomationStore` 是每个电脑素材库的规则表，独立 `automation.json` 原子保存。最多30条，按素材类型、收集来源、设备名称和素材名称匹配；只给新条目填空分组、追加标签，重复收集保留原手动整理。预览不修改素材，版本不支持/配置损坏时保留原文件并阻止覆盖；revision冲突要求重新加载。
- 素材WS增加可选能力 `automation_v1`，已鉴权socket才能发送 `automation_request`（action=get/preview/save，document为camelCase version/revision/rules）；`automation_result`以requestId单独回复，result含document、matched/samples或error。两端绑定当前socket，断开废止待答复；手机不持久化规则权限或跨目标草稿。64KiB请求上限、每会话300ms限频、10秒手机超时。规则执行与快照在素材UI线程串行，音频邀请仍独立处理。
- `LanItemSummary`/`Proto.ItemSummary` 增加可选 group/tags/note/hasFile，快照与新增广播都携带。Android展示分组、标签并按名称/分组/标签/备注搜索，仍是最近50条；旧服务端缺字段默认为空。单条手工整理尚无专用元数据增量，重连快照或再次新增时刷新。
- HTTP上传可携带可选 origin=directory、device；只作为整理来源显示，不是设备安全身份。保留原FileUploaded事件，新增FileCollected供主窗口登记，HEIC转换仍在最终登记前完成。M47实机检查同时修复文本文件被按空文字误去重：按HasFileSource区分文件与纯文字，文本预览最多读取5001字符，WS详情/HTTP下载返回完整源文件，Android通过hasFile选择保存动作。
- PC `FolderCollectionService` 使用明确选定目录的FileSystemWatcher，非递归、首次指纹基线、稳定检查、忽略临时/链接文件，分批最多32个；无空闲轮询。每机独立 `collection-folders.json`，最多10目录×5000文件。拒绝磁盘根、程序/素材库及其上级、网络路径和重解析路径；停止只移除订阅。离线目录和通知遗漏可手动检查。
- Android `FolderSubscriptions` 在LanController.init后初始化，只持久化系统SAF授权的来源树、文件指纹和接收电脑目标散列；本机 `AtomicFile`，不是接收目录选择。连接期间保留最多10个授权目录的通知cursor，断开/停止即关闭，并订阅提供者通知，前台/重连/显式按钮补查，1.2秒稳定对照后复用进程级TransferWork，最多2个真实传输并发。分批最多16个，更多文件另批处理。入队前记录尝试，取消/失败不自动重复；可在收发显式重试，不提供跨进程任务恢复。连接目标变化/停止订阅在排队、发送前和流式发送中校验。
- Windows opt-in Ctrl+Alt+V 收集现有剪贴板，默认关闭；热键与原Ctrl+反引号使用不同ID并正确注销。Android顶部“收集”提供前台文字预览/确认发送、文件选择及既有系统分享入口；不注册Windows右键菜单、不增加剪贴板轮询。
- 规则随整库迁移；订阅本地路径/SAF URI、目标散列、临时任务与快捷键偏好不迁移。手机接收路径始终为Download/Liuzhuan。
- `PairingScanActivity` 替代ZXing默认横屏CaptureActivity，仅该相机页声明portrait，沿用ScanContract原配对结果；Compose统一深墨绿卡片、真实解码框角标、返回/手电筒/权限重试。取景框位置来自实际相机解码区，不用假扫描进度；暂停关闭灯与相机。Android16大屏系统可能忽略方向约束，需另做平板适配验收。

### 手机麦克风实验链（M39–M46 / PC v1.9.1 本地构建）

- M46 `AudioRouteScanner` 在后台只读枚举当前/禁用/断开的端点及 Console、Multimedia、Communications 三种默认播放/录音，不激活音频流；`AudioRouteHealth` 独立判定三路配置，区分读取失败、未选择与保存设备失效。设置页可见时每10秒与手动体检共用最多一个扫描任务，关闭停止计时且忽略迟到结果；服务开启时仍可体检，不重新绑定当前选择或重启服务。三路卡片区分实体外放、流转写入和听写读取，修复按钮仅打开固定Windows声音/播放/录音面板。复制诊断只附受控问题代码与标题，排除设备名称/ID，不把配置结果当作端点连通、应用输入选择或ASR证据。手机协议与应用未改。

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

- **PC = Hub 服务器**：WPF (.NET 10)，Fleck WebSocket + 手写 TcpListener HTTP；可靠收发另复用ASP.NET Core。
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
- 回归命令：`dotnet run --project scripts/HeicConversionChecks -- <HEIC样本路径> ...`，验证设置持久化、格式识别、PNG/JPEG 全分辨率解码、关闭/普通图片透传及损坏文件保留。

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
