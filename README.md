# 余光阅读器 Afterglow Reader

![Windows](https://img.shields.io/badge/Windows-10%20%2F%2011-0078D4?logo=windows&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![WebView2](https://img.shields.io/badge/Reader-WebView2-0F6CBD?logo=microsoftedge&logoColor=white)
![Formats](https://img.shields.io/badge/Formats-TXT%20%7C%20EPUB%20%7C%20MOBI-6F5C4C)

一个为 Windows 桌面设计的轻量本地阅读器：小窗、透明、可自由拖动缩放，支持 TXT、EPUB 与无 DRM MOBI，并把阅读位置保存在本机。

## 功能

| 能力 | 说明 |
| --- | --- |
| 本地阅读 | TXT、EPUB、无 DRM MOBI；正文在本机解析，不上传书籍内容 |
| 自动续读 | 正常退出后自动打开上次书籍并恢复到阅读位置 |
| 小窗阅读 | 记忆窗口尺寸、位置与透明度；支持拖动和八方向缩放 |
| 阅读控制 | 目录跳章、自动滚动、F7 自动滚动、F8 隐藏/恢复、Ctrl+Tab 老板键 |
| 状态安全 | 原子写入、旧缓存兼容、未知新版缓存安全回退 |

## 安装与试用

从 [Releases](https://github.com/liao96312/afterglow-reader/releases) 下载 `AfterglowReader-test-win-x64.zip`，解压后运行 `install.cmd`。

程序会安装到 `%LocalAppData%\AfterglowReader` 并创建开始菜单快捷方式。无需安装 .NET；系统仍需可用的 WebView2 Evergreen Runtime。

## 使用方式

- 点击顶部“打开文件”选择书籍。
- 点击“目录”跳转章节；点击“自动滚动”或按 `F7` 控制滚动。
- 按 `F8` 隐藏/恢复；恢复后把鼠标移入窗口即可继续键盘控制。
- 从托盘菜单切换鼠标穿透、打开书籍或退出。
- 关闭程序后，下次启动会自动回到上次书籍和阅读位置。

## 当前边界

- MOBI 目前面向无 DRM 的基础样本；复杂 KF8、HUFF-CDIC 与加密文件未承诺兼容。
- 自动续读已完成基础验证；连续立即退出与多格式人工回归仍在进行。
- 设置面板正在收口，第一版仅包含字体、字号、行距、透明度和自动滚动速度。

## 开发

```powershell
$dotnet = Join-Path $env:LOCALAPPDATA 'dotnet\dotnet.exe'
& $dotnet test AfterglowReader.slnx --configuration Release
& $dotnet publish src\AfterglowReader\AfterglowReader.csproj -c Release -r win-x64 --self-contained true -o artifacts\AfterglowReader-win-x64
```

测试包与安装脚本位于 `artifacts/AfterglowReader-win-x64`。详细开发路线见 [todolist.md](todolist.md)。
