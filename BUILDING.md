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
- JDK 17+（建议 21 LTS）
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

3. 构建 Release（签名）：
   - 在 `AndroidApp/keystore/` 放 `liuzhuan.jks` 和 `keystore.properties`（模板如下）
   - keystore 与密码文件**不要提交到 git**
   ```properties
   storeFile=keystore/liuzhuan.jks
   storePassword=你的密码
   keyAlias=liuzhuan
   keyPassword=你的密码
   ```
   ```bash
   ./gradlew assembleRelease
   ```

产物：
- Debug：`app/build/outputs/apk/debug/app-debug.apk`
- Release：`app/build/outputs/apk/release/app-release.apk`

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
