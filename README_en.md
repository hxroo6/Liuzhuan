# Liuzhuan (流转)

> Cross-app material staging hub: drag-and-drop stash on desktop, one-copy instant push from Android, bidirectional file transfer, multi-device realtime sync

[![Build](https://github.com/hxroo6/Liuzhuan/actions/workflows/build.yml/badge.svg)](https://github.com/hxroo6/Liuzhuan/actions/workflows/build.yml)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
English · [中文](README.md)

Liuzhuan is a **pure LAN** material transfer tool. The desktop side is a floating side panel on the edge of your screen (drag-and-drop stash, use-and-drag), and the Android side pushes clipboard text and shared files from your phone to the PC in realtime — and pulls PC materials back to the phone whenever you need them.

**No cloud servers involved.** All data flows only inside your own local network.

## Latest download: v1.8.1

[Release notes](https://github.com/hxroo6/Liuzhuan/releases/tag/v1.8.1) · [Android v1.8.1 APK](https://github.com/hxroo6/Liuzhuan/releases/download/v1.8.1/Liuzhuan-v1.8.1-android.apk) · [PC v1.8.0 Windows x64](https://github.com/hxroo6/Liuzhuan/releases/download/v1.8.1/Liuzhuan-v1.8.0-windows-x64.zip) · [SHA-256 checksums](https://github.com/hxroo6/Liuzhuan/releases/download/v1.8.1/Liuzhuan-v1.8.1-SHA256SUMS.txt)

This release includes the Android startup fix and previously local improvements: batch organization/export, groups and tags, saved filters, source repair, clipboard privacy, image processing, and delivery receipts. The PC remains at v1.8.0 and is compatible with Android v1.8.1. To upgrade the PC, exit Liuzhuan, replace the `app` folder, and keep the adjacent `data` folder. The program ZIP contains no personal settings or materials.

## Transfer experience: Android v1.8.4 / PC v1.8.1 (local builds)

- Android transfer tasks now use a full-screen view with status filters. The desktop transfer window is enlarged to 840 × 680 and remains resizable.
- All new Android downloads, including images, videos and audio, go to **Internal storage / Download / Liuzhuan**. "Open receive folder" opens that location in a system file manager, without a folder-selection or permission picker. You can configure a compatible directory browser; the save location stays fixed. The folder is created on the first download. Existing files stay in their original locations, which remain accessible from their task records. Windows defaults to Explorer and supports a custom EXE with a `{folder}` argument template. The executable preference stays on the current PC and is excluded from migration packages.
- File tasks offer pause, continue and cancel. Android pause closes I/O and retains the task source; continuing **restarts the entire transfer**. Desktop pause retains the current HTTP connection for up to 90 seconds; connection loss requires retrying from the sender. **Byte-offset resume and task recovery across process restarts are not supported.** Submitted text and file registration cannot be recalled.
- Toggle accessibility off and back on after APK replacement. No Android device is currently attached, so directory navigation and third-party file manager compatibility still require device validation. Unsupported browsers show a message instead of falling back to a folder picker. See the [delivery record](docs/performance/TRANSFER_CONTROLS_2026-10-11.md) and [M36 directory correction](docs/performance/RECEIVE_DIRECTORY_2026-10-11.md) (Chinese). Public downloads remain v1.8.1.

## Android v1.8.2: performance and power improvements (local build)

Implementation details, 198 local regression checks and APK validation are recorded in the [optimization delivery report](docs/performance/ANDROID_POWER_OPTIMIZATION_2026-10-11.md) (Chinese).

- **Balanced power** is available under Automatic sync on the connection page. It is off by default to retain instant delivery; when enabled, idle periods can sleep and work receives temporary protection. Delivery while locked may be slower.
- Retry backoff survives consecutive failures, keep-alive acquisition is idempotent, and missing LAN connectivity waits for network changes without requiring Internet validation. A process-owned connection survives Activity recreation.
- Image hooks use bounded background work and a hard 512 KiB read limit. Byte broadcasts avoid Base64 expansion, while the receiver remains compatible with older hooks. Failed image retries use cache files.
- HTTP clients share connection resources. At most two file transfers run concurrently, with cancellation available in the task dialog. Off-screen thumbnails cancel their calls and concurrent requests for one URL are merged.
- Progress is throttled before list allocation, material deltas use an ordered index, hidden pages avoid unnecessary subscriptions, and settings share one DataStore instance.
- The foreground service declares `connectedDevice` for LAN device interaction and handles timeouts. Privacy checks, receipts, loop prevention, and process-owned background sending are retained.
- Device power consumption and locked-screen latency are not yet measured. After an overwrite install, toggle accessibility off/on and restart source apps in the LSPosed scope to load the updated image hook. Public downloads above remain v1.8.1.

## Android v1.8.1: startup compatibility fix

- On the tested Android 16 phone with LSPosed Irena 1.9.2, injecting a debuggable app crashed before the Activity started, leaving an apparently frozen splash screen. A production signing certificate does not disable the debuggable flag.
- The new `distribution` variant shares the Debug source and existing signing configuration, disables debugging, and keeps code minification off. Local delivery uses `Liuzhuan-M33-signed.apk`. Debug remains available for development; CI checks and uploads Distribution to prevent accidental delivery of debuggable APKs.
- Cold startup, PC discovery, switching among all three pages, the PC material snapshot, and manual text registration receipts were verified on the original phone and framework. Two copies from WeChat while Liuzhuan was in the background completed Hook→broadcast→PC registration receipt in about 42 ms and 63 ms, with duplicate broadcasts suppressed. These are results from this device, not guarantees for every ROM or network. Android v1.8.1 remains compatible with PC v1.8.0; no data migration is required. Background copying still requires the enabled LSPosed module and appropriate scopes.

Install the same-certificate APK over the existing app, then toggle the Liuzhuan accessibility service off and on. CI builds without the private keystore use a test certificate and cannot replace locally signed delivery APKs.

## v1.8.0: delivery receipts, file verification, and image presets (local build)

- Automatic clipboard text waits for a receipt confirming PC material registration. Stable request IDs and bounded retries distinguish pending, registered, and unconfirmed deliveries on compatible builds. Privacy rules, event age, and the target PC constrain retries; legacy PCs receive only one submission and report unconfirmed delivery instead of blind retransmission.
- Open **更多 → 素材内容完整性检查**, or choose **检查文件内容** from a file material's context menu, to compute SHA-256 in the background. Results distinguish an absent baseline, matching content, changed content, a missing source, and a read failure. Checking is opt-in and cancellable. Recording the first baseline or accepting externally edited content requires confirmation; source files are never modified.
- **处理图片 → 常用预设** saves named format, longest-edge, and JPEG-quality settings. Select to fill in parameters, update, rename, or delete up to 20 presets. Selecting a preset does not start processing; output remains an independent copy.
- Verification baselines and image presets travel with migration archives. Older libraries and archives remain readable. Source changes and stale background jobs cannot apply old results to a new path.

Receipts and deduplication are bounded in-memory state, not a permanent exactly-once guarantee across process restarts. Registered means accepted by the PC material store, not a durable disk transaction. Verification detects changes relative to an explicitly accepted baseline; it cannot restore lost or damaged content. Real-phone background copying, mixed-DPI dragging, and different networks require separate acceptance. After installing the APK over an existing version, toggle the Liuzhuan accessibility service off and on.

## v1.7.0: source repair, clipboard privacy, and image actions (local build)

- **更多 → 素材来源管理与重新定位** keeps missing-file records, favorites, tags, and notes. Choose a replacement individually, or scan a specific folder in the background and review candidates before applying them. Ambiguous matches are not bound automatically; scans are cancellable. Records already removed by older builds cannot be reconstructed automatically.
- **设置 → 剪贴板隐私与应用排除** excludes desktop processes (for example `WeChat.exe`); the phone's connection-page automatic-sync card excludes Android package IDs. Pause for 15 minutes or one hour, or resume manually. With exclusions configured, unknown automatic sources are skipped. Changing phone privacy rules invalidates captured but unsent automatic events. Explicit sending, sharing, and receiving remain available; existing materials are retained. This does not promise to identify every password.
- Select images and choose **处理图片** from their context menu to create PNG/JPEG copies, constrain the longest edge without upscaling, and choose JPEG quality. JPEG transparency is composited onto white. Each batch has its own output folder with actual dimensions, byte sizes, and per-item errors; originals remain untouched. Output can be larger than the source.
- Image actions produce only the primary/first frame and strip photographic metadata. HEIC/HEIF and other formats depend on installed Windows decoders; animation and vectors are not preserved. Cancellation takes effect at controllable stages after any current native decode/encode completes.
- Desktop app exclusions travel with migration archives, while temporary pause deadlines do not. Text-file migration retains the complete source file. Deferred thumbnails validate the source and load generation so old callbacks cannot replace a repaired image.

Update both apps; after replacing the APK, turn the Liuzhuan accessibility service off and on again. Automated and offscreen UI checks cover this release; real phone background copying, mixed-DPI dragging, and different network environments still need device acceptance. See the [product roadmap](docs/PRODUCT_ROADMAP.md) for official competitor references and future work.

## v1.6.0: saved filters and stable synchronization (local build)

- Open **常用 ▾ → 保存当前筛选** to save a named combination of category, group, tag, and search. Open it with one click later; newly matching materials appear automatically. Update criteria, rename, or remove rules, with a limit of 30.
- Migration archives restore saved filters and still support older archives. Removing a rule retains all materials.
- Desktop discovery always listens on UDP 8901 and advertises the actual connection port, so changing the desktop port no longer breaks phone discovery. QR pairing and file transfers retain their existing behavior.
- Android tracks sequences across additions, deletions, and clears. A gap requests one snapshot, reconnecting resets the baseline, and undo sorts restored materials by their original time. Empty desktop snapshots retain the correct global sequence.

See [competitor references and product roadmap](docs/PRODUCT_ROADMAP.md) for this round's rationale and future directions. Update both apps; after replacing the APK, turn the Liuzhuan accessibility service off and on again. Devices must still be reachable on the LAN. Phone discovery, real cross-monitor dragging, and Android frame rates need device acceptance.

## v1.5.0: groups, tags, and notes (local build)

- Select materials and click **整理…**, press **F2**, or use the context menu. Each material can have one group, multiple tags, and a note. Enter a new name to create a group; leave it blank for ungrouped materials.
- Batch edits apply only checked fields and retain all other information. Tags can be added, replaced, or removed. Ctrl+Enter saves; cancelling leaves materials unchanged.
- Group and tag filters combine with categories and search. Search also matches notes. Cards show an organization summary, with details available on hover.
- Removing current results affects only visible non-favorites and can be undone. The corresponding action in Favorites only removes the visible results from favorites.
- Organization information is stored with the library and included in Migration and Backup. Older libraries and archives remain supported. Use v1.5.0 or newer on both PCs to retain this information when migrating.

Groups allow up to 48 characters. Each material allows up to 20 tags of 32 characters each and a note of 4000 characters. Groups and tags match without case sensitivity; separate tags with commas or semicolons. Group lists come from current materials, so empty groups are not retained separately. Organization is available on desktop; phones continue syncing the original material content. Ordinary Save produces usable files; export a migration archive to reuse organization information on another PC.

## v1.4.0: batch organization and saving a selection (local build)

- Ctrl/Shift-select materials to reveal copy, favorite, save, and remove actions. Select all, invert, and clear operate within the current category and search results.
- Drag multiple files together, or combine selected text in list order. Mixed files and text can be saved together to a folder.
- **Save selected materials** creates a separate folder with collision-safe names. Materials with source paths preserve complete original bytes, while plain text becomes UTF-8 TXT. Includes progress, cancellation, and opening the result folder; originals are retained.
- Fix undo not restoring materials on connected phones, and partial/category clears incorrectly clearing unrelated phone materials or favorites.

Shortcuts: Ctrl+A select all, Ctrl+C copy, Ctrl+Shift+S save, Del remove, Ctrl+Z undo, and Esc clear selection. Save is for organizing or delivering a selection; use Migration and Backup when moving the entire library and configuration. Failed/cancelled saves attempt to remove their output; locked files may leave remnants and are reported as incomplete. End-to-end device behavior still requires verification.

## v1.3.1: stable dragging and edge snapping

- Fix obsolete queued animations moving content out of view after dragging or rapid expand/collapse, leaving only a dark panel.
- Suspend auto-collapse and edge detection while dragging. Keep the panel open after snapping right; collapse after the pointer leaves.
- Use the window's monitor and scale for snapping. Moving the pointer between monitors no longer relocates the panel.
- All 43 desktop regression checks passed. Real multi-monitor and mixed-DPI behavior still needs device verification.
- Desktop-only update; Android remains at v1.2.0.
## v1.3.0: migration and backup

Open **迁移与备份（配置 + 素材）** in Settings. Export a `.liuzhuan.zip` on the old PC, import it using a migration-capable build on the new PC, then exit and reopen Liuzhuan.

- Includes referenced original files, text, favorites, thumbnails, HEIC/clipboard preferences, LAN port/password, and startup preference.
- Verifies file hashes and remaps local paths; does not reuse the old data-directory path or window position.
- Creates a separate library and switches on next launch; no merging or overwriting of the old library. The previous config pointer is saved as `data/config.before-import-*.json` under the default data directory.
- Missing originals are reported. Cancellation or validation failure does not activate the new library. Keep the package private: it includes content and pairing credentials.
- Scan a new QR code on the new PC. Only files referenced by the current library are packaged, not the entire disk or all historical uploads.

### Moving to another PC and rolling back
1. Upgrade the old PC to v1.3.0 or newer and export from Settings. Export uses the material snapshot taken when you start; wait for success before copying the archive.
2. Extract the latest release on the new PC, run `app/Liuzhuan.exe`, select the migration archive from the same menu, and review its contents.
3. Exit and reopen Liuzhuan after import to apply the settings and new library, then pair your phone using a fresh QR code.
4. To restore the old library, exit Liuzhuan, back up the default `data/config.json`, copy the appropriate `config.before-import-*.json` over it, and reopen. Retain the original material directory and all `imports` directories.

Migration archives are unencrypted ZIPs containing materials and pairing credentials; transfer them only between your own devices. Public program downloads contain neither your configuration nor your materials. Windows HEIC codecs must be available separately on the new PC.

The settings menu also now uses a consistent dark background, readable text, and clear checkmarks, fixing the low-contrast HEIC submenu.

## ✨ Features

### New in v1.2.0 (includes M27–M29)

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

Download `Liuzhuan-v1.3.1-windows-x64.zip` from [GitHub Releases](https://github.com/hxroo6/Liuzhuan/releases/latest), extract it, and run `app/Liuzhuan.exe`. No separate .NET installation is needed. To upgrade, exit the old version, replace its `app` directory, and keep the sibling `data` directory containing materials, pairing information, and settings. This release updates Windows only. Keep using the [v1.2.0 Android APK](https://github.com/hxroo6/Liuzhuan/releases/download/v1.2.0/Liuzhuan-v1.2.0-android.apk). Scan a fresh QR code after moving to a new PC. If reinstalling the APK, turn its accessibility service off and back on.

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

Copyright (c) 2026 hxroo6

Released under the **MIT License**. See [LICENSE](LICENSE).
