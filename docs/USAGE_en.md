# Liuzhuan user guide

[Home](../README_en.md) · [简体中文](USAGE.md) · [Latest download](https://github.com/hxroo6/Liuzhuan/releases/latest)

For v1.9.0. Start with Connection. Material transfers do not require root or a virtual audio driver. The app UI is Chinese; this guide includes the labels to look for.

[Connection](#connect) · [Clipboard](#clipboard) · [Files](#files) · [Library](#library) · [Images](#images) · [Phone microphone](#microphone) · [Migration](#migration) · [Troubleshooting](#troubleshooting)

<a id="connect"></a>

## 1. Installation and connection

### Prepare both devices

- **Windows 10/11 x64:** Download the Windows ZIP from Releases, extract the entire archive and run `app/Liuzhuan.exe`. Runtime data defaults to the adjacent `data` folder; retain it.
- **Android 16+:** Install the APK from Releases. Allow notifications and camera access for scanning as needed; the microphone separately requires recording permission.
- **Network:** Use the same Wi-Fi, or enable the phone hotspot and connect the PC to it. Public Internet and cellular remote connections are not supported.

### Choose a connection method

| Method | Steps | Microphone behavior |
|---|---|---|
| Scan all features | Enable the PC LAN server and open its pairing QR; scan using **扫码连接全部功能** | Attempts audio setup after material authentication, asking for recording permission on first use; missing audio devices do not block materials |
| Discover or enter manually | Tap **搜索电脑** and select a result, or enter the PC IP and connection port; enter the PC password and connect | Ordinary material connections do not start capture; open the waveform and explicitly connect the microphone when needed |
| Connection history | Tap **历史连接** and select a previously authenticated address | Reconnects materials; does not restart recording after disconnection |

PC Settings or the tray menu shows the address, password and pairing QR. Keep passwords and QR codes out of public screenshots.

### Using the phone hotspot

1. Turn on the phone hotspot and connect the PC.
2. In the PC pairing window, choose the **actual PC adapter address on that hotspot** and generate a new QR.
3. Scan again. A connection saved on a previous network may no longer work.

With VPNs, virtual adapters or multiple network interfaces, select the PC address reachable from this phone; a private-looking address alone is not enough. Guest Wi-Fi or client isolation can also block communication.

Default ports: TCP **8899** for material connections, TCP **8900** for files, UDP **8901** for discovery and optional TCP **8902** for audio. When changing the material connection port, the file port is that port plus one; discovery stays on 8901. Allow required app traffic on a trusted private network instead of disabling the entire firewall.

<a id="clipboard"></a>

## 2. Text and clipboard

**Test manual sending first.** Type and send text on the phone's Send page. Its appearance on the PC confirms basic connectivity.

### Standard phone, no root

1. For automatic capture, follow the app prompt to enable its accessibility service and turn on automatic sync in **自动同步** on Connect.
2. Copy text in another app, then return to Liuzhuan to send the latest clipboard content.
3. Alternatively, select text and choose **流转** from the system text-selection menu, or share text/files to Liuzhuan.

Android restricts background clipboard reads. Accessibility may capture selections exposed by some apps, but cannot guarantee background copying from every app. For restricted cases, use sharing, the text-selection action, or copy and return.

### Rooted phone with LSPosed

Enable the Liuzhuan module in LSPosed, select target apps in its scope, then **force-stop and reopen those apps**. This mode observes clipboard writes without polling or changing the original copy result. Root alone does not enable the module or its scope.

### Pauses and exclusions

- PC: Settings → **剪贴板隐私与应用排除**, using process names.
- Phone: **自动同步** on Connect, using package names.
- Pause for 15 minutes, one hour, or until manually resumed. Rules are local to each device.
- With a nonempty exclusion list, automatic capture of unknown-source content is also skipped. Manual sending, sharing and receiving remain available. Exclusions do not erase saved materials or retract content already sent.
- **均衡省电** (balanced power saving) is off by default. It allows idle sleep and may delay lock-screen delivery; battery savings are not quantified.

<a id="files"></a>

## 3. File transfers and storage

**Phone → PC:** Attach a file on Send, or share it to Liuzhuan from another app. The PC registers it on the shelf.

**PC → phone:** Drop a file onto the PC shelf, then tap its entry on Receive to download. New downloads go to `Internal storage / Download / Liuzhuan`. The folder is created on first receipt; older files retain their original locations.

Open **收发** on either device for current-session tasks, progress, speed and results. Swiping down to dismiss the phone transfer panel does not stop tasks.

| Action | Actual behavior |
|---|---|
| Android pause / continue | Closes the transfer and retains its source; continuing **retransfers from the start**. Expired source access requires selecting the file again |
| PC pause / continue | Holds the current connection for up to 90 seconds; timeout or disconnection requires the sender to retransmit |
| Cancel | Stops cancellable transfer stages; submitted text or a file already being registered cannot be retracted |
| Open received folder | **打开接收目录** asks a file manager to browse the saved location; it does not select a new destination |

Android can use a chosen file manager that supports folder browsing, but the destination remains fixed. Unsupported managers produce a prompt to choose another app, not a folder picker. Windows supports a custom file-manager EXE and `{folder}` argument template; this preference stays on that PC.

**Byte-offset resume and task recovery across app restarts are not supported.** Large files stream without loading the whole file into memory; speed depends on the devices and network. PC “sent” and phone “saved” are separate stages; check the receiving device's result.

<a id="library"></a>

## 4. PC shelf and organization

- **Use materials:** Click to copy or drag out. Space previews images/text; Esc closes the preview.
- **Filter:** Combine category, search, group and tag. **常用 ▾ → 保存当前筛选** saves a rule that also matches future materials.
- **Organize:** Ctrl/Shift-select, then **整理…**, F2 or the context menu to edit groups, tags and notes. Batch edits affect checked fields only.
- **Save a selection:** Ctrl+Shift+S creates a separate folder, writes plain text as UTF-8 TXT and handles naming collisions without altering originals. Use migration to move the library and configuration.
- **Relink sources:** More → **素材来源管理与重新定位** checks unavailable sources; choose a replacement or scan a folder and review candidates.
- **Check integrity:** More → **素材内容完整性检查** compares files against a confirmed SHA-256 baseline. It cannot repair files or recover missing originals.

Shortcuts: Ctrl+F search, Ctrl+A select all, Ctrl+C copy, Ctrl+V paste, Del remove, Ctrl+Z undo, Esc clear selection. Removing a library entry differs from deleting the source file; read the action's prompt.

<a id="images"></a>

## 5. HEIC and image processing

Settings → **接收 HEIC 自动转换**: **PNG (default) / JPEG / Off**. Detection uses actual file content, including HEIC incorrectly named `.png`.

The setting persists and affects future phone uploads only; resend older images. Conversion creates a separate library file while retaining the original upload. JPEG uses quality 95 and a white background for transparency.

Windows needs working HEIF/HEVC decoding support. Failure retains the original and displays a warning. Output is a static primary image; multiple frames, HDR and all metadata are not guaranteed. A working thumbnail does not prove another viewer supports the original; renaming an extension does not convert it.

For batch actions, select images → context menu **处理图片**. Choose PNG/JPEG, longest edge and quality, then save reusable presets if desired. Outputs are separate copies, do not upscale and remove capture metadata. Conversion does not guarantee smaller files.

<a id="microphone"></a>

## 6. Use the phone as a PC microphone

This optional experimental feature transports sound. The phone captures it; Liuzhuan forwards it; the dictation app performs recognition.

### First setup

1. Download and install standard VB-CABLE from the [official VB-Audio website](https://vb-audio.com/Cable/), then reboot as instructed. It is not redistributed with Liuzhuan. It has an independent [Donationware license](https://vb-audio.com/Services/licensing.htm); professional/business use requires payment under its terms.
2. Click the PC title waveform or Settings → **手机麦克风**. Refresh devices, choose **CABLE Input** as output and **CABLE Output** as the matching recording endpoint. Do not choose `CABLE In 16ch` or enable **仅诊断收音** (diagnostic only). Choices persist on this PC; configure again when changing devices or PCs.
3. Configure Windows as shown below. Liuzhuan does not change system defaults automatically.
4. On Android, tap **扫码连接全部功能**, scan the regular PC QR and grant recording permission. The normal flow needs no second QR or manual PC forwarding start. If a driver was missing, configure the PC first, then open the phone waveform and tap **连接当前电脑麦克风**.
5. Speak into the phone, check for real waveforms on both devices, then start dictation. A waveform or connection message alone does not prove speech recognition succeeded.

| Setting | Device |
|---|---|
| Windows default playback and communications playback | **Physical speakers / headphones**, such as Realtek speakers |
| Liuzhuan audio output | **CABLE Input** (virtual playback endpoint) |
| Liuzhuan matching recording endpoint | **CABLE Output** (for observing recording sessions) |
| Dictation app input | **CABLE Output**; if the app follows system defaults, also set Windows default recording and communications recording to it |

**Speaker playback and dictation can work together.** Do not set Windows default playback to CABLE Input: this sends system sounds into the cable, may silence physical speakers and can interfere with dictation. “Listen to this device” is unnecessary. Apps with their own output setting should also use physical speakers/headphones.

### Daily controls and settings

- Click the phone's top-left or PC title waveform to open settings. Waveforms reflect actual audio.
- Dismissing or swiping down the phone panel closes its UI; **capture continues**.
- Quiet traffic-saving uses a volume threshold, not voice recognition. Quiet periods keep lightweight heartbeats while the phone continues capturing. Choose sensitive/standard/noise-resistant or disable saving for continuous transmission. Preferences persist and **apply to the next session**.
- Stopping forwarding does not release the phone microphone. Use End on the phone, or end the phone session from the PC, when finished. Authorized sessions have no fixed idle cutoff.
- Disconnecting or restarting does not automatically reopen the microphone. Ordinary material reconnection does not restore capture; reconnect explicitly from the phone microphone page. PC lock/sleep closes the audio service.
- The 60-second local test checks phone capture without sending or saving audio. Diagnostic-only PC mode checks receipt but cannot supply dictation input.
- Experimental automatic following is off by default and requires calibrating dictation start→end and validating a recording session. It does not directly hook the original button click, and compatibility is not guaranteed. Confirm ordinary forwarding works first.

Audio is currently plaintext on a trusted LAN and is not saved as recordings. Full automatic-following behavior, other ROMs, lock screens/calls, weak networks, word boundaries and real battery savings need further validation.

<a id="migration"></a>

## 7. Upgrade, migration and backup

### Upgrade an existing installation

- Exit the PC app, replace the complete `app` folder, and retain the adjacent `data` folder and any custom material directories.
- Install a same-signature APK over the existing phone app, then toggle Liuzhuan's accessibility service **off → on**. Restart LSPosed-scoped target apps. Do not uninstall first; check the package source if signatures differ.

### Move to a new PC

1. On the old PC, Settings → **迁移与备份（配置 + 素材）** → **导出迁移包…**. Wait for success.
2. Transfer the `.liuzhuan.zip` privately and install the current Liuzhuan version on the new PC.
3. Use **导入迁移包…** in the same menu, review the contents, import, then exit and reopen Liuzhuan.
4. Generate a fresh pairing QR on the new PC and scan again. Install HEIC codecs and virtual audio drivers separately if needed.

Migration includes referenced originals, text, favorites, thumbnails, organization, saved filters, image presets and portable settings. It does not copy the entire disk, old window position or data path, the local file-manager EXE preference, audio device IDs, or Android settings.

Import verifies files and creates a separate library, activated on restart; it does not merge with or overwrite the previous library. A `config.before-import-*.json` is retained in the default data directory. To roll back, exit Liuzhuan, back up the current `config.json`, replace it with the appropriate backup, and retain the original material and `imports` directories.

**Migration archives are unencrypted ZIPs containing materials and pairing credentials. Never publish them in a repository or public share.** Missing originals are reported; cancellation or failed validation does not activate a new library. Microphone device preferences do not migrate.

<a id="troubleshooting"></a>

## 8. Troubleshooting

| Symptom | Check first |
|---|---|
| Connection times out after scanning | PC server enabled, current adapter in the QR, network changes, private-network firewall, VPN/router isolation; the same Wi-Fi name does not guarantee reachability |
| Discovery finds nothing but manual connection works | UDP discovery may be blocked; scan or enter the current address |
| Cannot connect while phone hosts a hotspot | Connect the PC first, regenerate the QR with its hotspot adapter address, and replace old Wi-Fi history |
| Copied text does not arrive automatically | Test manual sending, then check accessibility, automatic sync and exclusions; reliable background copying needs the LSPosed module and scope |
| HEIC thumbnail works but original does not open | Check actual format and Windows decoder support; enable conversion and resend instead of renaming the extension |
| Download is missing from the gallery | Browse `Download/Liuzhuan` with **打开接收目录**; gallery indexing depends on the system |
| Phone waveform works, PC waveform does not | Check audio connection, pause/end state and quiet threshold; inspect diagnostics or explicitly reconnect the microphone |
| PC waveform works, dictation receives nothing | Disable diagnostic-only mode, verify CABLE Input/Output direction and actual recording input; receipt alone does not prove output or recognition |
| Speakers went silent after installing the driver | Restore physical speakers/headphones as Windows default playback and communications playback; keep Liuzhuan output on CABLE Input |
| Microphone remains in use after closing its panel | The panel only closes the UI; reopen it and tap End |
| Want to resume a file from its last byte after disconnecting | Currently unsupported; retransmit. Pause is not persistent byte-offset resume |

For a [GitHub issue](https://github.com/hxroo6/Liuzhuan/issues), include both app versions, OS versions, reproduction steps and sanitized diagnostics. Remove QR codes, passwords, LAN addresses, device identifiers, personal text and paths before sharing. Do not upload recordings or the entire data folder.

[Known issues and validation limits](KNOWN_ISSUES.md) · [Release notes](releases/v1.9.0.md) · [Build guide](../BUILDING.md)
