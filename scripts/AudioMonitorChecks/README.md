# 音频配置监听生命周期检查

Windows + .NET 10 上运行：

```powershell
./scripts/AudioMonitorChecks/Run-AudioMonitorChecks.ps1
```

本机 SDK 不在 PATH 时指定 `-DotnetPath F:/dotnet10/dotnet.exe`。CI 在 Windows runner 上使用 `setup-dotnet` 配置 .NET 10 后执行上面的命令即可；不依赖 Android、虚拟驱动或主 WPF 项目，也不引用主项目的构建输出。

脚本首先独立编译真实 NAudio 2.2.1 接口工程 `RealApi/RealApi.csproj`，不依赖或占用主 WPF 的输出。随后直接编译生产 `AudioConfigurationMonitor.cs` 与 `MicrophonePreferences.cs`，用隔离的 Core Audio 替身验证生命周期和调度，共 30 项：重复启动与关闭、启动不阻塞、六路默认端点去重、空闲无轮询、1000 次通知合并、回调线程隔离、音量变化不重绑、设备变化重绑、注销与关闭后通知抑制、启动前关闭和注册 HRESULT 失败释放。

新增覆盖：精确所选虚拟输出/采集端点单独音量变化、后台读取选择、选择变更主动刷新、旧选择退订、默认与所选去重、六个独立默认端点加两个所选端点最多八个订阅、1000 次主动刷新合并、所选设备断开/方向不符/读取设置失败时保留默认监听、关闭后主动刷新无效。`RequestRefresh()` 是单次事件通知；正确配置下不把 CABLE 设为系统默认外放，仍然可以监听它自身的静音变化。

测试只调用 Windows COM 初始化，不读取或修改真实音频设备，不采集、播放、保存音频，不包含现场设备 ID 或配置 JSON。通知合并使用真实单次计时器，等待设有期限；这些结果只证明替身条件下的控制逻辑和真实 NAudio 类型/API 编译兼容，实际扬声器外放、听写、驱动与系统通知仍须独立验收。
