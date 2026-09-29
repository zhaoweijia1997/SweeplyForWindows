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

SweeplyForWindows finds caches, temporary files and developer leftovers you can safely remove,
shows you exactly what they are, and never deletes anything until you choose to.

<p align="center">
  <img src="docs/screenshots/clean-en-light.png" width="760" alt="SweeplyForWindows showing cleanup categories and their sizes">
</p>

> **Status: preview (0.2).** Cleaning, background mode and the activity monitor work;
> many more cleanup categories (including chat apps like WeChat) are coming next.

## What it finds

**Windows**
- Temporary files programs left behind (only items untouched for a day)
- Crash dumps and Windows error reports

**Browsers**
- Microsoft Edge and Google Chrome disk caches — history, passwords and sign-ins are kept

**Chat apps**
- WeChat (current and older versions): cache and logs; chat pictures, videos and received files
  over 6 months old, which are not selected by default. Chat history itself is never touched.

**Developer tools**
- npm, pip, NuGet, Yarn and Gradle caches

**Downloads**
- Installers (.exe, .msi, .msix) in your Downloads folder — not selected by default

Each category explains what it is and whether it comes back. Expand it to see every item,
show it in File Explorer, or untick the ones you want to keep.

## Safety first

- **Nothing is moved without you.** Sweeply only scans. Before anything moves, you see the full list
  of what would go, can untick any item or save the list, and then confirm.
- **Everything goes to the Recycle Bin**, so you can restore it. If something is too big for the
  Recycle Bin, Windows asks you first instead of deleting it for good.
- **Undo.** The last 5 cleans can be undone: what was moved goes back where it was, as long as it
  is still in the Recycle Bin.
- **A "Never clean" list.** Add folders in Settings, or press the lock next to an item. Anything on
  the list, or inside it, is never offered or moved.
- **Checked twice.** Right before moving, each item is checked again: still inside its category's
  folder, not a link to somewhere else, on this PC's own disk, not on the "Never clean" list, and
  its app not running. Items that must be old enough (temporary files, old chat pictures) still have to be.
- **Only this PC's own disks.** USB sticks and network drives are never touched.
- **Apps are left alone while they're open** (browsers, WeChat), and files in use are skipped.
- **Works offline.** No network requests, no analytics, no accounts.

## Runs quietly in the background

<p align="center">
  <img src="docs/screenshots/settings-en-light.png" width="760" alt="Settings page with background and activity monitor options">
</p>

- **Stays in the notification area.** Closing the window keeps it there; right-click the icon to exit.
  You can turn this off in Settings.
- **Start with Windows** (off by default): starts as an icon only when you sign in.
  Nothing is scanned or cleaned until you ask.
- **Activity monitor**, every part optional:
  - a live number on the icon: CPU usage, download or upload speed, or disk writes;
  - download and upload speed, CPU usage and disk writes when you point at the icon;
  - a small floating bar that stays on top of other windows. Drag it anywhere; right-click it to hide it.

<p align="center">
  <img src="docs/screenshots/monitor-bar.png" width="380" alt="Floating bar showing download, upload, CPU and disk writes">
</p>

The readings come from Windows itself (performance counters and network adapters) and never leave your PC.

## Made for Windows

<p align="center">
  <img src="docs/screenshots/clean-en-dark.png" width="760" alt="SweeplyForWindows in dark mode">
</p>

- Windows 11 Fluent look that follows your light / dark theme and accent colour.
- Progress in the window and on the taskbar button while scanning and cleaning.
- Finds your Downloads folder even if you moved it to another drive.
- 7 languages, switch any time from the sidebar — no restart needed:
  English, 简体中文, 繁體中文, 日本語, Русский, Español, हिन्दी.
  Translations other than English and Chinese would love a review from native speakers.

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

- `SweeplyForWindows.exe --snapshot <folder>` renders every page with made-up results, in every
  language, light and dark — without capturing the screen. That's how the screenshots here are made.
- `SweeplyForWindows.exe --scan-report <file>` scans read-only and writes each category's status,
  item count and size, without any paths — handy to attach to a bug report.

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
