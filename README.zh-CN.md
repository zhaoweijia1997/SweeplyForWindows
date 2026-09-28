<p align="center"><img src="docs/icon.png" width="128" alt="SweeplyForWindows 图标"></p>

<h1 align="center">SweeplyForWindows</h1>

<p align="center">
  一个小巧、老实的 Windows 垃圾清理工具。<br>
  开源 · 离线 · 无广告 · 无需账号
</p>

<p align="center">
  <a href="../../releases"><img src="https://img.shields.io/github/v/release/zhaoweijia1997/SweeplyForWindows?include_prereleases&label=release" alt="最新版本"></a>
  <a href="../../releases"><img src="https://img.shields.io/github/downloads/zhaoweijia1997/SweeplyForWindows/total" alt="下载量"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4" alt="Windows 10 | 11">
  <a href="LICENSE"><img src="https://img.shields.io/github/license/zhaoweijia1997/SweeplyForWindows" alt="MIT 许可证"></a>
</p>

<p align="center"><a href="README.md">English</a> · <b>简体中文</b></p>

> **状态：早期预览（0.0.1）。**这一版只能打开窗口，清理功能在 0.1 版加入。
> 关注 [Releases](../../releases) 获取新版本。

<p align="center">
  <img src="docs/screenshots/main-light.png" width="420" alt="SweeplyForWindows 浅色主题">
  <img src="docs/screenshots/main-dark.png" width="420" alt="SweeplyForWindows 深色主题">
</p>

## 0.1 版会做什么

- 找出可以放心删除的临时文件、应用缓存、崩溃转储、错误报告和开发者缓存（npm、pip、NuGet、Gradle、Yarn）。
- 每一类都说清楚：它是什么、Windows 或应用会不会重新生成。
- 展开任意一类，可以看到每个文件夹、在资源管理器里打开，或者取消勾选想保留的。

## 安全第一

- **你不点确定，什么都不会删。**Sweeply 只负责扫描，删不删由你选。
- **删除的东西都进回收站**，随时可以还原。
- **只动这台电脑自己的硬盘。**U 盘、网络盘一律不扫、不碰。
- **不碰正在运行的程序。**打开着的程序的缓存会跳过。
- **完全离线。**不联网、不统计、不需要账号。

## 为 Windows 而做

- 跟随 Windows 浅色 / 深色主题，Windows 11 Fluent 外观。
- 在任务栏按钮上显示进度。
- 7 种语言：English、简体中文、繁體中文、日本語、Русский、Español、हिन्दी。

## 安装

1. 在 [Releases](../../releases) 下载 `SweeplyForWindows-<版本>-win-x64.zip`。
2. 解压到任意位置，运行 `SweeplyForWindows.exe`。不用安装，也不需要另装 .NET。
3. Windows 可能提示"无法识别的应用"，因为还没有代码签名。点**更多信息 → 仍要运行**即可。

需要 64 位 Windows 10 或 11。

## 从源码构建

需要 .NET 9 SDK。

```powershell
dotnet build
dotnet test
powershell -ExecutionPolicy Bypass -File tools\release.ps1   # 压缩包输出到 artifacts\
```

`SweeplyForWindows.exe --snapshot <文件夹>` 会把窗口按浅色、深色各画成一张 PNG，不截屏——上面的截图就是这样生成的。

## 支持作者

SweeplyForWindows 永久免费。如果它帮你省下了空间或时间，欢迎请作者喝杯咖啡：
国内可用微信、支付宝，海外可用 PayPal。每一杯咖啡都是项目继续下去的动力，谢谢！

<table align="center">
  <tr>
    <th>微信支付</th>
    <th>支付宝</th>
    <th>PayPal</th>
  </tr>
  <tr>
    <td><img src="docs/donate/wechat.png" height="240" alt="微信收款码"></td>
    <td><img src="docs/donate/alipay.png" height="240" alt="支付宝收款码"></td>
    <td><img src="docs/donate/paypal.png" height="240" alt="PayPal 收款码"></td>
  </tr>
</table>

不方便打赏也没关系：给仓库点个 ⭐、报个 bug、帮忙改改翻译，同样是很大的支持。

## 联系

问题和建议：[提交 issue](../../issues)。

## 许可证

[MIT](LICENSE)
