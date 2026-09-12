# Liuzhuan (流转)

> Cross-app material staging hub: drag-and-drop stash on desktop, one-copy instant push from Android, bidirectional file transfer, multi-device realtime sync

[![Build](https://github.com/hxroo6/Liuzhuan/actions/workflows/build.yml/badge.svg)](https://github.com/hxroo6/Liuzhuan/actions/workflows/build.yml)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
English · [中文](README.md)

Liuzhuan is a **pure LAN** material transfer tool. The desktop side is a floating side panel on the edge of your screen (drag-and-drop stash, use-and-drag), and the Android side pushes clipboard text and shared files from your phone to the PC in realtime — and pulls PC materials back to the phone whenever you need them.

**No cloud servers involved.** All data flows only inside your own local network.

## ✨ Features

### New in the local development build (M29, not yet on Releases)

- **Connection and discovery fixes**: QR codes exclude Wintun/VPN virtual adapters and allow LAN address selection. Android searches off the UI thread and reports failures inline. Scan a new QR code after updating.
- **Less work when switching pages**: the Receive list composes visible rows, and transfer progress no longer refreshes the entire main screen on every tick. Device performance still needs verification.
- **Consistent motion**: desktop panel transitions reverse smoothly, with card press feedback and a sliding category indicator. Android retains disclosure, thumbnail, and progress feedback without full-page transitions. Animations follow system settings.
- **More stable details**: the collapsed desktop panel exposes its actual trigger strip, and thumbnail updates preserve selection. Android retains drafts and page scroll positions and avoids stale photo flashes when items are inserted.
- **Transfer tasks**: open “收发” at the bottom of the desktop panel or top of the Android app to see progress, speed, and outcomes for the current session. Failed phone tasks can be retried.
- **Photo thumbnails on Android**: the Receive page loads and caches previews. Downloads stream to storage; incomplete files remain hidden from the gallery.
- **Desktop quick preview**: select an image or text and press Space. Browse previous/next items, zoom images, fit to window, copy, or press Esc to close.
- **Completion reflects the actual stage**: text waits for desktop registration, incomplete uploads are rejected, and desktop “sent” is distinct from phone “saved.” Upgrade both ends together. Task history resets on restart; retries transfer the whole file again.

### Desktop (C# WPF)
- **Automatic HEIC conversion**: detects uploaded HEIC images by their actual contents and converts them to PNG (default, lossless) or JPEG, even when incorrectly named `.png`; the original file is always retained
- Floating side panel: drag-and-drop stash any file/image/text, drag out to use
- Material categories: text / image / video / audio / file
- Search, delete, undo (Ctrl+Z), paste from clipboard (Ctrl+V)
- **Clipboard monitor (toggleable)**: copy anywhere on the PC → text is auto-captured into Liuzhuan and synced to phones
- **Pairing QR code**: one-click from the tray menu; phone scans to connect (auto-fills IP/port/password)
- **UDP auto-discovery**: phone finds PCs on the LAN with one tap
- Hotspot expand + self-healing: panel expands when the mouse nears the screen edge; auto-repositions after monitor changes
- Tray menu: local IP, password management, connected devices, auto-start

### Android (Kotlin + Compose, Android 16+)
- **Background copy, instant push (LSPosed mode)**: copy in any app (WeChat included) → pushed straight to the PC, no app-switching, fully automatic. The module hooks the system clipboard **write path** (`setPrimaryClip`) — event-driven, never reads the clipboard, zero polling, purely observe-only (zero interference with the source app). Requires Root + LSPosed, see "Two modes" below
- **Image/file copy sync**: copy an image or file → auto-uploaded to the PC material library
- **Send files**: "Attach file" on the Send tab accepts any format/size → streamed over LAN (constant memory; a GB-sized file takes about a minute)
- **Copy to PC (standard mode)**: copy anywhere, switch back to Liuzhuan → the latest clipboard content is pushed the moment the window gains focus; apps exposing text selections (browsers, editors) push background copies automatically
- **Text-selection menu**: long-press to select text in any app → choose "Liuzhuan" (流转) in the system menu → sent directly without touching the clipboard
- **Background auto-send toggle**: turn off "auto-send clipboard" anytime from the Send tab (default on)
- **Receive page**: realtime mirror of the PC's recent materials (WS incremental push, zero polling, battery-friendly)
- **Pipeline diagnostics**: the Connect tab log area shows the live status and history of every stage (capture → send) — no adb needed
- **Tap a material**: text → copy to clipboard; image/video/audio → save to gallery
- **Share-to-upload**: share text/files from any app → Liuzhuan → auto-uploaded to the PC
- **Scan-to-connect**: scan the QR code on the PC screen to connect in one step
- **Auto-discovery**: scan for PCs on the LAN, tap to fill the IP
- **Pause reconnect**: stop the auto-reconnect loop anytime while it is retrying
- **Double-tap logs to copy**: double-tap the log area to copy all logs for debugging
- **Multi-device**: multiple phones/tablets can connect simultaneously; changes sync everywhere

### Security
- LAN-only, zero cloud dependency
- Password hashed with SHA-256 (WS handshake + HTTP file auth)
- File download/upload requires the password hash; unauthorized access is rejected
- Clipboard content never leaves your LAN — safe for sensitive workflows

## 📦 Architecture

```
┌────────────┐   WS 8899 (control: handshake/heartbeat/text/list/broadcast)  ┌──────────────┐
│ Android ×N ├──────────────────────────────────────────────────────────────►│              │
└────────────┘                                                               │   Liuzhuan   │
┌────────────┐   HTTP 8900 (data: file upload/download, authed)             │ (WS server)  │
│ Android ×N ├──────────────────────────────────────────────────────────────►│              │
└────────────┘                                                               └──────────────┘
```

- Control plane: Fleck WebSocket, multi-session + full broadcast (multi-device ready)
- Data plane: minimal HTTP server (hand-written TcpListener, avoids http.sys permission issues), streaming file transfer
- Sync strategy: **WS incremental push + initial full pull** — zero polling, near-zero power cost

## 🚀 Quick Start

### Desktop (Windows 10/11 x64)

Download `Liuzhuan-v1.1.1-windows-x64.zip` from [GitHub Releases](https://github.com/hxroo6/Liuzhuan/releases/latest), extract it, and run `app/Liuzhuan.exe`. No separate .NET installation is needed. To upgrade, exit the old version, replace its `app` directory, and keep the sibling `data` directory containing materials, pairing information, and settings. v1.1.1 updates the desktop app only; keep using the v1.1.0 Android APK without reinstalling it.

Alternatively, build from source:

```bash
cd Liuzhuan
dotnet publish -c Release -r win-x64 --self-contained true -o dist
./dist/Liuzhuan.exe
```

After launch: tray menu → view local IP / generate password. Default ports: control 8899, data 8900.

#### Automatic HEIC conversion (v1.1.1)

Open Settings → **接收 HEIC 自动转换** (Convert received HEIC images):

| Option | Behavior |
|---|---|
| PNG (default) | Lossless PNG output at the original image resolution |
| JPEG | JPEG quality 95, with a white background for transparent areas |
| Off | Receive the original file without conversion |

The setting persists and applies only to future uploads from phones; resend older images to convert them. The converted file is added to the material library and synced to phones, while the original remains in the uploads directory. Other image formats pass through unchanged.

Conversion requires Windows HEIF/HEVC decoding support. If decoding is unavailable or conversion fails, the app displays a warning and keeps the received original. Output is a static primary image; multiple frames, HDR, and all original metadata are not guaranteed to be preserved. Images are actually re-encoded, not merely renamed.

### Android

Build requirements: see [BUILDING.md](BUILDING.md).

1. Build and install the APK; allow notification permission on first run (**requires Android 16+**)
2. Connect tab: **Scan QR** (scan the pairing QR from the PC tray menu — one step) or **Discover** (auto-discovery list, tap to fill) or enter IP/port/password manually
3. Tap Connect → follow the prompt to enable accessibility (clipboard monitor) → the app auto-reconnects
4. Send: copy anywhere and switch back to Liuzhuan for an instant push; long-press selected text → "Liuzhuan" sends directly; "Attach file" on the Send tab streams any file; or share to Liuzhuan
5. (Optional, rooted users) LSPosed → enable the Liuzhuan module → check WeChat etc. → force-stop and reopen the target app → **background copies push instantly** (see "Two modes")

After installing an APK update over an existing installation, turn the Liuzhuan accessibility service off and back on in Android settings.

> Both ends must be on the same LAN (same Wi-Fi, or a hotspot from the PC).
>
> 💡 On the PC: tray menu → "配对二维码" pops the pairing QR; enable "剪贴板监控" to auto-capture copies on the PC too.

### Two modes of "background copy auto-send"

**LSPosed mode (recommended, requires a rooted device)**: enable the Liuzhuan module in LSPosed Manager and check WeChat and other target apps — then **every background copy reaches the PC instantly**, in any app. How it works: the module hooks the system clipboard **write path** (`setPrimaryClip`) and captures content event-driven — it never reads the clipboard back and never polls, bypassing the Android 10+ focus restriction at its root; it is strictly observe-only (never modifies or blocks), so the source app's copy behavior is untouched. After enabling, force-stop the target app and reopen it (running processes are not injected).

**Standard mode (no root needed)**: for security, Android 10+ denies clipboard reads to apps without window focus (enforcement varies by OEM; ColorOS, for example, rejects it outright). Liuzhuan offers three compliant paths:

| Scenario | What to do | Experience |
|---|---|---|
| Copy, then switch back to Liuzhuan | Nothing — the latest clipboard content is pushed the moment you return | Recommended, most natural |
| Restricted apps (e.g. WeChat) | Long-press to select text → "Liuzhuan" in the system menu | Direct send, bypasses the clipboard |
| Browsers / editors | Background copies push automatically (these apps expose text selections to accessibility) | Fully automatic |

The app has built-in pipeline diagnostics (Connect tab log area) showing the real status of every stage: capture → send.

## 🔌 Protocol (summary)

| Type | Direction | Description |
|------|-----------|-------------|
| `hello` / `welcome` | C↔S | Handshake auth (SHA-256 password hash) |
| `sync_text` / `clipboard_push` | C→S | Text material push |
| `list_sync` / `list_data` | C↔S | Recent material list (50 summaries) |
| `item_added` | S→C | New material broadcast (all devices) |
| `get_item` / `item_data` | C↔S | Material detail (full text / file download URL) |
| `heartbeat` / `ack` | C↔S | Heartbeat / ack |
| `GET /file/{id}?auth=` | C→S | File download (HTTP 8900) |
| `POST /upload?name=&auth=` | C→S | File upload (HTTP 8900) |
| UDP 8901 `LIUZHUAN_DISCOVER` | C→S | Device auto-discovery (broadcast reply) |

## 📁 Repository Layout

```
Liuzhuan/
├── Liuzhuan/          # Desktop (C# WPF)
├── AndroidApp/        # Android (Kotlin + Jetpack Compose)
├── docs/              # Development docs
├── scripts/           # End-to-end test scripts (Python)
├── BUILDING.md        # Build guide
└── LICENSE            # MIT
```

## 📄 License

Copyright (c) 2026 Huang Xinrong (黄信荣) · Email 332258260@qq.com · WeChat HXRO_I

Released under the **MIT License**. See [LICENSE](LICENSE).
