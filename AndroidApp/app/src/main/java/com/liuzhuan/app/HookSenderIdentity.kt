package com.liuzhuan.app

/** Pure identity policy, kept independent of Xposed classes and content payloads. */
internal object HookSenderIdentity {
    enum class Result { VERIFIED, UNKNOWN, MISMATCH }

    fun verify(claimedPackage: String?, senderPackage: String?, senderUid: Int,
        packagesForUid: (Int) -> Set<String>): Result {
        // INVALID_UID (-1), system/root/shell and missing identity are never trusted as
        // an application clipboard writer. The exported receiver must remain exported
        // because the hook executes under the original application's UID.
        val appId = senderUid % 100_000 // Android UID = userId * PER_USER_RANGE + appId.
        if (senderUid < 0 || appId < 10000 || senderPackage.isNullOrBlank()) return Result.UNKNOWN
        if (claimedPackage.isNullOrBlank() || claimedPackage != senderPackage) return Result.MISMATCH
        val owned = runCatching { packagesForUid(senderUid) }.getOrElse { return Result.UNKNOWN }
        if (owned.isEmpty()) return Result.UNKNOWN
        return if (claimedPackage in owned) Result.VERIFIED else Result.MISMATCH
    }
}
