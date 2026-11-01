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

    private fun rollDay(day: LocalDate) {
        val d = day.toString()
        if (usageDay != d) { usageDay = d; usageSecondsToday = 0; usageSegmentsToday.clear() }
    }

    fun usageMinutes(day: LocalDate) = if (usageDay == day.toString()) (usageSecondsToday / 60).toInt() else 0
    fun segments(day: LocalDate): List<UsageSegment> = if (usageDay == day.toString()) usageSegmentsToday else emptyList()

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
