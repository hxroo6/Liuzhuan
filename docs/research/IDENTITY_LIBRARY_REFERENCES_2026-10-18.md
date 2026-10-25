# M49 稳定身份与完整素材库：开源参考

核对日期：2026-10-18。仅参考设计和协议边界，本轮没有引入新的第三方依赖、复制以下项目的源码、SQL、图标或界面素材。

| 参考 | 本轮参考内容 | 采用边界 |
|---|---|---|
| [LocalSend 协议](https://github.com/localsend/protocol#2-fingerprint)、[应用许可](https://github.com/localsend/localsend/blob/main/LICENSE) | 身份与网络地址分离；明文模式可用持久随机标识，设备名只供显示 | 自行实现安装 UUID、受限 UDP 发现和口令挑战响应；不是 LocalSend 协议实现。LocalSend 应用为 Apache-2.0，不引入其源码 |
| [Syncthing 设备 ID](https://docs.syncthing.net/dev/device-ids.html)、[设备 ID 源码](https://github.com/syncthing/syncthing/blob/main/lib/protocol/deviceid.go)、[许可](https://github.com/syncthing/syncthing/blob/main/LICENSE) | 先发现地址，再核对身份；名称与地址都不能替代身份验证 | 不复制其证书 ID 算法或 MPL-2.0 源码，不宣称拥有 Syncthing 的双向 TLS 信任。保留用户选择的局域网明文模式 |
| [Immich Sync v2](https://immich.app/blog/sync-v2)、[移动同步仓库](https://github.com/immich-app/immich/blob/main/mobile/lib/infrastructure/repositories/sync_api.repository.dart)、[许可](https://github.com/immich-app/immich/blob/main/LICENSE) | 分批获取摘要、独立更新与删除、检查点和会话隔离；避免每次页面导航全量同步 | 仅参考架构。Immich 为 AGPL-3.0，本轮不复制其代码或数据库方案；自行实现 100 条分页、序号失效重取及逐项版本冲突检查 |

本轮没有“套壳”这些应用，也没有声称协议互通。既有 MIT VirtualizingWrapPanel 等依赖继续保留各自许可证。项目 MIT 许可不覆盖第三方项目。
