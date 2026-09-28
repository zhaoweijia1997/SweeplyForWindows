<p align="center"><img src="docs/icon.png" width="128" alt="SweeplyForWindows icon"></p>

<h1 align="center">SweeplyForWindows</h1>

<p align="center">
  A small, honest junk cleaner for Windows.<br>
  Open source · Offline · No ads · No accounts
</p>

<p align="center">
  <a href="../../releases"><img src="https://img.shields.io/github/v/release/zhaoweijia1997/SweeplyForWindows?include_prereleases&label=release" alt="Latest release"></a>
  <a href="../../releases"><img src="https://img.shields.io/github/downloads/zhaoweijia1997/SweeplyForWindows/total" alt="Downloads"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4" alt="Windows 10 | 11">
  <a href="LICENSE"><img src="https://img.shields.io/github/license/zhaoweijia1997/SweeplyForWindows" alt="MIT license"></a>
</p>

<p align="center"><b>English</b> · <a href="README.zh-CN.md">简体中文</a></p>

> **Status: early preview (0.0.1).** This build only opens a window — cleaning arrives in 0.1.
> Watch the [releases](../../releases) to get it when it's ready.

<p align="center">
  <img src="docs/screenshots/main-light.png" width="420" alt="SweeplyForWindows, light theme">
  <img src="docs/screenshots/main-dark.png" width="420" alt="SweeplyForWindows, dark theme">
</p>

## What 0.1 will do

- Find temporary files, app caches, crash dumps, error reports and developer caches
  (npm, pip, NuGet, Gradle, Yarn) that are safe to remove.
- Explain every category: what it is, and whether Windows or the app will recreate it.
- Expand any category to see every folder, open it in File Explorer, or untick what you want to keep.

## Safety first

- **Nothing is deleted without you.** Sweeply only scans until you pick what to clean.
- **Everything goes to the Recycle Bin**, so you can restore it.
- **Only this PC's own disks.** USB and network drives are never scanned or touched.
- **Running apps are left alone.** Caches of apps that are open are skipped.
- **Works offline.** No network requests, no analytics, no accounts.

## Made for Windows

- Follows the Windows light / dark theme with the Windows 11 Fluent look.
- Shows progress on the taskbar button.
- 7 languages: English, 简体中文, 繁體中文, 日本語, Русский, Español, हिन्दी.

## Install

1. Download `SweeplyForWindows-<version>-win-x64.zip` from [Releases](../../releases).
2. Unzip it anywhere and run `SweeplyForWindows.exe`. Nothing to install, no .NET needed.
3. Windows SmartScreen may say the app is unrecognised, because it isn't code-signed yet.
   Click **More info → Run anyway**.

Requires 64-bit Windows 10 or 11.

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

<table align="center">
  <tr>
    <th>WeChat Pay</th>
    <th>Alipay</th>
    <th>PayPal</th>
  </tr>
  <tr>
    <td><img src="docs/donate/wechat.png" height="240" alt="WeChat Pay QR code"></td>
    <td><img src="docs/donate/alipay.png" height="240" alt="Alipay QR code"></td>
    <td><img src="docs/donate/paypal.png" height="240" alt="PayPal QR code"></td>
  </tr>
</table>

Not in a position to donate? A ⭐ on this repo, a bug report or a translation fix helps just as much.

## Contact

Bugs and ideas: [open an issue](../../issues).

## License

[MIT](LICENSE)
