# 分类性能与可靠收发：开源参考和采用范围

核对日期：2026-10-18；实施构建：M48 / v1.11.0，尚未发布。

| 参考 | 可借鉴内容 | 本项目实际采用 |
|---|---|---|
| [VirtualizingWrapPanel](https://github.com/sbaeumlisberger/VirtualizingWrapPanel)、[使用文档](https://sbaeumlisberger.github.io/VirtualizingWrapPanel/docs/getting-started.html) | WPF 换行网格的虚拟化、容器回收、有界缓存 | **直接依赖 MIT 组件 2.5.4**；网格只创建视口中的卡片。完整许可证随程序放入 `licenses/VirtualizingWrapPanel.txt` |
| [tus 协议](https://tus.io/protocols/resumable-upload.html) | 服务端确认偏移、偏移冲突拒绝、分块校验、取消和过期 | 参考协议语义，使用自己的小型成对协议；**不是 tus 兼容端点** |
| [tusdotnet](https://github.com/tusdotnet/tusdotnet) | .NET 磁盘存储和完成事件边界，MIT | 研究参考，未引入包或复制源码；保留现有 WPF 素材登记与 HEIC 转换流程 |
| [tus-java-client](https://github.com/tus/tus-java-client)、[tus-android-client](https://github.com/tus/tus-android-client) | 先查远端偏移，再定位来源；跨进程保留任务关联，MIT | 研究参考，未引入包或复制源码；复用现有 OkHttp、双并发传输和任务界面 |
| [.NET eShop TransactionBehavior](https://github.com/dotnet/eShop/blob/main/src/Ordering.API/Application/Behaviors/TransactionBehavior.cs) | 业务提交与后续事件的顺序；失败后重放保持幂等 | 参考提交顺序；本项目用写前登记意图、稳定关联键、素材落盘和回执落盘，不引入消息队列或数据库服务 |
| [Android 持久任务官方说明](https://developer.android.com/develop/background-work/background-tasks/persistent) | 系统调度与应用生命周期的边界 | 本轮不额外引入 WorkManager、周期轮询或保活锁。记录持久化后，在应用启动/原电脑鉴权重连时恢复；不承诺被系统强制停止后自行运行 |

采用现成网格组件，避免自行维护 WPF 虚拟化算法。收发协议同时包含双向下载、素材转换、隐私撤销、原电脑绑定和登记回执，因此本轮保留成对协议及现有传输器；参考成熟的偏移/校验/提交做法，并增加实际 HTTP、WS 和跨进程故障检查。没有声称仅保存任务列表就实现了可靠恢复，也没有把参考项目的能力当作本项目已验收能力。
