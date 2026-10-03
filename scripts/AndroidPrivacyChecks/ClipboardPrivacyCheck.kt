import android.content.*
import android.util.Base64
import android.view.accessibility.AccessibilityEvent
import android.view.accessibility.AccessibilityNodeInfo
import com.liuzhuan.app.ClipReceiver
import com.liuzhuan.app.LanHub
import com.liuzhuan.app.clipboard.*
import com.liuzhuan.app.clipboard.action.PushToPcAction
import com.liuzhuan.app.net.LanClient
import kotlinx.coroutines.*
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.io.File
import kotlin.concurrent.thread

private var checks = 0
private fun verify(value: Boolean, label: String) {
    check(value) { label }; checks++; println("PASS $label")
}
private fun event(text: String, at: Long, pkg: String? = "com.allowed.app", source: ClipboardCaptureSource = ClipboardCaptureSource.XPOSED_HOOK) =
    ClipboardEvent(text, at, pkg, source, listOf("text/plain"), fingerprintOf(text, listOf("text/plain")))
private suspend fun awaitValue(test: () -> Boolean) {
    withTimeout(3000) { while (!test()) delay(10) }
}

private fun corruptedPreferencesChecks(mode: String) {
    val context = Context()
    val adapter = ClipboardPrivacyPreferences(context)
    val preferences = context.getSharedPreferences("clipboard_privacy", 0)
    if (mode == "physical-empty" || mode == "backup-empty") {
        val suffix = if (mode == "backup-empty") ".bak" else ""
        File(context.applicationInfo.dataDir, "shared_prefs/clipboard_privacy.xml$suffix").apply {
            parentFile.mkdirs(); writeText("<unreadable-fixture/>")
        }
    } else {
        adapter.save(ClipboardPrivacyPolicy.State(automaticEnabled = true, excludedPackages = setOf("com.secret.app")))
        when (mode) {
            "missing-marker" -> preferences.values.remove("schema")
            "missing-field" -> preferences.values.remove("excluded_packages")
            "wrong-type" -> preferences.values["paused_until"] = "not a time"
            else -> error("Unexpected corrupted fixture $mode")
        }
    }
    ClipboardMonitorCoordinator.init(context)
    verify(!ClipboardPrivacy.state.value.automaticEnabled && ClipboardPrivacy.state.value.pausedUntilMs == -1L &&
        ClipboardPrivacy.loadError.value.isNotEmpty(), "$mode config fails closed instead of treating existing data as first install")
    val closed = ClipboardPrivacy.state.value
    ClipboardPrivacy.migrateLegacyAutomaticEnabled(true)
    verify(ClipboardPrivacy.state.value == closed, "$mode cannot be overwritten by legacy enabled flow")
    val before = preferences.all
    preferences.failSaves = true
    verify(runCatching { ClipboardPrivacy.saveExcludedPackages("") }.isFailure && ClipboardPrivacy.state.value == closed &&
        ClipboardPrivacy.loadError.value.isNotEmpty() && preferences.all == before,
        "$mode failed repair preserves closed policy, diagnostic and original preference cache")
    preferences.failSaves = false
    ClipboardPrivacy.saveExcludedPackages("")
    verify(ClipboardPrivacy.loadError.value.isEmpty() && preferences.getInt("schema", 0) == 1 &&
        ClipboardPrivacyPreferences(context).load().pausedUntilMs == -1L && !ClipboardPrivacy.state.value.automaticEnabled,
        "$mode empty-rule repair confirms full record without enabling or resuming automatically")
    ClipboardPrivacy.resume(); ClipboardPrivacy.setAutomaticEnabled(true)
    verify(ClipboardPrivacy.state.value.automaticEnabled && !ClipboardPrivacy.state.value.paused(System.currentTimeMillis()),
        "$mode corrected rules can be explicitly resumed and enabled")
    println("All $checks $mode corruption checks passed.")
}

fun main(args: Array<String>) = runBlocking {
    if (args.isNotEmpty()) { corruptedPreferencesChecks(args.single()); return@runBlocking }
    var at = 1000L
    val policy = ClipboardPrivacyPolicy { at }
    val automatic = event("automatic", at)
    val manual = automatic.copy(source = ClipboardCaptureSource.MANUAL)
    verify(policy.blockReason(automatic) == ClipboardPrivacyPolicy.BlockReason.NOT_READY && policy.blockReason(manual) == null,
        "automatic events wait for initialized privacy rules while explicit manual events remain usable")
    policy.initialize(ClipboardPrivacyPolicy.State(automaticEnabled = true))
    verify(policy.blockReason(automatic) == null && policy.blockReason(automatic.copy(sourcePackage = null)) == null,
        "default settings permit automatic copies and unknown sources before any exclusion rule")
    verify(ClipboardPrivacyPolicy.parsePackages(" com.tencent.mm，com.android.chrome\ncom.tencent.mm ; com.Example_app ") ==
        setOf("com.tencent.mm", "com.android.chrome", "com.Example_app"), "package rules accept separators, deduplicate, and retain exact names")
    verify(ClipboardPrivacyPolicy.parsePackages(" \n,； ").isEmpty(), "blank rules remove exclusions")
    val invalidInputs = listOf("com.example.*", "com.example.app:remote", "https://example.app", "com..bad", "1com.app", "com.2app", "com.bad-name", "justname")
    verify(invalidInputs.all { runCatching { ClipboardPrivacyPolicy.parsePackages(it) }.isFailure }, "wildcards, process suffixes, URLs, empty segments and invalid package characters are rejected")
    verify(runCatching { ClipboardPrivacyPolicy.parsePackages((0..100).joinToString("\n") { "com.example.app$it" }) }.isFailure,
        "exclusion rules have a bounded package count")
    policy.replaceExcludedPackages("com.secret.app")
    at += 10
    verify(policy.blockReason(event("secret", at, "com.secret.app")) == ClipboardPrivacyPolicy.BlockReason.EXCLUDED_SOURCE,
        "known excluded source is blocked")
    verify(policy.blockReason(event("unknown", at, null)) == ClipboardPrivacyPolicy.BlockReason.UNKNOWN_SOURCE,
        "unknown clipboard writer is skipped when exclusions exist")
    verify(policy.blockReason(event("allowed", at)) == null && policy.blockReason(manual) == null,
        "unexcluded source and explicit manual actions remain usable")
    val beforeInvalid = policy.state.value
    runCatching { policy.replaceExcludedPackages("com.good.app, bad-input") }
    verify(policy.state.value == beforeInvalid, "invalid rule does not partially change the active configuration")
    val captured = policy.stampIfAllowed(event("pending", at))!!
    at += 10
    policy.pause()
    verify(policy.captureRevision("com.allowed.app") == null && policy.blockReason(event("paused", at)) == ClipboardPrivacyPolicy.BlockReason.PAUSED,
        "indefinite pause blocks capture and dispatch")
    verify(policy.blockReason(manual) == null, "pause does not block manual actions")
    at += 100
    policy.resume()
    verify(policy.blockReason(captured) == ClipboardPrivacyPolicy.BlockReason.INVALIDATED, "resume cannot revive already captured pre-pause events")
    verify(policy.blockReason(event("copied during pause", at - 20)) == ClipboardPrivacyPolicy.BlockReason.OLD_COPY,
        "delayed hook copy from the paused period is not replayed after resume")
    at += 1
    verify(policy.blockReason(event("new copy", at)) == null, "new permitted copy is accepted after resume")
    policy.pause(15 * 60_000L)
    val deadline = policy.state.value.pausedUntilMs
    at = deadline + 1
    val afterDeadline = policy.stampIfAllowed(event("after deadline", at))!!
    verify(policy.blockReason(event("during timed pause", deadline - 1)) == ClipboardPrivacyPolicy.BlockReason.OLD_COPY,
        "timed pause rejects delayed copies from its entire interval even before UI refresh")
    verify(policy.blockReason(afterDeadline) == null && policy.refreshExpiredPause() && policy.blockReason(afterDeadline) == null,
        "expiry automatically permits new copies and one-shot refresh preserves their validity")
    policy.setAutomaticEnabled(false)
    verify(policy.blockReason(event("disabled", at + 1)) == ClipboardPrivacyPolicy.BlockReason.DISABLED && policy.blockReason(manual) == null,
        "master switch only controls automatic copies")
    at += 10
    policy.setAutomaticEnabled(true)
    verify(policy.blockReason(event("disabled-period copy", at - 1)) == ClipboardPrivacyPolicy.BlockReason.OLD_COPY,
        "re-enabling does not forward copies retained from the disabled period")

    val preferencesContext = Context()
    val adapter = ClipboardPrivacyPreferences(preferencesContext)
    val prefs = preferencesContext.getSharedPreferences("clipboard_privacy", 0)
    verify(adapter.needsLegacyMigration && adapter.load() == ClipboardPrivacyPolicy.State() && prefs.commitCount == 0,
        "only a first absent privacy file accepts legacy migration and loading never writes settings")
    val stored = ClipboardPrivacyPolicy.State(automaticEnabled = true, pausedUntilMs = at + 60 * 60_000L,
        excludedPackages = setOf("com.tencent.mm", "com.android.chrome"), discardBeforeMs = at)
    adapter.save(stored)
    verify(ClipboardPrivacyPreferences(preferencesContext).load() == stored, "fresh preferences adapter restores enabled flag, one-hour deadline, exclusions and discarded-copy boundary")
    val beforeFailure = prefs.all
    prefs.failSaves = true
    verify(runCatching { adapter.save(stored.copy(automaticEnabled = false, excludedPackages = emptySet())) }.isFailure &&
        prefs.all == beforeFailure && prefs.persistedValues == beforeFailure && adapter.load() == stored,
        "failed commit restores its prematurely updated cache and preserves the previous persisted protection")
    prefs.failSaves = false
    preferencesContext.getSharedPreferences("clipboard_privacy", 0).values["excluded_packages"] = "com.bad.*"
    verify(runCatching { adapter.load() }.isFailure && adapter.exclusionText() == "com.bad.*", "invalid persisted exclusions are reported and remain available for correction")

    val concurrentPolicy = ClipboardPrivacyPolicy { at }
    concurrentPolicy.initialize(ClipboardPrivacyPolicy.State(automaticEnabled = true))
    val inFlight = concurrentPolicy.stampIfAllowed(event("in flight", at))!!
    val entered = CountDownLatch(1); val release = CountDownLatch(1); val pauseDone = CountDownLatch(1)
    val sender = thread { concurrentPolicy.runIfAllowed(inFlight) { entered.countDown(); release.await(2, TimeUnit.SECONDS) } }
    check(entered.await(1, TimeUnit.SECONDS))
    val pauser = thread { concurrentPolicy.pause(); pauseDone.countDown() }
    verify(!pauseDone.await(40, TimeUnit.MILLISECONDS), "pause serializes with a transfer that has already begun")
    release.countDown(); sender.join(); pauser.join()
    var submitted = false
    verify(!concurrentPolicy.runIfAllowed(inFlight) { submitted = true } && !submitted,
        "after pause completes an old event cannot submit another transfer")

    var failPersistence = true
    var persistenceCalls = 0
    val transactionalPolicy = ClipboardPrivacyPolicy(beforeChange = { persistenceCalls++; if (failPersistence) error("injected disk failure") }) { at }
    val protected = ClipboardPrivacyPolicy.State(automaticEnabled = true, pausedUntilMs = -1,
        excludedPackages = setOf("com.secret.app"))
    transactionalPolicy.initialize(protected)
    verify(persistenceCalls == 0, "loading initializes policy without attempting to persist it again")
    val protectedState = transactionalPolicy.state.value
    verify(runCatching { transactionalPolicy.resume() }.isFailure && transactionalPolicy.state.value == protectedState,
        "failed resume cannot publish an unpaused candidate or change the event revision")
    verify(runCatching { transactionalPolicy.replaceExcludedPackages("") }.isFailure && transactionalPolicy.state.value == protectedState,
        "failed exclusion save retains the previous source protection")
    var leaked = false
    verify(!transactionalPolicy.runIfAllowed(event("protected queued secret", at + 1, "com.secret.app")) { leaked = true } && !leaked,
        "failed persistence does not let queued automatic data through the existing pause")
    failPersistence = false; transactionalPolicy.resume()
    verify(transactionalPolicy.state.value.pausedUntilMs == 0L && persistenceCalls == 3,
        "successful persistence is required before the confirmed candidate becomes active")

    val saveEntered = CountDownLatch(1); val saveRelease = CountDownLatch(1); val guardedDone = CountDownLatch(1)
    val lockedFailurePolicy = ClipboardPrivacyPolicy(beforeChange = {
        saveEntered.countDown(); saveRelease.await(2, TimeUnit.SECONDS); error("injected failure after delay")
    }) { at }
    lockedFailurePolicy.initialize(protected)
    val failedSaving = thread { runCatching { lockedFailurePolicy.resume() } }
    check(saveEntered.await(1, TimeUnit.SECONDS))
    val guardDuringSave = thread { lockedFailurePolicy.runIfAllowed(event("never submitted", at + 1)) { leaked = true }; guardedDone.countDown() }
    verify(!guardedDone.await(40, TimeUnit.MILLISECONDS), "send guards serialize with pending persistence and cache recovery")
    saveRelease.countDown(); failedSaving.join(); guardDuringSave.join()
    verify(!leaked && lockedFailurePolicy.state.value.pausedUntilMs == -1L,
        "guard after failed save sees the original paused policy and cannot submit the candidate")

    // 以下使用生产 Receiver/Capture/Dispatcher/Pipeline/Queue/Action/Coordinator，只替换 OS 与网络副作用。
    val context = Context()
    ClipboardMonitorCoordinator.init(context)
    val livePrefs = context.getSharedPreferences("clipboard_privacy", 0)
    ClipboardPrivacy.migrateLegacyAutomaticEnabled(false)
    verify(livePrefs.getInt("schema", 0) == 1 && !ClipboardPrivacy.state.value.automaticEnabled,
        "first legacy false value still writes an authoritative schema marker")
    ClipboardPrivacy.migrateLegacyAutomaticEnabled(true)
    verify(!ClipboardPrivacy.state.value.automaticEnabled,
        "subsequent legacy settings flow cannot override the authoritative privacy disabled switch")
    ClipboardPrivacy.setAutomaticEnabled(true)
    ClipboardPrivacy.migrateLegacyAutomaticEnabled(false)
    verify(ClipboardPrivacy.state.value.automaticEnabled,
        "later legacy disabled value cannot override an explicitly saved privacy enabled switch")
    val client = LanClient(); LanHub.client = client
    fun live(text: String, pkg: String? = "com.allowed.app", source: ClipboardCaptureSource = ClipboardCaptureSource.XPOSED_HOOK) =
        event(text, System.currentTimeMillis() + 10, pkg, source)
    ClipboardEventDispatcher.dispatch(live("offline pending"))
    awaitValue { ClipboardMonitorCoordinator.queue.size() == 1 }
    ClipboardPrivacy.pause()
    verify(ClipboardMonitorCoordinator.queue.size() == 0, "pause removes production offline automatic queue entries")
    val pauseBeforeFailedResume = ClipboardPrivacy.state.value
    val prefsBeforeFailedResume = livePrefs.all
    livePrefs.failSaves = true
    verify(runCatching { ClipboardPrivacy.resume() }.isFailure && ClipboardPrivacy.state.value == pauseBeforeFailedResume &&
        livePrefs.all == prefsBeforeFailedResume, "production failed resume retains old pause, revision and preference cache")
    ClipboardMonitorCoordinator.queue.enqueue(live("queued private data", "com.secret.app"))
    client.state = LanClient.State.Connected
    ClipboardMonitorCoordinator.onWsConnected()
    awaitValue { ClipboardMonitorCoordinator.queue.size() == 0 }
    verify(client.texts.isEmpty() && ClipboardMonitorCoordinator.queue.size() == 0,
        "failed production save cannot leak queued automatic events during reconnection")
    livePrefs.failSaves = false
    ClipboardPrivacy.resume()
    ClipboardMonitorCoordinator.onWsConnected()
    awaitValue { ClipboardMonitorCoordinator.queue.size() == 0 }
    verify(client.texts.isEmpty(), "reconnection after resume does not flush pre-pause automatic data")
    delay(15)
    ClipboardEventDispatcher.dispatch(live("permitted hook"))
    awaitValue { client.texts.size == 1 }
    verify(client.texts.last().first == "permitted hook", "permitted LSPosed text traverses production dispatcher and action")

    val receiver = ClipReceiver()
    fun hook(kind: String, pkg: String = "com.allowed.app", text: String = "hook text") =
        Intent(ClipReceiver.ACTION_CLIP).putExtra(ClipReceiver.EXTRA_TOKEN, ClipReceiver.TOKEN)
            .putExtra(ClipReceiver.EXTRA_KIND, kind).putExtra(ClipReceiver.EXTRA_PKG, pkg)
            .putExtra(ClipReceiver.EXTRA_TS, System.currentTimeMillis() + 10)
            .putExtra(ClipReceiver.EXTRA_TEXT, text).putExtra(ClipReceiver.EXTRA_FILE_B64, "AQID")
            .putExtra(ClipReceiver.EXTRA_FILE_MIME, "image/png").putExtra(ClipReceiver.EXTRA_FILE_SIZE, 3)
    ClipboardPrivacy.pause()
    val pausedText = hook(ClipReceiver.KIND_TEXT)
    val pausedFile = hook(ClipReceiver.KIND_FILE)
    val decodesBefore = Base64.decodes
    receiver.onReceive(context, pausedText); receiver.onReceive(context, pausedFile)
    verify(pausedText.reads[ClipReceiver.EXTRA_TEXT] == null && pausedFile.reads[ClipReceiver.EXTRA_FILE_B64] == null && Base64.decodes == decodesBefore,
        "paused receiver does not extract text or decode automatic files")
    ClipboardPrivacy.resume(); ClipboardPrivacy.saveExcludedPackages("com.secret.app")
    val secretText = hook(ClipReceiver.KIND_TEXT, "com.secret.app")
    val secretFile = hook(ClipReceiver.KIND_FILE, "com.secret.app")
    receiver.onReceive(context, secretText); receiver.onReceive(context, secretFile)
    verify(secretText.reads[ClipReceiver.EXTRA_TEXT] == null && secretFile.reads[ClipReceiver.EXTRA_FILE_B64] == null && client.files.isEmpty(),
        "excluded LSPosed source cannot bypass privacy using text or file broadcasts")
    val textCount = client.texts.size
    ClipboardEventDispatcher.dispatch(live("unknown origin", null))
    delay(80)
    verify(client.texts.size == textCount, "production dispatcher blocks unknown writer under active exclusions")

    val manager: ClipboardCaptureManager = ClipboardMonitorCoordinator.captureManager
    val excludedNode = AccessibilityNodeInfo("excluded selection", "com.secret.app")
    val excludedAcc = AccessibilityEvent(AccessibilityEvent.TYPE_VIEW_TEXT_SELECTION_CHANGED, "com.secret.app", excludedNode)
    manager.onAccessibilityEvent(excludedAcc)
    verify(excludedAcc.sourceReads == 0 && excludedNode.textReads == 0, "excluded accessibility source is skipped before reading selected text")
    val serviceReads = context.systemServiceReads
    verify(manager.tryReadClipboard("unknown-source test") == null && context.systemServiceReads == serviceReads,
        "unknown-source clipboard fallback is skipped before accessing ClipboardManager under exclusions")
    delay(15)
    val allowedNode = AccessibilityNodeInfo("allowed selection")
    val allowedAcc = AccessibilityEvent(AccessibilityEvent.TYPE_VIEW_TEXT_SELECTION_CHANGED, "com.allowed.app", allowedNode)
    val selection = manager.tryCaptureAfterCopy(allowedAcc, "selection-test")
    verify(selection?.sourcePackage == "com.allowed.app" && selection.source == ClipboardCaptureSource.ACCESSIBILITY_SELECTION,
        "permitted accessibility selection retains its actual event source package")
    ClipboardEventDispatcher.dispatch(selection!!)
    awaitValue { client.texts.size == textCount + 1 }

    ClipboardPrivacy.saveExcludedPackages(""); delay(15)
    val retainedItem = ClipData.Item("retained old clipboard")
    context.clipboard.clip = ClipData(listOf(retainedItem), ClipDescription(ClipboardPrivacy.state.value.discardBeforeMs - 1))
    verify(manager.tryReadClipboard("old clipboard after resume") == null && retainedItem.textReads == 0,
        "retained pre-rule clipboard content is rejected by copy timestamp before reading text")
    val freshItem = ClipData.Item("fresh clipboard after resume")
    context.clipboard.clip = ClipData(listOf(freshItem), ClipDescription(System.currentTimeMillis() + 10))
    val fresh = manager.tryReadClipboard("fresh clipboard")
    verify(fresh?.text == freshItem.text && fresh.sourcePackage == null,
        "fresh clipboard is accepted without exclusions and honestly reports unknown source")
    val pendingAction = ClipboardPrivacy.stampIfAllowed(live("asynchronous pending"))!!
    ClipboardPrivacy.pause(); ClipboardPrivacy.resume()
    val beforeLateAction = client.texts.size
    PushToPcAction(ClipboardMonitorCoordinator.queue).execute(pendingAction)
    verify(client.texts.size == beforeLateAction && ClipboardMonitorCoordinator.queue.size() == 0,
        "asynchronous action captured before pause cannot send or requeue after resume")

    ClipboardPrivacy.saveExcludedPackages("com.secret.app"); delay(15)
    ClipboardMonitorCoordinator.queue.enqueue(live("stale secret coordinator", "com.secret.app"))
    ClipboardMonitorCoordinator.onWsConnected()
    awaitValue { ClipboardMonitorCoordinator.queue.size() == 0 }
    verify(client.texts.size == beforeLateAction && ClipboardMonitorCoordinator.queue.size() == 0,
        "coordinator revalidates and discards excluded queued events")
    ClipboardMonitorCoordinator.queue.enqueue(live("stale secret action", "com.secret.app"))
    PushToPcAction(ClipboardMonitorCoordinator.queue).execute(ClipboardPrivacy.stampIfAllowed(live("new allowed action"))!!)
    awaitValue { client.texts.lastOrNull()?.first == "new allowed action" }
    verify(client.texts.last().first == "new allowed action" && client.texts.none { it.second == "com.secret.app" },
        "action FIFO flush also revalidates queued source rules")
    val allowedFile = hook(ClipReceiver.KIND_FILE)
    receiver.onReceive(context, allowedFile)
    verify(client.files.size == 1 && Base64.decodes == decodesBefore + 1, "permitted automatic file remains usable through guarded receiver path")
    ClipboardPrivacy.setAutomaticEnabled(false); ClipboardPrivacy.pause()
    val beforeManual = client.texts.size
    ClipboardEventDispatcher.dispatch(live("explicit manual text", source = ClipboardCaptureSource.MANUAL))
    awaitValue { client.texts.size == beforeManual + 1 }
    client.pushFile(byteArrayOf(1), "manual file", "application/octet-stream")
    verify(client.texts.last().first == "explicit manual text" && client.files.last() == "manual file",
        "explicit manual text and file actions remain usable while automatic sync is disabled and paused")
    println("All $checks Android automatic clipboard privacy checks passed.")
}
