# Codex 原听写 → 流转监听 → MicYou：链路规划

日期：2026-10-16。状态：仅调查与规划，未开发、未安装、未执行设备或音频测试。本文聚焦用户选定链路，优先复用开源，不增加自有 ASR、音频传输协议或虚拟音频驱动。

## 1. 可行性判断

**有条件可行。** Windows 提供录音会话通知，流转可以监听指定虚拟录音端点，再经 MicYou 插件控制手机音频转发。音频继续由 MicYou 与虚拟线缆输送，Codex 使用自己的听写识别。

但这不等于已经证明能够精确监听 Codex 按钮。真正可观察的是应用的录音会话是否 Active；如果当前 Codex 预先开启并持续保持录音流，或会话归属无法可靠确定，就无法由这个信号还原听写按钮。应先验证当前版本行为，再决定是否交付自动模式。

OpenAI 官方确认 [Dictate 将语音转为编辑器文字，再由用户检查和发送](https://learn.chatgpt.com/docs/prompting#use-voice-dictation)。本次查阅未找到第三方可订阅的听写按钮事件；方案不依赖修改 Codex、注入进程或按钮坐标识别。

## 2. 组件分工与两条链路

### 控制链

```mermaid
flowchart LR
    C[Codex 开始或停止读取虚拟麦克风] --> W[Windows 录音会话通知]
    W --> L[流转 PC：端点与进程匹配]
    L -->|仅本机命名管道| P[小型 MicYou Native 插件]
    P --> M[MicYou PC 官方宿主接口]
    M -->|既有控制连接| A[手机 MicYou：推流或静音]
```

### 音频链

```mermaid
flowchart LR
    A[手机 MicYou 采音] -->|既有局域网传输| M[MicYou PC]
    M --> I[CABLE Input：播放端]
    I --> V[虚拟音频线缆]
    V --> O[CABLE Output：录音端]
    O --> C[Codex 原听写与编辑器]
```

MicYou 负责手机连接、采音、传音频与处理；流转 PC 只新增监听、协调和状态提示。手机流转、素材 WS、DataStore、剪贴板捕获与回执不参与这条音频链。Codex 原听写路线无需 Handy/OpenWhispr 再次识别。

[VB-CABLE 官方定义](https://vb-audio.com/Cable/)是播放端 Input 转到录音端 Output。监听必须针对录音端，不能监听 MicYou 自己长期使用的播放端。首版只选一套专用虚拟线缆与一台 MicYou 手机，避免控制其他用途的声音。

## 3. 现成实现与真实缺口

| 组件 | 已有、可复用 | 本链路还需做什么 |
|---|---|---|
| [MicYou](https://github.com/MicYou-Dev/MicYou) | Android→LAN→Windows、虚拟线缆路由、静音同步、插件体系 | 新增外部流转到插件的本机桥接；真实停止采集为后续项 |
| [VoxMic](https://github.com/melody0709/vox_mic) | 指定录音端点的按需检测、每会话状态、防抖 | 借鉴监测设计；它没有现成 Codex 白名单，且自身以 ADB 音频链为主 |
| [NAudio v2.2.1](https://github.com/naudio/NAudio/tree/v2.2.1) | .NET Core Audio 会话 API | 流转新增独立观察器；保留少量原生互操作处理跨进程状态 |
| VB-CABLE | Windows 可用的虚拟录音端点 | 用户按官方方式安装和选择；它不是开源驱动，不自行重写 |
| Codex | 原有听写识别与编辑器 | 后续确认实际输入设备、共享模式和会话启停边界 |

MicYou 源码核实基线：**v2.1.1，commit `d821da032e2b72330ced09678dd127acd2714bef`**。[官方提交](https://github.com/MicYou-Dev/MicYou/commit/d821da032e2b72330ced09678dd127acd2714bef)。其他组件实施前再锁定版本，不能直接用变化中的 master/main 作为交付依据。

### MicYou 不能直接被 WPF 调用

现有 `set_muted` 是 MicYou 进程内 Native 插件的宿主回调，带宿主上下文，不是外部 WPF 可以直接 P/Invoke 的跨进程接口。Tauri 命令属于自身 WebView IPC；当前 CLI 没有 mute，名为 stop 的命令也没有真正跨进程停止服务。[插件加载](https://github.com/MicYou-Dev/MicYou/blob/d821da032e2b72330ced09678dd127acd2714bef/tauri-app/crates/micyou-plugin/src/native.rs)、[CLI 实现](https://github.com/MicYou-Dev/MicYou/blob/d821da032e2b72330ced09678dd127acd2714bef/tauri-app/crates/micyou-cli/src/commands.rs)。

因此“流转负责监听”的最小方案是**新增小型 Native bridge 插件与每用户命名管道**，插件在 MicYou 内调用宿主接口。不是重写 MicYou，也不是用两个现成程序同时运行就自动具备联动。若以后允许插件自身监听 Windows，可以省去流转监听与管道，但那是另一种职责分配。

## 4. 流转 PC 监听器设计（拟定）

1. 用户显式开启联动，选择虚拟录音端点，保存稳定 endpoint ID；不只按 CABLE Output 名称匹配，不自动改系统默认设备。
2. 独立非 UI 的 MTA 线程注册会话通知，枚举已存在会话并订阅各自状态；WPF 主线程仅显示结果。微软要求初始化 MTA，并调用会话枚举 GetCount 才开始接收通知。[注册要求](https://learn.microsoft.com/en-us/windows/win32/api/audiopolicy/nf-audiopolicy-iaudiosessionmanager2-registersessionnotification)。
3. 维护“允许的活动会话实例集合”，不使用设备的 DeviceState.Active 代替录音状态，不根据音量或用户沉默决定停麦。
4. 验证 Codex 主程序与合法辅助进程路径/进程家族，识别不明时降级手动；不笼统放行所有 Electron、Chrome 或系统宿主。
5. 第一个匹配会话 Active 后请求转发；最后一个离开 Active 后设置短宽限，期间重新 Active 就取消停止。初期可参考数百毫秒宽限，实际数值后续测定。
6. 回调只排队，管道通信与 UI 更新各自处理；注销与 COM 对象释放统一归属工作线程。如需漏事件校正，仅在联动开启且端点有效时限频核查已跟踪会话，不做高频全系统扫描。

Windows [GetProcessId](https://learn.microsoft.com/en-us/windows/win32/api/audiopolicy/nf-audiopolicy-iaudiosessioncontrol2-getprocessid) 对跨进程会话可能只返回最初创建者。NAudio 的包装会丢失成功 HRESULT 中的这项提示，严格归属判断需保留原生返回码。[NAudio 会话实现](https://github.com/naudio/NAudio/blob/v2.2.1/NAudio.Wasapi/CoreAudioApi/AudioSessionControl.cs)。

微软明确 [IAudioSessionControl 不适用于独占流](https://learn.microsoft.com/en-us/windows/win32/api/audiopolicy/nn-audiopolicy-iaudiosessioncontrol)。首版自动联动限定可可靠识别的共享模式会话；系统设置试听、其他软件、跨进程归属不明及独占模式不得被当成 Codex 按钮。

### 从 VoxMic 借鉴与避免照搬的部分

可参考 [`mic_usage_monitor.cpp`](https://github.com/melody0709/vox_mic/blob/main/src/mic_usage_monitor.cpp) 与 [`mic_session_state.h`](https://github.com/melody0709/vox_mic/blob/main/src/mic_session_state.h) 的 MTA 所有权、会话实例幂等跟踪、通知与状态校正、防抖。其 PID 只用于日志，判断的是端点上任意活动会话；端点缺失时回退默认设备、监控失败时保持传音的行为不适合作为本方案默认。流转应显示不可用并回退手动，不悄悄扩大监听对象或持续传音。

## 5. MicYou 桥接插件设计（拟定）

- Native bridge 插件只开放给本机当前 Windows 用户；命名管道限制用户 ACL、本机访问与消息大小。
- 管道提供版本协商、连接状态、幂等 setMuted、查询状态和释放控制权；每条请求包含编号、会话代次与期限。避免 toggle，防止重试导致反向切换。
- 自动模式需要用户事先批准。手动停止/撤销始终优先；不在普通 MicYou 使用期间偷偷接管静音状态。管道失联或控制租约过期时撤销自动转发请求，不能继续盲目解除静音。
- I/O 线程收命令后入队，由合适的宿主回调执行；不在实时音频处理或 COM 回调中阻塞。卸载先停止并等待线程，再释放宿主上下文。
- 基线 Host API=2、ABI=1，以固定版本头文件和常量为准，部分文字文档仍写 API1；实施时检查插件兼容性。[C ABI](https://github.com/MicYou-Dev/MicYou/blob/d821da032e2b72330ced09678dd127acd2714bef/tauri-app/crates/micyou-plugin/include/micyou_plugin_abi.h)、[插件文档](https://github.com/MicYou-Dev/MicYou/blob/d821da032e2b72330ced09678dd127acd2714bef/docs/plugins/api-reference.md)。

**配置前提：**手机已由用户开启 MicYou 会话并连接电脑；开启静音同步 muteSync；优先选 TCP 或 TCP+UDP，保留实际控制连接，不能假定 UDP-only 也接收静音控制。[静音发送](https://github.com/MicYou-Dev/MicYou/blob/d821da032e2b72330ced09678dd127acd2714bef/tauri-app/crates/micyou-core/src/settings.rs)、[手机接收与采集](https://github.com/MicYou-Dev/MicYou/blob/d821da032e2b72330ced09678dd127acd2714bef/composeApp/src/main/kotlin/com/lanrhyme/micyou/audio/AudioEngine.kt)。

现有 set_muted 不带手机 ID，改变该 MicYou 服务的静音状态并通知当前连接，不能当作逐手机控制 API。首版限定一个会话；连接断开、切换或身份不明时撤销自动控制，重新连接需用户再次确认。Codex 允许列表只控制触发条件，并不提供音频访问隔离：其他被 Windows 允许读取同一录音端点的应用仍可能收到声音。

### 确认状态不能混用

| 层次 | 实际能证明什么 | 界面限制 |
|---|---|---|
| 本机管道确认 | 插件收到请求 | 不能显示手机已经开麦 |
| 宿主 set_muted 成功且查询一致 | MicYou PC 静音状态已改变 | 不等于手机收到、采音成功或 Codex 收到音频 |
| 现有 streaming 状态 | 服务/连接存在 | 不足以证明本次最新音频有效 |
| 手机执行确认与新鲜音频进度 | 需要后续补充的端到端证据 | 有此证据后才能显示相应“已开始推流”，仍不表示文字识别成功 |

当前 MuteMessage 没有 requestId 或手机执行 ACK，宿主也不完整传播发送失败。[协议定义](https://github.com/MicYou-Dev/MicYou/blob/d821da032e2b72330ced09678dd127acd2714bef/tauri-app/crates/micyou-protocol/proto/network.proto)。如果最小插件不能提供足够的真实状态，自动模式先限实验用途；可靠日常交付需另评估小范围 ACK/状态扩展，不能假装零核心改动已经包含端到端确认。

## 6. 两档目标与交互边界

**第一档：按 Codex 使用状态自动转发。**尽量只改流转 PC 与插件，手机使用现成 MicYou。空闲时静音/停发，但 MicYou 仍可能持续 AudioRecord.read；不能称停止采集，也不能承诺消除麦克风功耗或指示灯。

**第二档：真正按需采集。**需要在 MicYou Android 的采集状态机和控制协议中增加停止/释放、重新启动与执行确认，成为独立适配任务。Android 16 的后台开麦受约束，可靠基线仍为用户在前台主动开启合法麦克风会话；不能承诺从被杀、重启或任意锁屏冷状态无提示启动。[Android 服务类型](https://developer.android.com/develop/background-work/services/fgs/service-types#microphone)、[后台限制](https://developer.android.com/develop/background-work/services/fgs/restrictions-bg-start)。

首次准备延迟和网络缓冲可能丢首字，停止时也可能丢尾字。Codex 已经开始/停止采集后，流转不能让它必然等待手机就绪；停止宽限也不能让 Codex 自动多录一段。若追求点击后马上说话，只能后续评估明确告知用户的限时预开流，接受相应功耗与隐私取舍。

流转状态建议：未配置 → 手机未准备 → 可联动（静音）→ 请求转发 → 桌面状态已应用 → 手机推流已确认（需证据）→ 停止/异常。证据不足时不显示更高层的成功状态。发生错误时提示用户取消本次 Codex 听写；流转不能宣称已替用户取消 Codex。

## 7. 后续阶段与通过门槛（本次均不执行）

| 阶段 | 后续工作 | 进入下一步的条件 | 不满足时 |
|---|---|---|---|
| P0 现成音频链 | MicYou、虚拟线缆、Codex 手动配合 | Codex 实际接受此录音输入、正常听写；手机停止后无旧声音 | 停在配置兼容性，不开发控制器 |
| P1 只观察 | 仅记录指定端点与允许进程的会话变化，不发命令 | 开始/完成/取消边界可识别，设置试听和其他应用不误触发 | 若 Codex 长期保持 Active，按钮自动联动不成立，改为显式手动控制 |
| P2 最小桥接 | 流转观察器 + 本机管道 + MicYou 插件 | 幂等、断线、手动接管、实际停发及状态反馈可靠 | 保留手动路线，不为小便利重写音频系统 |
| P3 完整联动 | 会话计数、防抖、睡眠唤醒、就绪与异常提示 | 当前 Codex/手机上端到端可靠，首尾字与耗电可接受 | 限实验模式，明确具体不支持条件 |
| P4 可选采集适配 | 手机真正停采/重启和执行 ACK | 证明采集器释放与恢复、锁屏/来电/撤权安全退回 | 保留第一档，不宣传零功耗按需采音 |

重点场景：同一设备多个 Codex 窗口/听写与语音共存、合法辅助进程变化、录音预热、系统设置试听、手动 MicYou 静音、其他手机连接、线缆重装、权限撤回、手机被杀、PC 睡眠恢复、普通 Wi-Fi/热点和大文件并发。全部仍待后续授权实施时验证。

## 8. 开源复用、交付范围

- NAudio 为 MIT，可复用 .NET 包装；VoxMic 主仓为 BSD-3-Clause，复用时保留声明，第三方依赖单独检查。
- MicYou 为 GPLv3 + Plugin Exception，插件例外有专门条件；不把其核心抽进流转后仅按 MIT 发布。先按独立程序、独立插件、公开接口设计，后续分发/商用前核对具体许可。[固定版本许可证](https://github.com/MicYou-Dev/MicYou/blob/d821da032e2b72330ced09678dd127acd2714bef/LICENSE)。
- VB-CABLE 是外部驱动，依其[官方使用与分发条件](https://vb-audio.com/Services/licensing.htm)，首版规划用户从官方安装，不开发驱动或改变系统默认音频设置。
- 最小研发范围只包括流转 PC 的监听器、MicYou 控制适配器、状态/配置 UI，以及独立插件；不新建 ASR、手机流转采音模块、音频数据面或 Codex 集成插件。

本次只新建此规划文档。没有开发上述模块、安装依赖/驱动、进行录音或设备验证、改版本、生成安装包、提交 Git、推送 GitHub 或更新 Releases。
