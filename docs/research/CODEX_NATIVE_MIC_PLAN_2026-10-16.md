# 流转自有手机麦克风 → Codex 原听写：无 MicYou 方案

日期：2026-10-16。状态：可行性与开发规划，未开发。用户最新方向为参考开源，由两端流转实现，不依赖 MicYou 应用。本方案作为当前方向；此前 MicYou 插件方案仅保留作对照。

后续约束已细化：MicYou、AndroidMic、VoxMic 仅分析优秀设计，不直接复制或整包引入。以[独立开发详细设计](CODEX_NATIVE_MIC_DETAILED_SPEC_2026-10-16.md)为当前完整规格；下文“复用代码”的早期表述不再作为直接搬用第三方业务实现的计划。

## 1. 结论与边界

**可以实现，不需要安装 MicYou，也不需要自己实现语音识别。**手机流转采音，电脑流转接收并送入系统虚拟录音设备，Codex 继续使用自己的原听写。

但“不依赖 MicYou 应用”与“不安装任何第三方虚拟音频驱动”是两个目标。建议第一版使用成熟虚拟线缆；如还要求取消该驱动依赖，就需要自研、签名和维护 Windows 音频驱动，独立立项。NAudio 是音频库，不会凭空创建系统麦克风。

链路可行不等于已验证当前 Codex 的按钮行为。流转能观察录音会话，不能据此保证获得按钮事件；当前版本是否预开流、辅助进程归属及首尾字延迟仍需后续验收，沿用 [Windows 监听方案的边界](CODEX_MICYOU_LINK_PLAN_2026-10-16.md#4-流转-pc-监听器设计拟定)。

## 2. 自有链路

```mermaid
flowchart LR
    C[Codex 读取虚拟麦克风] --> W[流转 PC：Windows 录音会话监听]
    W -->|开始/停止请求| S[手机流转：授权与采集控制]
    S --> A[AudioRecord]
    A -->|独立局域网实时音频| P[流转 PC：缓冲与重采样]
    P --> I[虚拟线缆播放端]
    I --> O[虚拟线缆录音端]
    O --> D[Codex 原听写识别]
```

- 手机端：原生 AudioRecord、录音权限、独立 microphone 前台服务、会话状态、明确的静音/停止通知。麦克风功能自身不要求 Root/LSPosed。
- 电脑端：指定录音端点的会话观察器、音频接收与有界缓冲、必要的重采样、虚拟线缆输出、配置和状态提示。
- 控制复用现有连接增加能力协商；音频使用独立通道，避免长音频帧挤占剪贴板回执、素材快照与大文件任务。
- 只向明确允许的电脑传音频，不进入素材库、不广播给其他手机、不默认落盘录音。

使用 VB-CABLE 时，电脑流转写入 CABLE Input（播放端），Codex 读取 CABLE Output（录音端）。[官方方向定义](https://vb-audio.com/Cable/)。手机和电脑只需流转业务客户端；虚拟音频驱动仍需单独按官方方式安装。

## 3. 优先参考与复用的代码

| 来源 | 可复用内容 | 使用边界 |
|---|---|---|
| [VoxMic](https://github.com/melody0709/vox_mic) | Windows 录音会话观察、音频缓冲/输出、Android AudioRecord 生命周期的实现参考 | 主仓 [BSD-3-Clause](https://raw.githubusercontent.com/melody0709/vox_mic/main/LICENSE)，保留版权和声明；第三方依赖单独审查。其 ADB 传输不能直接当作我们的普通 LAN 通道 |
| [NAudio 2.2.1](https://github.com/naudio/NAudio/tree/v2.2.1) | .NET 音频输出、缓冲、重采样及 Core Audio 会话访问 | MIT；适配现有 .NET 7，先固定兼容的 2.x。不要直接引入要求 .NET 9 的最新 3.x |
| Android 官方 AudioRecord / microphone FGS | 手机采音与合法后台会话 | 平台能力，无需重造录音引擎；目标为现有 Android 16 |
| MicYou / AndroidMic | 网络音频流程、用户交互、缓冲与降噪设计参考 | GPL 等许可需要分别遵守；并非不能复用，但不能复制核心代码后仅按流转现有 MIT 发布 |

Windows 监测可优先参考 VoxMic 的 [`mic_usage_monitor.cpp`](https://github.com/melody0709/vox_mic/blob/main/src/mic_usage_monitor.cpp) 与会话状态跟踪；需要自行增加 Codex 允许列表和可靠降级，不照搬监控失效就一直推流的策略。NAudio 最新框架要求见[官方说明](https://github.com/naudio/NAudio)。

不需要复用 MicYou 插件 API、宿主 ABI或跨进程管道。本方案的控制和音频模块均归流转维护，故比“外部 MicYou 插件适配”工作量更大，但可自行设计执行回执和就绪状态。

## 4. 模块与协议边界（拟定，尚未创建）

Android 建议新增 `audio/AudioCaptureController`、`MicrophoneService`、`AudioSessionState`、`AudioTransport`；PC 建议新增 `Services/Audio/AudioSessionCoordinator`、`CaptureSessionObserver`、`AudioReceiver`、`AudioJitterBuffer`、`VirtualAudioOutput`。

必须保留：进程级发送作用域、Context 初始化时机、原 connectedDevice 服务停启规则、剪贴板 Hook、捕获评分、幂等初始化和防循环。麦克风前台服务独立承担新职责，不把新权限塞进剪贴板 Hook。

现有 hello/welcome 成对增加音频能力；不支持音频的旧端仍能使用原文字/文件功能。控制消息至少区分：

1. 请求：目标电脑、会话编号、请求编号、格式、许可有效期。
2. 准备状态：手机是否已授权、是否具有合法会话、当前是否被其他录音任务占用。
3. 开始确认：AudioRecord 确实开始，关联当前流编号；电脑再区分收到新音频与写入虚拟播放端。
4. 停止确认：区分停止转发与采集资源已经释放；不能只因收到 stop 就报告麦克风已关闭。
5. 异常：权限不足、后台启动不允许、来电/音频占用、设备断开、会话过期。

实时音频帧有独立流编号、序号、单调时间和采样信息；有界缓冲，断线不回放旧录音。首版可用语音用途 PCM，后续再评估 Opus/降噪；格式与虚拟设备协商并重采样，不承诺固定低延迟。

音频连接需鉴权并绑定手机许可和当前控制会话，采用成熟加密传输方案；不能把现有明文 LAN 控制直接标成加密，也不能把知道现有配对口令等价于已经获得远程开麦许可。具体加密与配对扩展在后续开发设计中成对落实。

## 5. 分期：先稳定收音，再自动联动

| 阶段 | 范围 | 进入下一步的门槛 |
|---|---|---|
| A 手动手机麦克风 | 手机前台授权并开始；局域网传音；电脑写入成熟虚拟线缆 | Codex 原听写接受此输入，声音正常；停止与断线无旧声音、无资源泄漏；原复制/同步链路不回归 |
| B 跟随 Codex 自动转发 | 指定录音端点、Codex/合法辅助进程识别、防抖、手机执行回执、就绪与异常提示 | 当前 Codex 会话边界可识别；系统设置试听/其他应用不误触发；不能识别时回退显式快捷键 |
| C 真正按需采集 | 空闲释放 AudioRecord 与编码/网络任务，合法会话内重新启动 | 证明后台资格、锁屏/来电/撤权处理、真实释放与恢复、首尾字及功耗可接受 |

Android 官方支持的是符合资格的 [microphone 前台服务](https://developer.android.com/develop/background-work/services/fgs/service-types#microphone)，涉及 RECORD_AUDIO、FOREGROUND_SERVICE_MICROPHONE 及用户可见状态。不能承诺应用被杀/手机重启/任意后台状态时，电脑单个命令就能无提示冷启动录音。

阶段 B 如果仍持续采音，仅停止发送，就必须显示“手机会话采音中、当前未转发”；不称为零功耗或已释放麦克风。真正停止采集后再远程恢复是否满足目标 ROM 的系统规则，是阶段 C 的独立门槛。

在 Codex 已开始读取以后才启动手机，准备与网络延迟可能丢首字；Codex 停止读取以后，流转也不能强迫其继续等待尾包。先给真实就绪提示和手动回退，限时预开流只作为用户明确选择的体验取舍。

## 6. 当前决定

推荐方向：**两端流转自有采音/传输/控制 + 兼容的 NAudio 2.x + 成熟虚拟线缆 + Codex 原听写**。优先借鉴宽松许可证的实现，保持不依赖 MicYou 应用；自有驱动不作为首版前提。

本次只新增规划文档；没有修改应用代码、版本或现有协议，没有安装依赖/驱动、构建、录音/设备测试、Git 提交/推送或 Releases 操作。具体工期在阶段 A 的兼容性与阶段 B 的会话识别门槛确认后确定。
