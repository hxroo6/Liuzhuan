# 流转 — 跨应用素材暂存中转站

## 简介
流转是一个 Windows 桌面端的跨应用素材暂存中转站（扩展型剪贴板），核心是「拖拽暂存、即用即拖、不改动源文件」。

## 核心特性
- 拖入即暂存：支持从资源管理器、微信、浏览器等拖入素材
- 自动分类：视频、图片、音频、文字自动归类到对应分页
- 拖出即用：拖出文件等同于直接拖拽本地源文件
- 路径不变：绝不复制、移动、修改源文件
- 侧边栏停靠：屏幕右侧常驻，鼠标移入呼出，移出自动收回
- 本地持久化：重启后素材索引不丢失

## 运行要求
- Windows 10 / Windows 11
- 无需安装 .NET（自包含单文件部署）

## 使用方法
1. 直接运行 Liuzhuan.exe
2. 鼠标移到屏幕右侧触发条，面板自动滑出
3. 从任意应用拖入素材到面板
4. 从面板拖出素材到任意应用
5. 右键素材可删除索引（不影响源文件）
6. 底栏可清空当前分类或全部素材

## 技术栈
- C# .NET 7.0 + WPF
- Windows Shell API（缩略图、OLE 拖拽）
- JSON 本地持久化
- 单文件自包含部署

## 项目结构
```
Liuzhuan/
├── Models/MaterialItem.cs       素材数据模型
├── Services/
│   ├── DataStore.cs             数据持久化
│   ├── DragDropService.cs       拖拽服务
│   ├── ThumbnailService.cs      缩略图服务
│   └── StartupService.cs        开机自启
├── Utils/
│   ├── Logger.cs                日志系统
│   ├── FileClassifier.cs        文件分类器
│   └── Converters.cs            WPF值转换器
├── App.xaml / App.xaml.cs       应用入口
├── MainWindow.xaml / .xaml.cs   主窗口
└── Liuzhuan.csproj              项目配置
```

## 日志
- logs/run.log — 运行日志（启动、拖入拖出、错误等）
- logs/dev.log — 开发日志（每次修改记录）
