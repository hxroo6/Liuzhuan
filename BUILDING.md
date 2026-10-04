# 构建指南 (BUILDING.md)

## 电脑端（Liuzhuan/）

**环境要求**：.NET 7 SDK（Windows）

```bash
cd Liuzhuan
dotnet build -c Release                      # 构建验证
dotnet publish -c Release -r win-x64 \
  --self-contained true -o dist              # 发布独立可执行版
```

产物：`dist/Liuzhuan.exe`（自包含，无需安装 .NET 运行时）。

## 安卓端（AndroidApp/）

**环境要求**：
- JDK 21
- Android SDK：compileSdk 36 / targetSdk 36 / minSdk 36
- Gradle 8.11+

**关键配置**：

1. 在 `AndroidApp/` 根目录创建 `local.properties`：
   ```properties
   sdk.dir=/你的/Android/SDK/路径
   ```
   > ⚠️ `sdk.dir` 必须用正斜杠（`/`），反斜杠会被 Properties 转义吞掉。

2. 构建 Debug：
   ```bash
   cd AndroidApp
   ./gradlew assembleDebug
   ```

3. 配置正式签名并构建日常交付包：
   - 在 `AndroidApp/keystore/` 放 `liuzhuan.jks` 和 `keystore.properties`（模板如下）
   - keystore 与密码文件**不要提交到 git**
   ```properties
   storeFile=keystore/liuzhuan.jks
   storePassword=你的密码
   keyAlias=liuzhuan
   keyPassword=你的密码
   ```
   ```bash
   ./gradlew assembleDebug assembleDistribution
   python3 ../scripts/check_android_distribution.py app/build/outputs/apk/distribution/app-distribution.apk --aapt "$ANDROID_HOME/build-tools/36.0.0/aapt"
   ```

产物：
- Debug：`app/build/outputs/apk/debug/app-debug.apk`
- Distribution（日常交付）：`app/build/outputs/apk/distribution/app-distribution.apk`
- Release（可选混淆构建，`assembleRelease`）：`app/build/outputs/apk/release/app-release.apk`

Distribution 与 Debug 共用源码和签名，关闭可调试标记，不启用 R8。Debug 用于开发，不作为日常安装包；部分旧 LSPosed 框架会在注入可调试进程时崩溃。覆盖安装必须使用与原 APK 相同的证书，并到无障碍设置关闭→重新开启流转服务。

未配置私人 keystore 时，CI 的 Distribution 使用测试证书，不能覆盖安装本地正式签名的 APK。公开 Releases 上传已验签的本地交付包，不上传 keystore、密码文件或手机数据。Windows 使用 `aapt.exe`、`apksigner.bat` 对应命令；公开 Windows ZIP 只包含程序及说明，不包含 `data`、日志、备份或 PDB。

## 常见坑

| 问题 | 解法 |
|------|------|
| `sdk.dir` 找不到 SDK | 检查 local.properties，正斜杠 + 正确路径 |
| 工程路径含中文编译失败 | 把工程复制到纯 ASCII 路径编译 |
| `libs.versions.toml` 注释报错 | 注释只能用 `#`，不能用 `//` |
| Release 签名失败 | 确认 keystore.properties 存在且内容正确 |

## 测试

仓库内 `scripts/` 提供 Python 端到端测试（依赖 `websocket-client`）：

```bash
pip install websocket-client
python scripts/test_m4_multi.py   # 多设备并发测试
```
