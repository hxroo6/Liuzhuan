# Liuzhuan user guide

[Home](../README_en.md) · [简体中文](USAGE.md) · [Latest download](https://github.com/hxroo6/Liuzhuan/releases/latest)

Covers the released v1.9.0 and current **v1.20.0 / M57 source version**. M57 features require building and updating both peers; existing v1.9.0 installers do not include the new controls. See [what's new](releases/v1.20.0.md) and [building](../BUILDING.md). Start with Connection. Material transfers do not require root or a virtual audio driver. The app UI is Chinese; this guide includes the labels to look for.

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

M55 adds task search by name, direction or displayed status, plus send/receive direction filters on both devices. **进行中** means active, including acknowledgement or stop in progress; **待处理** includes paused, failed, expired or waiting recovery; **已结束** includes success and cancellation, counted separately. Filtering does not retry or cancel anything. Searches cover retained local history, not full message bodies.

On PC, open **更多 → 查找内容相同的文件**, then click **开始检查** to inspect file-backed library entries. Size is only a prefilter; SHA-256 confirms matching bytes across different paths. Select a group to review its sources and open their folders. Missing, locked or changed files are reported separately, and scans can be cancelled. The tool does not detect visual similarity, update baselines, merge indices or delete originals. Logical duplicate bytes do not measure actual reclaimable disk space. Android has no remote duplicate-scan action in this release.

**Phone → PC:** Attach a file on Send, or share it to Liuzhuan from another app. The PC registers it on the shelf.

**PC → phone:** Drop a file onto the PC shelf, then tap its entry on Receive to download. New downloads go to `Internal storage / Download / Liuzhuan`. The folder is created on first receipt; older files retain their original locations.

Open **收发** on either device for tasks, local recovery records, progress, speed and results. Swiping down to dismiss the phone transfer panel does not stop tasks.

**M57 transfer policy:** Expand the policy controls in Transfers, select balanced/send-first/receive-first, and set aggregate local upload/download limits separately (0 means unlimited; up to 100 MiB/s). Optional audio yielding caps each direction at 1 MiB/s while Liuzhuan is forwarding the microphone. Preferences persist locally and apply at data-block boundaries to active and queued work. There are still at most two active transfers; priority does not remove pause, cancellation, verification or starvation protection.

| Action | Actual behavior |
|---|---|
| Android pause / continue | M48 closes current IO and retains committed bytes; continuing resumes at the verified offset. Older peers retransmit from the start |
| PC pause / continue | M48 persists the pause and notifies the phone; it does not keep an idle socket open. Legacy transfers retain the 90-second limit |
| Cancel | Stops cancellable transfer stages; submitted text or a file already being registered cannot be retracted |
| Open received folder | **打开接收目录** asks a file manager to browse the saved location; it does not select a new destination |

Android can use a chosen file manager that supports folder browsing, but the destination remains fixed. Unsupported managers produce a prompt to choose another app, not a folder picker. Windows supports a custom file-manager EXE and `{folder}` argument template; this preference stays on that PC.

**M48 / v1.11.0 requires both peers to be updated.** New tasks support verified offsets, complete SHA-256 and restart recovery. Partial downloads remain private until verified and published to the fixed receive folder. PC “sent” completes after the phone confirms saving. See the recovery boundaries below.


### Reliable transfers and text outbox (M48 / v1.11.0)

Update both peers; negotiation is automatic and requires no second QR scan. Existing WS/file/discovery/audio ports remain unchanged. The new transfer endpoint uses the control port +4 (8903 by default). If pairing works but file requests time out, check the Liuzhuan program rule on the PC's private network.

- **Files:** Complete a private phone-side source snapshot before the task becomes recoverable. A temporary source whose access expires during preparation may need selecting again. New tasks verify 1 MiB blocks and the complete SHA-256. Pause retains committed bytes; cancel clears staging, preserving originals and already saved files.
- **Restart:** Open the app and authenticate to the original PC to resume waiting tasks. Manually paused tasks remain paused. Address, pairing credentials and PC identity must match; changing an address does not automatically rebind an existing task. Force-stopped Android apps do not run by themselves, and recovery never restarts the microphone.
- **Text:** Saving the outbox and waiting for a receipt are distinct stages. Automatic text expires after five minutes; manual outbox entries last up to seven days and remain available for explicit retry/cancel after three automatic attempts. Target and privacy changes can invalidate old automatic events.
- **Retention:** PC task/receipt records have an eight-day window. A previously created remote task that no longer exists is marked expired instead of being blindly recreated. Check the receiver before sending a new task. Changed sources, bad checksums and insufficient space never produce a completion receipt.
- **Space:** Private send snapshots require additional space. Download staging and final publication briefly require two copies. Preparation and saving are explicit phases; speed depends on storage and the network.

Older peers retain the legacy transfer behavior, and pre-upgrade in-memory tasks cannot be reconstructed. Recovery records stay on their current device and are excluded from migration archives. See [implementation and test boundaries](performance/M48_RECOVERY_2026-10-18.md).

<a id="library"></a>

## 4. PC shelf and organization

### Project packages (M56; phone controls added in M57)

Select PC materials, then open **更多 → 项目素材包：导出所选 / 合入**. Export a project ZIP containing selected text, original files, favorites, groups, tags, notes and material dates. It excludes PC configuration, passwords, device identity and absolute source paths. On the receiving PC, choose the ZIP, click **校验并预览**, review the counts, then **合入当前库** to merge into the existing library.

Every payload is verified before index changes. Reimporting the same package skips registered entries; identical inline text keeps the existing local material and its organization. Files from different packages are not automatically merged by content. The original library and configuration remain in place.

Limits: 1,000 materials, a 16MiB manifest, 64GiB of originals, and 262,144 characters per inline body. Oversized complete text is rejected. File-backed text keeps its complete original plus at most 5,000 characters of existing card preview.

Verification prepares originals in the current library's `project-imports` directory and consumes disk space. Prepared files remain after cancellation or an uncertain save so retries can reuse them. In M57, open **项目暂存空间** from the PC storage window, scan references and blockers, then explicitly recycle eligible unreferenced originals. Trash still consumes space and can be restored without overwriting files; there is no automatic cleanup. Do not delete directories still referenced by library items. Cancellation is available before commit; once saving starts, wait for confirmation. Success requires a durable local index. After an uncertain save, preview and retry in the current session. Changing the package or library after preview requires another review.

M57 phone controls can request a package from up to 100 selected, synchronized materials on the PC, then download its ZIP using the reliable transfer path. To import, upload and register a project ZIP on the current PC first, request its preview, review counts, then explicitly merge. Resolve pending phone drafts first: packaging uses values already saved on the PC. Older PCs do not expose this capability. PC jobs are limited to four and 15 minutes, remain in the current process only, and do not survive PC restart. Closing the phone panel stops status queries, not an already submitted PC operation. Once the ZIP is ready, its file download retains normal task recovery.

### Android saved searches (M56)

Combine search text, type, favorite, group and tag in Receive, then tap **保存此筛选**. Use **常用搜索** to apply, update with current conditions, or delete. Each authenticated PC/library has up to 10 named queries; names allow 1–40 characters and search text up to 256. Only explicit saves write preferences; typing is not recorded as search history. Preferences use this phone's encrypted local records, work offline and after restart, and are not synchronized to PC or included in its migration archive.

Clearing the offline index retains saved searches. Explicitly removing an entire offline library also removes its saved searches. Unreadable records are retained and disable only preference writes. Search input uses a 250ms trailing delay; newer conditions can cancel obsolete reads without cancelling metadata writes already in progress.

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

The PC microphone settings show three route cards: physical playback, Liuzhuan output and dictation input. They check daily, media and communications defaults on opening, every 10 seconds while visible, and when clicking **重新体检**. Checks also work while forwarding without interrupting it.

If communications playback points to a virtual endpoint, use **打开播放设备 · 修复外放** to set physical speakers/headphones as default playback and communications playback. **打开录音设备 · 检查输入** opens recording devices; change global recording defaults only if the dictation app follows them. Read failures remain unknown, and missing saved endpoints never fall back to speakers. Checks do not change defaults, install drivers or start capture, and cannot prove successful dictation. M57 also lets the phone explicitly check and repair Liuzhuan's virtual route, as described below.

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

### M57: check and repair PC audio from the phone

1. Connect to the PC material service first; no rescan is needed. Open the phone waveform and find **电脑音频** below it.
2. Tap **仅检查 · 不改配置** to inspect status. This does not save device preferences or change Windows defaults or volume; an existing microphone session may keep running.
3. For repair, end the current microphone session, then tap **检查并修复电脑音频**. The PC reconfigures Liuzhuan and starts its standby service only when one standard VB-CABLE pair can be verified safely. Missing drivers, incomplete identity, multiple pairs or busy forwarding produce a message instead of guessing or interrupting an active stream.
4. Keep the phone foregrounded with the panel open. After repair it requests a fresh invitation through the existing connection and follows the recording-permission flow; no second audio QR is needed. Permission must still be granted explicitly. Closing the panel or backgrounding the app never silently starts capture.

In Energy-saving connection mode, an explicit repair may temporarily open the microphone. Ending it retains the energy-saving preference. Repair affects Liuzhuan's virtual route and service only: it never writes Windows playback/recording defaults, speaker mute or volume, installs a driver, or recreates a missing Realtek sound card. Pairing checks driver identity and shared hardware ownership rather than the word “CABLE”; 16-channel devices are excluded.

On 2026-10-25 the updated APK was installed and the user confirmed phone repair worked. Inspection preserved the saved preferences, and repair preserved Windows defaults plus active-endpoint mute/volume. This does not establish dictation accuracy, every audio use case or battery savings.

### M57: save working settings and review changes

1. Forward speech and confirm that the virtual output actually reads non-silent audio. Liuzhuan automatically retains this verified virtual-device selection; received audio alone does not prove speaker playback or recognition.
2. Play sound through the PC speakers and complete a dictation test. When both work, click **外放和听写正常 · 保存配置** in microphone settings. This saves Liuzhuan selections and records the six Windows playback/recording defaults plus speaker volume/mute as a comparison baseline. It saves no audio or recognized text.
3. Device, default-endpoint and volume events trigger checks for suspicious configurations. An ordinary switch to headphones is not automatically treated as a failure. Review warnings using the Windows sound-setting links.
4. If Liuzhuan's own selection was changed incorrectly, click **恢复流转设备选择**. Recovery stops the audio service and restores exact saved device IDs only. It never writes Windows defaults, volume, mute, recording permission or automatic-follow settings, and never reopens the microphone. Review and explicitly enable the service afterward.

Missing devices or IDs changed by driver reinstallation are not replaced by same-name endpoints. Recover the driver or explicitly select and verify new devices. If Realtek speakers disappear from Windows entirely, route recovery cannot recreate a sound card. Save work, restart, and check Device Manager if they remain missing. On 2026-10-25 the user reported restored playback after restarting; the cause remains unconfirmed. Protection profiles stay on this PC and are excluded from migration archives.

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

### M57: index history and safe library switching

Open PC Settings → **素材库恢复与切换…**. Review the current library path and retained index versions. Compare an old version or schedule switching to an existing library. Plans run on the next launch; the current session keeps its original library. Cancel the plan before exit if needed. Do not manually move the active library directory.

History preservation is attempted at most once per five minutes, retaining up to ten versions, 128 MiB total and 64 MiB per version. It is not a complete backup. Restore preserves the current index and verifies the chosen fingerprint first; it restores entries and organization, not deleted originals, Windows settings, device identity or transfer receipts. Conflicting migration plans are refused. Failed validation preserves data rather than forcing a switch.

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
| Want to resume a file from its last byte after disconnecting | Update both peers to M48 and continue the original task in Transfers; older peers or expired tasks require retransmission |

For a [GitHub issue](https://github.com/hxroo6/Liuzhuan/issues), include both app versions, OS versions, reproduction steps and sanitized diagnostics. Remove QR codes, passwords, LAN addresses, device identifiers, personal text and paths before sharing. Do not upload recordings or the entire data folder.

[Known issues and validation limits](KNOWN_ISSUES.md) · [Release notes](releases/v1.9.0.md) · [Build guide](../BUILDING.md)


## Rules, watched folders and quick collection (v1.10.0)

Open **Settings → Automatic organization, folder subscriptions and quick collection** on Windows, or **Collect** in the Android header. Update both clients.

- **Rules:** match type, collection origin, device-name and material-name keywords. The computer maintains one shared rule book; Android can load, edit, preview and save it over the authenticated connection. Preview examines the latest 50 items without modifying them. Saving affects future first-time collection only, fills empty groups and appends tags while preserving manual organization. Reload after changes from the other client. Limits: 30 rules and 20 tags per material. Current Android builds browse the complete PC library in pages, search names/groups/tags/notes, and support single/batch organization with live metadata updates. Only legacy-PC compatibility retains a 50-summary mirror; the rule-preview budget is separate from library size.
- **Folder subscriptions:** explicitly select a source folder. Existing files are baselined and skipped; stable new/changed direct children are collected, without recursion. Limits:10 folders,5000 files each. Temporary files are excluded. Windows uses filesystem events; Android uses persisted SAF read access and provider notifications, with foreground/reconnect/manual scans as fallbacks. Some providers cannot notify changes or report reliable size/timestamps. Unknown-length files can be sent manually.
- **Fixed receiving path:** the Android folder picker grants access to a **source**, never a receiving destination. Received files remain in`Download/Liuzhuan`. Android subscriptions bind to the computer selected when authorization was granted; changing its address/computer requires removing and adding the subscription again.
- **Failure and migration:** failed/cancelled Android versions are not repeatedly auto-submitted; retry explicitly in Transfers. M48 can restore tasks after their private send snapshot has been completed; temporary sources still being prepared may need selecting again. Stopping a subscription preserves originals and collected files. Migration carries rules, but excludes local source paths, SAF grants and target bindings.
- **Quick collection:** collect the current Windows clipboard from the app, or opt in to global`Ctrl+Alt+V` (off by default). Android offers foreground clipboard preview/confirmation, file selection and existing system sharing. No Windows shell context-menu registration is added.
- **Scanner:** a dedicated portrait camera page uses the shared dark-teal visual language, real decode-frame corners, back/flashlight controls and permission/camera retry. Pairing still connects materials and the phone microphone through one QR; recording needs the existing explicit permission flow. Large-screen Android16 orientation overrides require separate tablet testing.

<a id="storage"></a>

## M51: storage, history and large libraries (local v1.14.0)

Open PC Settings → Storage, retention and cleanup preview. Defaults protect favorites, the newest 500 entries and the last 30 days. Set your own parameters and explicitly scan. Counts are logical file sizes, not physical allocation. Changing parameters invalidates the preview; opening or saving a policy never schedules deletion.

The left list removes selected index entries while preserving file originals. Plain-text contents are part of their index entry and are removed with it; Undo is limited to 50 steps in the current process, so export first for long-term retention. The right list moves selected unreferenced managed files into library-local trash, at most 200 per batch. Trash still uses disk space. Restore the latest batch without overwriting existing names. Expand the trash details, review them, then separately confirm permanent deletion to free space.

Live/undo paths, resumable references, HEIC originals, linked paths and files created within the last day are protected. Active, paused or waiting transfers block physical recycling. Damaged records, incomplete scans, drive roots and UNC libraries fail closed. Scanning is limited to 100,000 files in known managed directories, without recursively cleaning user subdirectories. Changed metadata or library versions require a fresh preview. Recovery intents help interrupted moves; they are not a filesystem transaction guarantee. External originals are never deleted.

Retention parameters migrate as an optional field. Trash, pending tasks/receipts and hidden-history IDs do not. Old packages use defaults. Migration still includes indexed payloads only; separately back up unindexed HEIC originals and trash when needed.

Android Transfers → Storage and history reads cache/recovery usage, clears the 8MB redownloadable thumbnail memory cache and previews completed history by 7/30/90 days or all. Active, paused, retryable and pending tasks, recovery descriptors, receipts and downloaded files remain intact. Hidden IDs use existing encrypted local storage, capped at 20,000; background save failures are logged. Completed descriptors cannot resurrect hidden rows, but authoritative active recovery tasks always remain visible.

Mobile browsing retains at most 1,000 summaries, including live deltas. Further pages release older rows and show the current range. Return to newest or refresh starts the latest window. Full-library search stays on the PC and originals download on demand. Legacy lists stay compatible; this is not an offline complete library.

See the [M51 validation record](performance/M51_STORAGE_SCALE_2026-10-18.md) for scale measurements and remaining physical-device checks.

## M50: microphone recovery and energy modes (local v1.13.0)

Open the phone waveform → preferences → **Instant standby / Energy-saving connection**. Instant standby keeps capture ready. Energy-saving ends audio capture, the audio socket and its CPU lock while preserving material sync. Subsequent scans do not start capture or request recording permission. To dictate, explicitly open the current PC microphone while the phone app is foregrounded; ending this temporary session preserves the energy-saving preference. Pause/quiet traffic-saving alone still capture audio.

After an interruption, reconnect from the phone microphone page without rescanning when materials are connected. Permission failures link to system app settings; remove system mute/recording conflicts before retrying. On the PC, Retry original devices reuses exact saved IDs after driver recovery or lock/sleep. Missing endpoints never fall back to speakers.

Experimental follow stays off by default. Observe → calibrate → complete one dictation on/off cycle within 45 seconds → enable only after compatibility is verified. Unknown ownership or an incomplete cycle requires manual forwarding. Manual stop, phone pause and disconnection revoke automatic following. A permanently open recording stream may not expose button state.

Capture/read/send counts and audio CPU-lock status are diagnostic work counters, not battery measurements; no audio is saved. Energy-saving does not promise background microphone restart. Battery, speech boundaries and complete ASR compatibility need dedicated device acceptance.

## M49: device history and complete mobile library (local v1.12.0)

Update both devices. Phone connection history now groups successful connections by the PC installation ID and supports local aliases. Reconnecting from history performs bounded discovery and verifies a fresh proof using the saved pairing credential before following a new address. PC Settings → “电脑名称与设备身份” changes the friendly name without changing its ID. Discovery restrictions, changed credentials or legacy peers may still require rescanning.

Receive exposes the full PC index in pages of 100; “加载更多素材” loads older entries. Search runs across the server index and combines type, favorite, group and tag filters. “整理” edits a material's favorite, group, tags and note. Saving commits to the PC and updates other connected phones. A revision conflict preserves your draft and offers an explicit reload; it never silently overwrites another device's edit.

Original files are downloaded only when requested, always to Download/Liuzhuan. Source availability reflects the PC's last check. M49 retained in-process summaries only; M52 adds offline browsing and editing as described below. See the [M49 implementation record](performance/M49_IDENTITY_LIBRARY_2026-10-18.md) for that version's validation limits.

## M52: mobile batch organization and offline use (local v1.15.0)

Update both devices. In Receive, select “批量整理”, check materials or select the current page (up to 100 items), then edit favorites, groups or tags. Tag actions support append, remove and replace. Unselected fields and notes stay unchanged. Changes are persisted on the phone before synchronization; “待同步” shows drafts and unresolved results.

While connected to the original PC, explicitly confirm “保存离线索引” to save a complete encrypted index across restarts. Limits are 50,000 entries, 64 MB per index and five PC libraries. Interrupted, changing or oversized downloads retain the previous committed snapshot. Automatically saved recently viewed rows are labeled partial. The index is a point-in-time snapshot and requires manual refresh; it is excluded from system backup.

Offline search and filters cover the saved index and local metadata edits. The UI still retains at most 1,000 summaries. File originals and thumbnails are not automatically downloaded. Fully saved pure text of at most 16,000 characters can be copied offline; longer text requires the PC. This is not full-text indexing of arbitrary files.

Up to 500 pending item edits are retained per library. Reconnecting to the original authenticated PC/library merges independent field changes. Same-field conflicts or remote deletion preserve the draft. Open “待同步 → 查看与处理”, compare remote values with your changes, then explicitly rebase and resubmit or discard that local draft. Retries are idempotent and never recreate deleted materials.

“清除离线索引” removes only the local index/text copy, retaining pending drafts, received files and PC originals. With an empty index and no drafts, “移除此离线库” releases its slot. Reconnect to enable it again. Legacy PCs do not receive queued edits. Offline indexes and drafts are excluded from migration archives.

## M57: incremental offline updates and pinned originals (local v1.20.0)

**Update the index:** When a complete offline index exists, an explicit save first requests changes from the current PC server run. Older PCs, server restarts, expired cursors or inconsistent library versions trigger a full save instead. Interrupted, cancelled or invalid updates retain the previous committed snapshot. This is a point-in-time index, not an automatic background mirror. Cancelling an index save preserves drafts. Re-editing a draft submits only fields actually changed; untouched PC fields continue to reflect remote values.

**Keep files offline:** Tap **保留原件** on a file card and confirm to download and verify a private phone copy. Use the top **离线原件** control to inspect status, verify and open, retry failures, unpin, or explicitly clean unreferenced interrupted copies. The phone-wide quota offers 128/512 MiB or 1/2 GiB, up to 200 entries. Reservations and interrupted copies count against it. Adjust the budget or unpin older copies when full; verification is never skipped.

Payload copies are private plaintext files; catalog records use encrypted local storage. Both are excluded from system backup. They are historical snapshots, do not refresh automatically when the PC source changes, and cannot prove current freshness. Opening grants the selected app temporary read-only access. Unpinning removes only this feature's private copy, preserving normal `Download/Liuzhuan` receipts and PC originals. Uninstalling Liuzhuan removes private copies; save important files to the ordinary receive directory for independent retention. Pinned originals are excluded from PC migration archives.

See the [M57 validation record](performance/M57_RELIABILITY_OFFLINE_2026-10-24.md) for local builds, production-path checks, phone audio-repair acceptance and isolated UI tests. Offline-original permissions, other ROMs, lock screens and battery behavior still need their own device checks.
