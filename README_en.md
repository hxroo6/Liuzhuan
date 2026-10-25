# Liuzhuan · 流转

**Move text, photos and files between your phone and PC.**

A floating shelf on your desktop, with an Android companion for sending and receiving. Drop materials in, drag them out when needed, or use your phone as a PC microphone.

[![Build](https://github.com/hxroo6/Liuzhuan/actions/workflows/build.yml/badge.svg)](https://github.com/hxroo6/Liuzhuan/actions/workflows/build.yml)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

[简体中文](README.md) · **English** · [Download](https://github.com/hxroo6/Liuzhuan/releases/latest) · [User guide](docs/USAGE_en.md) · [Report an issue](https://github.com/hxroo6/Liuzhuan/issues)

> **v1.20.0 source update:** repair PC microphone devices from the phone, protect verified audio settings, browse and organize the full mobile library, pin offline originals, resume transfers after restarts, control transfer priorities/rates, and safely recover libraries or exchange project packages. [What's new](docs/releases/v1.20.0.md) · [Validation scope](docs/performance/M57_RELIABILITY_OFFLINE_2026-10-24.md)
>
> **Check the download version:** current Releases installers are still **v1.9.0** and do not include these additions. v1.20.0 is currently available as source: [build it](BUILDING.md) and update both peers. New installers have not been uploaded.

## What you can do

| Your task | How Liuzhuan helps |
|---|---|
| Keep materials handy across apps | Drop files, images and text onto the floating PC shelf; search, favorite, preview and drag them out |
| Send from phone to PC | Send text manually, share files to Liuzhuan, or sync clipboard content; background copying has requirements explained below |
| Send from PC to phone | Browse the complete PC library on Receive, search/filter across the library, edit organization metadata, and tap to copy or download |
| Organize a collection | Multi-select, groups, tags, notes, saved filters, batch export and relinking missing sources |
| Work with phone photos | Convert received HEIC images to separate PNG/JPEG files; batch resize, convert and save image presets |
| Move to a new PC | Export materials and configuration together, then import the archive on another PC |
| Take one project with you | Export selected materials as a project ZIP, then verify, preview and merge into an existing library while keeping organization metadata |
| Open selected files offline | Explicitly pin originals, verify their bytes and retain private copies with a configurable quota |
| Transfer files while dictating | Set directional priority/rate limits and optionally yield bandwidth while audio is forwarded |
| Recover an index or switch libraries | Compare retained index versions and schedule a restart-time action without redirecting the running library |
| Dictate without a PC microphone | Send phone audio over the LAN into a Windows virtual microphone for dictation apps such as Codex; experimental |
| Protect working audio settings | Automatically retain the verified virtual route; confirm playback and dictation to save a complete reference, with warnings and explicit recovery if settings change |
| Fix stale PC microphone devices from the phone | Open the phone waveform → Computer audio → Check and repair; the authenticated PC applies a safely identified virtual pair and returns the result |

**Connect over the same Wi-Fi, or connect the PC to the phone's hotspot.** Materials and audio travel directly between devices without a Liuzhuan cloud service. Public Internet/cellular remote connections are not available. A dictation app may separately use online services.

## Download and install

The links below provide the released **v1.9.0** installers. Features listed for v1.20.0 require the current source on both peers; a source ZIP is not an installer.

| Device | Download | Requirements |
|---|---|---|
| PC | [Windows x64 ZIP](https://github.com/hxroo6/Liuzhuan/releases/download/v1.9.0/Liuzhuan-v1.9.0-windows-x64.zip) | Windows 10/11, 64-bit; extract and run, no separate .NET installation |
| Phone | [Android APK](https://github.com/hxroo6/Liuzhuan/releases/download/v1.9.0/Liuzhuan-v1.9.0-android.apk) | **Android 16 or newer** |
| Verification and changes | [SHA-256](https://github.com/hxroo6/Liuzhuan/releases/download/v1.9.0/Liuzhuan-v1.9.0-SHA256SUMS.txt) · [Release notes](docs/releases/v1.9.0.md) | Download binaries from Releases; a source ZIP is not a runnable app |

Manual sending and file transfers **do not require root**. The phone microphone needs a separate virtual audio driver; material features do not.

## First connection in three steps

1. **Open the PC app.** Extract the Windows ZIP and run `app/Liuzhuan.exe`. Enable **局域网服务器** (LAN server) in Settings or the tray menu, then open **配对二维码（手机扫码连接）** (pairing QR). Allow the app on your trusted private network if Windows Firewall asks.
2. **Connect the phone.** Join the same LAN. On Android's Connect page, tap **扫码连接全部功能** (scan to connect all features) and scan the PC QR. This also attempts to start the phone microphone and may request recording permission. For materials only, decline recording permission, or use **搜索电脑** (find PCs), then enter the password displayed on the PC and connect.
3. **Try both directions.** Manually send text from the phone's Send page and check the PC shelf. Then drop a file onto the shelf and check the phone's Receive page.

After authentication, the PC shelf expands with a success message. Use Connection history next time; after changing Wi-Fi or hotspot networks, scan a fresh QR because the address may change.

[Connection, permissions and hotspot details →](docs/USAGE_en.md#connect)

## Everyday use

| Task | Action |
|---|---|
| Stash and use PC materials | Drag onto the shelf, then click to copy or drag out; Space previews images/text |
| Select and organize | Ctrl/Shift multi-select, then **整理…** or F2; Ctrl+Shift+S saves a selection |
| Send a phone file | Use the attachment action on Send, or share a file to Liuzhuan from another app |
| Save a PC file on the phone | Tap a material on Receive; downloads go to `Internal storage / Download / Liuzhuan` |
| Manage transfers | Open **收发** on either device to search tasks, filter direction/status, pause, continue or cancel; **打开接收目录** opens the file manager |
| Review duplicate files | PC **更多 → 查找内容相同的文件** → start; compare size and SHA-256, then inspect matching paths without automatic deletion |
| Pack a project | Select materials on PC → **更多 → 项目素材包** → export; on import, verify and preview before merging |
| Reuse phone searches | Tap **保存此筛选** in the Receive library, then apply, update or delete from **常用搜索**; up to 10 per library |
| Keep an original offline | Tap **保留原件** on a material; manage quota, verification, opening and unpinning from **离线原件** |
| Pack a project from the phone | Multi-select on Receive, then **项目素材包** to export/download; select an uploaded project ZIP to preview and confirm a PC merge |
| Control transfer bandwidth | Open **收发 → 传输调度**; new limits affect subsequent payload blocks while manual pauses remain paused |
| Recover or manage staging | PC Settings **素材库恢复与切换**; the storage window provides **项目暂存空间** for explicit inspection and recoverable recycling |
| Send phone clipboard text | In standard mode, copy and return to Liuzhuan; see below for background copying |

**M48 supports byte-offset resume and restart recovery when both devices are updated.** New file tasks verify chunks and the complete SHA-256. Text outbox and receipts persist locally; recovery is bound to the original PC, and manually paused tasks stay paused. Older peers still retransmit from the start. [Transfer details →](docs/USAGE_en.md#files)

### Phone clipboard: choose the right mode

| Mode | Requirements | Behavior |
|---|---|---|
| Standard | No root; enable accessibility and automatic sync for automatic capture | Copy and return to Liuzhuan, use the system text-selection action **流转**, or share directly. Android restrictions prevent guaranteed background clipboard capture in every app |
| LSPosed | Root, Liuzhuan module enabled, target apps in its scope | Observes target apps' clipboard writes for automatic background delivery; restart target apps after enabling |

If you do not need background copying, manual sending and sharing are enough. [Setup and privacy exclusions →](docs/USAGE_en.md#clipboard)

## Storage and history: preview before cleaning

On the PC, **Settings → Storage, retention and cleanup preview** separates index, managed copies, thumbnails, external originals, recovery data and trash. Defaults protect favorites and the newest 500 entries, showing candidates older than 30 days. Save your own policy; nothing is deleted automatically.

Removing index entries keeps originals and supports Undo. Unreferenced managed files first move to recoverable trash. Review its contents before separately confirming permanent deletion to free space. External originals, HEIC originals, undo sources, resumable references and linked paths stay protected. Retention parameters migrate; trash does not.

On Android, **Transfers → Storage and history** shows local usage, clears redownloadable thumbnail memory and previews completed display history. Downloads, pending sources and durable receipts remain intact. Browsing retains at most 1,000 summaries while further paging and full-library search remain available.

[Controls and boundaries →](docs/USAGE_en.md#storage) · [Scale and validation record →](docs/performance/M51_STORAGE_SCALE_2026-10-18.md)

## Phone microphone without losing speaker playback

**Optional and experimental.** Liuzhuan transports audio; Codex or another dictation app performs speech recognition.

```text
Phone mic → Liuzhuan → CABLE Input → CABLE Output → Dictation app
Other PC audio → Physical speakers / headphones
```

1. Install VB-CABLE yourself from the [official VB-Audio website](https://vb-audio.com/Cable/) and restart as instructed. The driver is not bundled and has its own [licensing terms](https://vb-audio.com/Services/licensing.htm).
2. Click the PC title waveform. Choose **CABLE Input** as output and **CABLE Output** as the matching recording endpoint. Do not choose `CABLE In 16ch` or enable diagnostic-only mode.
3. On the phone, scan the regular pairing QR with **扫码连接全部功能** and grant recording permission. One scan connects materials and audio. If materials are already connected, open the top-left waveform and tap **连接当前电脑麦克风**.
4. Use **CABLE Output** as the dictation app's input. If it follows the system input, set that as the Windows default recording device. **Keep Windows default playback and default communications playback on physical speakers/headphones**, not CABLE Input.

Click either waveform to open settings. Dismissing the phone panel keeps capture running; tap End when finished. Quiet traffic-saving reduces audio transmission while the microphone continues capturing.

- **Instant standby:** capture remains active after an authorized scan, ready for PC forwarding requests.
- **Energy-saving connection:** select it on the phone microphone page to end audio capture and its wake lock while keeping materials connected. Later scans leave the microphone off. Tap Open current PC microphone in the foreground when needed; there is no background microphone wake-up.
- **Recovery:** actionable permission, system blocking, capture and network messages; the PC Retry original devices button preserves exact endpoint IDs and never falls back to speakers.
- **Experimental follow:** off by default. A verified idle–active–idle dictation cycle is required within 45 seconds. Unknown ownership requires manual mode; manual stop always wins. Compatibility is not proof of ASR success.

Capture/read/send counters and audio CPU-lock status describe work, not battery consumption. See the [M50 validation record](docs/performance/M50_AUDIO_RECOVERY_2026-10-18.md) for measured scope and remaining device checks.

[Full setup, pause/end behavior and audio troubleshooting →](docs/USAGE_en.md#microphone)

## Upgrades, migration and limits

- **Upgrade:** Exit the PC app, replace the entire `app` folder and preserve the adjacent `data` folder. Install a same-signature APK over the existing app, then toggle Liuzhuan's accessibility service **off → on**. Do not uninstall first just to upgrade.
- **New PC:** Settings → **迁移与备份（配置 + 素材）** → export on the old PC, import on the new one → restart Liuzhuan → pair the phone again. Archives are unencrypted and contain materials and pairing credentials; keep them private. [Migration guide →](docs/USAGE_en.md#migration)
- **Network and privacy:** Transfers are currently plaintext LAN traffic. A pairing password is not transport encryption. Use a trusted LAN and do not expose the ports to the public Internet. Automatic clipboard capture supports pauses and app exclusions.
- **Compatibility:** HEIC decoding needs Windows codec support. File managers, ROMs, mixed DPI, automatic dictation following and actual battery usage still need broader validation. [Troubleshooting →](docs/USAGE_en.md#troubleshooting)

## Further reading

[English user guide](docs/USAGE_en.md) · [中文使用指南](docs/USAGE.md) · [All releases](https://github.com/hxroo6/Liuzhuan/releases) · [Build guide](BUILDING.md) · [Architecture](docs/ARCHITECTURE.md) · [Known issues](docs/KNOWN_ISSUES.md) · [Roadmap](docs/PRODUCT_ROADMAP.md)

Source: `Liuzhuan/` contains Windows WPF/.NET 10, `AndroidApp/` contains Kotlin/Compose with AGP 8.10.1, and `scripts/` contains checks. Liuzhuan uses the [MIT License](LICENSE); third-party components retain their own licenses. MIT does not cover external virtual audio drivers. The app UI is currently Chinese; English labels above explain the corresponding Chinese controls.
