# 余光阅读器 Afterglow Reader

**语言：** 简体中文

![Windows](https://img.shields.io/badge/Windows-10%20%2F%2011-0078D4?logo=windows&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![WebView2](https://img.shields.io/badge/Reader-WebView2-0F6CBD?logo=microsoftedge&logoColor=white)
![Formats](https://img.shields.io/badge/Formats-TXT%20%7C%20EPUB%20%7C%20MOBI-6F5C4C)

一个为 Windows 桌面设计的轻量本地阅读器：小窗、透明、可自由拖动缩放，支持 TXT、EPUB 与无 DRM MOBI，并把阅读位置保存在本机。

## 核心能力

| 能力 | 说明 |
| --- | --- |
| 本地阅读 | TXT、EPUB、无 DRM MOBI；正文在本机解析，不上传书籍内容 |
| 自动续读 | 正常退出后自动打开上次书籍并恢复到阅读位置 |
| 小窗阅读 | 记忆窗口尺寸、位置与透明度；支持拖动和八方向缩放 |
| 完全透明 | 背景不透明度可拉到 0（或点“全透明”），只留文字浮在桌面之上 |
| 阅读控制 | 目录跳章、自动滚动、F7 自动滚动、F8 隐藏/恢复、Ctrl+Tab 老板键 |
| 状态安全 | 原子写入、旧缓存兼容、未知新版缓存安全回退 |

## 快速开始

下载 `AfterglowReader-Setup-1.0.1.exe` 直接双击运行：

- 默认安装到 `%LocalAppData%\AfterglowReader`，只写入当前用户，不需要管理员权限；
- 自动创建开始菜单和桌面快捷方式，并在“设置 → 应用 → 已安装的应用”中登记卸载项；
- 安装包已内置 .NET 运行时，无需另装 .NET；系统仍需可用的 WebView2 Evergreen Runtime；
- 卸载走同一目录下的 `Uninstall.exe`，保留阅读进度与设置，重装后可以继续读。

旧的分发包方式（解压后运行 `install.cmd`）仍然保留，用于便携部署。

## 使用方式

- 点击设置面板里的“背景不透明度”可把背景调到完全透明；拉到 0 或点“全透明”后，窗口只剩文字，正文背景不再有任何底色。
- 透明度以“正文背景 alpha”为唯一开关：alpha 为 0 时页面、WebView2 与窗口三层都不铺底色；工具条和设置面板另有独立底色，不透明度归零后依然可见可点。
- 点击顶部“打开文件”选择书籍。
- 点击“目录”跳转章节；点击“自动滚动”或按 `F7` 控制滚动。
- 按 `F8` 隐藏/恢复；恢复后把鼠标移入窗口即可继续键盘控制。
- 从托盘菜单切换鼠标穿透、打开书籍或退出。
- 关闭程序后，下次启动会自动回到上次书籍和阅读位置。

## 当前边界

- MOBI 目前面向无 DRM 的基础样本；复杂 KF8、HUFF-CDIC 与加密文件未承诺兼容。
- 自动续读已完成基础验证；连续立即退出与多格式人工回归仍在进行。
- 设置面板正在收口，第一版仅包含字体、字号、行距、透明度和自动滚动速度。

## 验证

```powershell
$dotnet = Join-Path $env:LOCALAPPDATA 'dotnet\dotnet.exe'
& $dotnet test AfterglowReader.slnx --configuration Release
& $dotnet publish src\AfterglowReader\AfterglowReader.csproj -c Release -r win-x64 --self-contained true -o artifacts\publish
```

生成安装包（测试 → 发布阅读器 → 打包程序负载 → 生成单文件安装器）：

```powershell
powershell -ExecutionPolicy Bypass -File build-installer.ps1
# 输出 artifacts\AfterglowReader-Setup-1.0.1.exe
```

安装器源码在 `src/AfterglowReader.Setup`，程序负载以 `payload.zip` 嵌入；支持静默安装
`AfterglowReader-Setup-1.0.1.exe --silent --dir=<路径>` 与静默卸载 `Uninstall.exe --uninstall --silent`。
详细开发路线见 [todolist.md](todolist.md)。

## 目录结构

```text
src/AfterglowReader/        阅读器应用、格式解析与本地状态管理
src/AfterglowReader.Setup/ 单文件安装器（负载以 payload.zip 嵌入）
tests/                      自动续读、缓存兼容与格式处理测试
build-installer.ps1         一键测试 + 发布 + 打包 + 生成安装器
install.* / uninstall.*     便携部署用的 Windows 安装/卸载脚本
```

## 当前状态

项目已具备可安装、可续读的 Windows 本地阅读体验；复杂 MOBI/KF8 兼容与设置面板仍在持续收口。
