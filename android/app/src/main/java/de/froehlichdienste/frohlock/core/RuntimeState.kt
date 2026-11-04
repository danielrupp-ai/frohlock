package de.froehlichdienste.frohlock.core

import org.json.JSONArray
import org.json.JSONObject
import java.time.LocalDate

/** Persistierter Laufzeitzustand (überlebt Neustart) – Port von RuntimeState.cs. Keine Klartext-Geheimnisse. */
class RuntimeState {
    var unlockUntilUnix = 0L
    var masterUnlockUntilUnix = 0L
    /** „Sofort sperren" der Eltern: gilt bis Eltern-PIN, Fern-Entsperren oder Master-PIN. */
    var forcedLock = false
    var pinFailuresToday = 0
    var pinFailureDay = ""

    var usageDay = ""
    var usageSecondsToday = 0L
    val usageSegmentsToday = mutableListOf<UsageSegment>()
    /** Sekunden, in denen FrohLock heute „Bildschirm an + entsperrt" gesehen hat (gezählt ODER gesperrt). */
    var onSecondsToday = 0L
    /** Davon in der Sperre in einer erlaubten App (Telegram, Signal, Telefon) – zählt nicht aufs Limit, wird aber gezeigt. */
    var allowedInLockSecondsToday = 0L
    /** Aus Androids eigenen Bildschirm-Ereignissen nachgezählte Zeit (FrohLock lief nicht oder sah den Bildschirm nicht). */
    var missedSecondsToday = 0L
    /** Gegenprobe: Stand von Android („Bildschirm an + entsperrt" seit Mitternacht) und von FrohLock beim letzten Abgleich. */
    var ledgerBaseAndroidSeconds = -1L
    var ledgerBaseOwnSeconds = 0L
    /** Letzter Stand der Android-Gegenprobe (nur Anzeige im Admin). */
    var ledgerAndroidSecondsToday = 0L
    /** Tag, an dem zuletzt „Uhr lief rückwärts" gemeldet wurde (nur einmal je Tag). */
    var backwardsDayWarned = ""

    var penaltySecondsRemaining = 0L
    var penaltyCount = 0
    var penaltyTotalMinutes = 0
    val executedCommandIds = mutableListOf<String>()

    /** Letzter Takt: Wanduhr + Wach-Uhr (SystemClock.uptimeMillis, steht im Tiefschlaf) + Boot-Zähler. */
    var lastTickWallUnix = 0L
    var lastTickUptimeMs = 0L
    var lastTickBootCount = -1

    /** Eltern haben die Schutz-Einstellungen vorübergehend gelockert (Entwickleroptionen, Uhrzeit …). */
    var relaxedUntilUnix = 0L
    /** Welche Apps FrohLock pausiert hat (damit genau diese wieder freigegeben werden). */
    val suspendedPackages = mutableListOf<String>()
    /** Bereits verschickte Schlafenszeit-Erinnerungen („2026-11-01 21:00 -10"). */
    var lastReminderKey = ""
    /** Einfacher Modus: wann zuletzt „Update bereit" angeboten wurde (nicht ständig nerven). */
    var lastUpdatePromptUnix = 0L

    fun markCommandExecuted(id: String): Boolean {
        if (id.isEmpty()) return true
        if (id in executedCommandIds) return false
        executedCommandIds.add(id)
        while (executedCommandIds.size > 100) executedCommandIds.removeAt(0)
        return true
    }

    fun registerPinFailure(day: LocalDate) {
        val d = day.toString()
        if (pinFailureDay != d) { pinFailureDay = d; pinFailuresToday = 0 }
        pinFailuresToday++
    }

    fun pinFailures(day: LocalDate) = if (pinFailureDay == day.toString()) pinFailuresToday else 0

    fun addUsage(day: LocalDate, nowUnix: Long, seconds: Int) {
        rollDay(day)
        usageSecondsToday += seconds
        UsageTimeline.accrue(usageSegmentsToday, nowUnix, seconds)
    }

    /** Jede Sekunde „Bildschirm an + entsperrt" festhalten – auch in der Sperre (Basis für die Android-Gegenprobe). */
    fun addOn(day: LocalDate, seconds: Int, lockedInAllowedApp: Boolean) {
        rollDay(day)
        onSecondsToday += seconds
        if (lockedInAllowedApp) allowedInLockSecondsToday += seconds
    }

    /** Verpasste Zeit nachzählen (Android sah den Bildschirm an, FrohLock nicht): zählt aufs Limit, ohne Uhrzeit-Abschnitt. */
    fun addMissedUsage(day: LocalDate, seconds: Int) {
        if (seconds <= 0) return
        rollDay(day)
        usageSecondsToday += seconds
        onSecondsToday += seconds
        missedSecondsToday += seconds
    }

    /**
     * Neuer Tag → alles auf null. Liegt der „neue" Tag VOR dem gespeicherten (Uhr/Zeitzone zurückgedreht),
     * wird NICHT zurückgesetzt – sonst gäbe es durch Zurückstellen frische Bildschirmzeit. Liefert true, wenn das passiert ist.
     */
    fun rollDay(day: LocalDate): Boolean {
        val d = day.toString()
        if (usageDay == d) return false
        if (usageDay.isNotEmpty() && usageDay > d) return true
        usageDay = d; usageSecondsToday = 0; usageSegmentsToday.clear()
        onSecondsToday = 0; allowedInLockSecondsToday = 0; missedSecondsToday = 0
        ledgerBaseAndroidSeconds = -1; ledgerBaseOwnSeconds = 0; ledgerAndroidSecondsToday = 0
        return false
    }

    /** Gilt der gespeicherte Tag für [day]? (gleicher Tag – oder die Uhr steht hinter dem gespeicherten Tag.) */
    private fun sameDay(day: LocalDate): Boolean { val d = day.toString(); return usageDay == d || (usageDay.isNotEmpty() && usageDay > d) }

    fun usageMinutes(day: LocalDate) = if (sameDay(day)) (usageSecondsToday / 60).toInt() else 0
    fun segments(day: LocalDate): List<UsageSegment> = if (sameDay(day)) usageSegmentsToday else emptyList()
    fun onSeconds(day: LocalDate) = if (sameDay(day)) onSecondsToday else 0L
    fun allowedInLockMinutes(day: LocalDate) = if (sameDay(day)) (allowedInLockSecondsToday / 60).toInt() else 0
    fun missedMinutes(day: LocalDate) = if (sameDay(day)) (missedSecondsToday / 60).toInt() else 0
    fun ledgerAndroidMinutes(day: LocalDate) = if (sameDay(day)) (ledgerAndroidSecondsToday / 60).toInt() else 0

    fun clearPenalty() { penaltySecondsRemaining = 0; penaltyCount = 0; penaltyTotalMinutes = 0 }

    fun toJson(): JSONObject = JSONObject()
        .put("unlockUntilUnix", unlockUntilUnix)
        .put("masterUnlockUntilUnix", masterUnlockUntilUnix)
        .put("forcedLock", forcedLock)
        .put("pinFailuresToday", pinFailuresToday)
        .put("pinFailureDay", pinFailureDay)
        .put("usageDay", usageDay)
        .put("usageSecondsToday", usageSecondsToday)
        .put("usageSegmentsToday", JSONArray().also { a -> usageSegmentsToday.forEach { a.put(it.toJson()) } })
        .put("onSecondsToday", onSecondsToday)
        .put("allowedInLockSecondsToday", allowedInLockSecondsToday)
        .put("missedSecondsToday", missedSecondsToday)
        .put("ledgerBaseAndroidSeconds", ledgerBaseAndroidSeconds)
        .put("ledgerBaseOwnSeconds", ledgerBaseOwnSeconds)
        .put("ledgerAndroidSecondsToday", ledgerAndroidSecondsToday)
        .put("backwardsDayWarned", backwardsDayWarned)
        .put("penaltySecondsRemaining", penaltySecondsRemaining)
        .put("penaltyCount", penaltyCount)
        .put("penaltyTotalMinutes", penaltyTotalMinutes)
        .put("executedCommandIds", JSONArray(executedCommandIds))
        .put("lastTickWallUnix", lastTickWallUnix)
        .put("lastTickUptimeMs", lastTickUptimeMs)
        .put("lastTickBootCount", lastTickBootCount)
        .put("relaxedUntilUnix", relaxedUntilUnix)
        .put("suspendedPackages", JSONArray(suspendedPackages))
        .put("lastReminderKey", lastReminderKey)
        .put("lastUpdatePromptUnix", lastUpdatePromptUnix)

    companion object {
        fun fromJson(o: JSONObject): RuntimeState = RuntimeState().apply {
            unlockUntilUnix = o.optLong("unlockUntilUnix")
            masterUnlockUntilUnix = o.optLong("masterUnlockUntilUnix")
            forcedLock = o.optBoolean("forcedLock")
            pinFailuresToday = o.optInt("pinFailuresToday")
            pinFailureDay = o.optString("pinFailureDay", "")
            usageDay = o.optString("usageDay", "")
            usageSecondsToday = o.optLong("usageSecondsToday")
            o.optJSONArray("usageSegmentsToday")?.let { a ->
                for (i in 0 until a.length()) usageSegmentsToday.add(UsageSegment.fromJson(a.getJSONObject(i)))
            }
            // Vor 1.0.5 gab es die Felder nicht: Gesehenes = Gezähltes (kein falscher Sprung beim ersten Abgleich).
            onSecondsToday = o.optLong("onSecondsToday", usageSecondsToday)
            allowedInLockSecondsToday = o.optLong("allowedInLockSecondsToday")
            missedSecondsToday = o.optLong("missedSecondsToday")
            ledgerBaseAndroidSeconds = o.optLong("ledgerBaseAndroidSeconds", -1)
            ledgerBaseOwnSeconds = o.optLong("ledgerBaseOwnSeconds")
            ledgerAndroidSecondsToday = o.optLong("ledgerAndroidSecondsToday")
            backwardsDayWarned = o.optString("backwardsDayWarned", "")
            penaltySecondsRemaining = o.optLong("penaltySecondsRemaining")
            penaltyCount = o.optInt("penaltyCount")
            penaltyTotalMinutes = o.optInt("penaltyTotalMinutes")
            o.optJSONArray("executedCommandIds")?.let { a -> for (i in 0 until a.length()) executedCommandIds.add(a.optString(i)) }
            lastTickWallUnix = o.optLong("lastTickWallUnix")
            lastTickUptimeMs = o.optLong("lastTickUptimeMs")
            lastTickBootCount = o.optInt("lastTickBootCount", -1)
            relaxedUntilUnix = o.optLong("relaxedUntilUnix")
            o.optJSONArray("suspendedPackages")?.let { a -> for (i in 0 until a.length()) suspendedPackages.add(a.optString(i)) }
            lastReminderKey = o.optString("lastReminderKey", "")
            lastUpdatePromptUnix = o.optLong("lastUpdatePromptUnix")
        }
    }
}
