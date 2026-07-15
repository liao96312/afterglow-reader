# 余光阅读器

一个 Windows 本地轻量阅读器，支持 TXT、EPUB 和无 DRM MOBI。

## 测试包

发布目标为 Windows 11 x64。测试包是自包含目录包，不需要另外安装 .NET；WebView2 Evergreen Runtime 仍需在系统中可用。

解压后运行 `install.cmd`，程序会安装到 `%LocalAppData%\AfterglowReader`，并创建开始菜单快捷方式。也可以直接运行目录中的 `AfterglowReader.exe`。

卸载：再次运行安装目录中的 `uninstall.cmd`，或删除 `%LocalAppData%\AfterglowReader` 和开始菜单快捷方式。

## 基本操作

- 打开书籍：阅读页顶部的“打开文件”。
- 章节：点击“目录”，选择章节跳转。
- 老板键：`Ctrl + Tab`；`F8` 作为备用键。
- 自动滚动：`F7` 或阅读页顶部的“自动滚动”。
- 鼠标穿透：托盘菜单中切换；开启后通过托盘关闭。
- 退出：托盘菜单“退出”或窗口关闭按钮。

窗口大小、位置和阅读进度会保存到 `%LocalAppData%\AfterglowReader`。

拖动左上角“余光阅读器”文字区域可以移动窗口；拖动任意边缘或四角可以自由调整窗口大小。

## 当前测试边界

- MOBI 当前按无 DRM Beta 范围测试，复杂 KF8/HUFF-CDIC/加密样本不保证兼容。
- `Ctrl + Tab` 可能与浏览器或编辑器的标签页切换冲突；测试时请重点观察。
- 外部导航、下载和新窗口默认被阅读页拦截。

## 从源码构建

```powershell
$dotnet = Join-Path $env:LOCALAPPDATA 'dotnet\dotnet.exe'
& $dotnet build src\AfterglowReader\AfterglowReader.csproj
& $dotnet test tests\AfterglowReader.Tests\AfterglowReader.Tests.csproj
```

发布命令：

```powershell
& $dotnet publish src\AfterglowReader\AfterglowReader.csproj -c Release -r win-x64 --self-contained true -o artifacts\AfterglowReader-win-x64
```
