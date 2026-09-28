# SweeplyForWindows

**English** · [简体中文](#简体中文)

A small, honest junk cleaner for Windows. Open source, offline, no ads, no accounts.

> **Status: early preview (0.0.1).** This build only opens a window — cleaning arrives in 0.1.
> Follow the [releases](../../releases) to get it when it's ready.

<p align="center">
  <img src="docs/screenshots/main-light.png" width="420" alt="SweeplyForWindows, light theme">
  <img src="docs/screenshots/main-dark.png" width="420" alt="SweeplyForWindows, dark theme">
</p>

## What 0.1 will do

- Find temporary files, app caches, crash dumps, error reports and developer caches
  (npm, pip, NuGet, Gradle, Yarn) that are safe to remove.
- Explain every category: what it is, and whether Windows or the app will recreate it.
- **Never delete anything until you choose to.** Everything goes to the **Recycle Bin**,
  so you can restore it.
- Skip apps that are running, and never touch removable or network drives.
- Work completely offline: no network requests, no analytics, no accounts.
- Follow the Windows light / dark theme, in 7 languages: English, 简体中文, 繁體中文,
  日本語, Русский, Español, हिन्दी.

## Install

1. Download `SweeplyForWindows-<version>-win-x64.zip` from [Releases](../../releases).
2. Unzip it anywhere and run `SweeplyForWindows.exe`. Nothing to install, no .NET needed.
3. Windows SmartScreen may say the app is unrecognised, because it isn't code-signed yet.
   Click **More info → Run anyway**.

Requires Windows 10 or 11, 64-bit.

## Build from source

Requires the .NET 9 SDK.

```powershell
dotnet build
dotnet test
powershell -ExecutionPolicy Bypass -File tools\release.ps1   # zip in artifacts\
```

`SweeplyForWindows.exe --snapshot <folder>` renders the window to PNG in light and dark
without capturing the screen — that is how the screenshots above are made.

## Support SweeplyForWindows

SweeplyForWindows is free and always will be. If it saved you some space or some time,
you can buy the developer a coffee — WeChat Pay or Alipay in China, PayPal anywhere.
Every coffee keeps the project going. Thank you!

| WeChat Pay | Alipay | PayPal |
|:---:|:---:|:---:|
| <img src="docs/donate/wechat.png" height="260" alt="WeChat Pay QR code"> | <img src="docs/donate/alipay.png" height="260" alt="Alipay QR code"> | <img src="docs/donate/paypal.png" height="260" alt="PayPal QR code"> |

Not in a position to donate? A ⭐ on this repo, a bug report or a translation fix helps just as much.

## Contact

Bugs and ideas: [open an issue](../../issues).

## License

[MIT](LICENSE)

---

## 简体中文

一个小巧、老实的 Windows 垃圾清理工具。开源、离线，没有广告，不用注册账号。

> **状态：早期预览（0.0.1）。**这一版只能打开窗口，清理功能在 0.1 版加入。
> 关注 [Releases](../../releases) 获取新版本。

### 0.1 版会做什么

- 找出可以放心删除的临时文件、应用缓存、崩溃转储、错误报告和开发者缓存（npm、pip、NuGet、Gradle、Yarn）。
- 每一类都说清楚：它是什么、Windows 或应用会不会重新生成。
- **你不点确定，什么都不会删。**删除的东西都进**回收站**，可以还原。
- 跳过正在运行的程序，不碰 U 盘和网络盘。
- 完全离线：不联网、不统计、不需要账号。
- 跟随 Windows 浅色 / 深色主题，支持 7 种语言。

### 安装

1. 在 [Releases](../../releases) 下载 `SweeplyForWindows-<版本>-win-x64.zip`。
2. 解压到任意位置，运行 `SweeplyForWindows.exe`。不用安装，也不需要另装 .NET。
3. Windows 可能提示"无法识别的应用"，因为还没有代码签名。点**更多信息 → 仍要运行**即可。

需要 64 位 Windows 10 或 11。

### 支持作者

SweeplyForWindows 永久免费。如果它帮你省下了空间或时间，欢迎请作者喝杯咖啡：
国内可用微信、支付宝，海外可用 PayPal。每一杯咖啡都是项目继续下去的动力，谢谢！

| 微信支付 | 支付宝 | PayPal |
|:---:|:---:|:---:|
| <img src="docs/donate/wechat.png" height="260" alt="微信收款码"> | <img src="docs/donate/alipay.png" height="260" alt="支付宝收款码"> | <img src="docs/donate/paypal.png" height="260" alt="PayPal 收款码"> |

不方便打赏也没关系：给仓库点个 ⭐、报个 bug、帮忙改改翻译，同样是很大的支持。

### 联系

问题和建议：[提交 issue](../../issues)。
