import com.liuzhuan.app.data.MaterialItem
import com.liuzhuan.app.data.MaterialRepository as Repo

private var checks = 0
private var gaps = 0
private fun checkCase(value: Boolean, label: String) {
    check(value) { label }
    checks++
    println("PASS $label")
}
private fun item(id: String, time: Long = 100, sequence: Long = 0, name: String = id) =
    MaterialItem(id, "Text", name, 1, time, sequence)
private fun ids() = Repo.materials.value.map { it.id }
private fun snapshot(items: List<MaterialItem>, sequence: Long) {
    gaps = 0
    Repo.onSequenceGap = { gaps++ }
    Repo.replaceAll(items, sequence)
}

fun main() {
    snapshot(listOf(item("new", 200), item("old", 100)), 10)
    Repo.delete("old", 11)
    checkCase(ids() == listOf("new"), "continuous delete applies without requesting snapshot")
    Repo.add(item("old", 100, 12))
    checkCase(ids() == listOf("new", "old") && gaps == 0, "undo keeps old material below newer material and consumes delete sequence")
    Repo.add(item("fresh", 300, 13))
    checkCase(ids() == listOf("fresh", "new", "old"), "new material is sorted by creation time")
    Repo.delete("not-in-recent-window", 14)
    Repo.add(item("after-hidden-delete", 400, 15))
    checkCase(gaps == 0 && ids().first() == "after-hidden-delete", "deleting an unseen ID still consumes its sequence")
    Repo.clear(16)
    checkCase(ids().isEmpty(), "continuous clear applies")
    Repo.add(item("after-clear", 500, 17))
    checkCase(ids() == listOf("after-clear") && gaps == 0, "add after clear continues without a false gap")
    Repo.clear(18)
    Repo.clear(19)
    Repo.add(item("after-empty-clear", 500, 20))
    checkCase(gaps == 0 && ids() == listOf("after-empty-clear"), "clearing an already empty mirror still consumes the sequence")

    snapshot(listOf(item("a"), item("b"), item("c")), 100)
    checkCase(ids() == listOf("a", "b", "c"), "snapshot order remains stable within the same second")
    Repo.add(item("d", sequence = 101))
    checkCase(ids() == listOf("d", "a", "b", "c"), "new material from the same second appears first")
    Repo.delete("b", 102)
    Repo.add(item("b", sequence = 103))
    checkCase(ids() == listOf("d", "a", "b", "c"), "same-second undo restores the original position")
    Repo.delete("b", 104)
    Repo.delete("a", 105)
    Repo.add(item("a", sequence = 106))
    Repo.add(item("b", sequence = 107))
    checkCase(ids() == listOf("d", "a", "b", "c") && gaps == 0, "batch undo preserves same-second positions and sequence continuity")
    Repo.add(item("e", sequence = 108))
    checkCase(ids() == listOf("e", "d", "a", "b", "c"), "new arrivals remain above restored same-second materials")
    Repo.replaceAll(listOf(item("b"), item("a"), item("d"), item("c")), 109)
    checkCase(ids() == listOf("b", "a", "d", "c"), "new snapshot overrides local tie ordering")
    Repo.add(item("a", sequence = 110, name = "updated text"))
    checkCase(ids() == listOf("a", "b", "d", "c") && Repo.materials.value.first().name == "updated text", "same-ID new event updates its summary and same-second position")
    Repo.clear(111)
    Repo.add(item("c", sequence = 112))
    Repo.add(item("d", sequence = 113))
    Repo.add(item("b", sequence = 114))
    Repo.add(item("a", sequence = 115))
    checkCase(ids() == listOf("a", "b", "d", "c") && gaps == 0, "undo clear restores same-second ordering")

    snapshot(listOf(item("kept")), 10)
    Repo.add(item("stale-new", sequence = 9))
    Repo.add(item("kept", sequence = 10, name = "stale overwrite"))
    Repo.delete("kept", 8)
    Repo.clear(0)
    checkCase(ids() == listOf("kept") && Repo.materials.value.first().name == "kept", "stale and duplicate add/delete/clear cannot modify the mirror")
    Repo.add(item("kept", 200, 11, "new summary"))
    Repo.add(item("next", 300, 12))
    checkCase(ids() == listOf("next", "kept") && gaps == 0, "same-ID new sequence is consumed instead of causing the next event to gap")
    Repo.add(item("next", 1, 12, "duplicate overwrite"))
    checkCase(Repo.materials.value.first().name == "next", "duplicate same-sequence event cannot revert content")

    snapshot(listOf(item("stable")), 20)
    Repo.add(item("gap-item", sequence = 22))
    checkCase(ids() == listOf("stable") && gaps == 1 && Repo.syncState.value == Repo.SyncState.RESYNCING, "add gap requests one snapshot before applying any mutation")
    Repo.delete("stable", 23)
    Repo.clear(24)
    Repo.add(item("future", sequence = 25))
    Repo.add(item("late", sequence = 21))
    checkCase(ids() == listOf("stable") && gaps == 1, "all deltas wait for snapshot after a gap without repeated requests")
    Repo.replaceAll(listOf(item("repaired")), 25)
    Repo.delete("repaired", 26)
    checkCase(ids().isEmpty() && Repo.syncState.value == Repo.SyncState.SYNCED, "replacement snapshot repairs the baseline and resumes deletion")
    snapshot(listOf(item("stable")), 1)
    Repo.delete("stable", 3)
    checkCase(ids() == listOf("stable") && gaps == 1, "delete gap does not remove data before snapshot")
    snapshot(listOf(item("stable")), 1)
    Repo.clear(3)
    checkCase(ids() == listOf("stable") && gaps == 1, "clear gap does not clear data before snapshot")

    snapshot(listOf(item("old-epoch")), 100)
    Repo.markSyncing()
    checkCase(ids() == listOf("old-epoch") && Repo.syncState.value == Repo.SyncState.SYNCING, "reconnect retains the offline picture while awaiting authoritative data")
    Repo.add(item("pre-snapshot", sequence = 101))
    Repo.delete("old-epoch", 102)
    Repo.clear(103)
    checkCase(ids() == listOf("old-epoch") && gaps == 0, "new connection does not apply deltas against the old epoch")
    Repo.replaceAll(emptyList(), 0)
    Repo.add(item("restarted", sequence = 1))
    checkCase(ids() == listOf("restarted") && gaps == 0, "empty sequence-zero snapshot resets a previous high sequence")
    Repo.replaceAll(listOf(item("loaded-from-disk")), 0)
    Repo.add(item("first-mutation", 200, 1))
    checkCase(ids() == listOf("first-mutation", "loaded-from-disk") && gaps == 0, "nonempty sequence-zero snapshot also resets the baseline")
    Repo.markError()
    Repo.clear(2)
    checkCase(ids() == listOf("first-mutation", "loaded-from-disk") && Repo.syncState.value == Repo.SyncState.ERROR, "sync error preserves the picture until a new snapshot")
    println("All $checks Android receive synchronization checks passed.")
}
