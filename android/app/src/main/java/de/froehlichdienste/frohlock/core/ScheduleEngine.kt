package de.froehlichdienste.frohlock.core

import java.time.LocalDateTime
import java.time.format.DateTimeFormatter

/** 0 = Sonntag .. 6 = Samstag (wie .NET DayOfWeek und der Server). */
fun LocalDateTime.dow(): Int = dayOfWeek.value % 7

/**
 * Reine Zeitplan-Entscheidung, 1:1 wie FrohLock.Core.Schedule.ScheduleEngine (Windows).
 * Fenster über Mitternacht (Start > Ende) zählen zum Starttag.
 */
class ScheduleEngine(private val config: LockConfig) {

    fun decide(localNow: LocalDateTime, trustedTimeAgeMinutes: Long, unlockUntilLocal: LocalDateTime?): Decision {
        if (trustedTimeAgeMinutes > config.maxTrustedTimeStalenessMinutes)
            return Decision.locked("Vertrauenszeit zu alt ($trustedTimeAgeMinutes min) – Fail-Secure")

        if (unlockUntilLocal != null && localNow.isBefore(unlockUntilLocal) && matchingWindow(localNow) != null)
            return Decision.unlocked("Entsperrt bis ${unlockUntilLocal.format(HM)}")

        matchingWindow(localNow)?.let { w ->
            return Decision.locked("Sperrfenster ${fmt(w.startMinute)}–${fmt(w.endMinute)}")
        }
        return Decision.unlocked("Außerhalb aller Sperrfenster")
    }

    /** Minuten bis zum nächsten Sperrbeginn; -1 wenn schon Sperrzeit oder keine in 24 h. */
    fun minutesUntilNextLockStart(localNow: LocalDateTime): Int {
        if (matchingWindow(localNow) != null) return -1
        val start = localNow.withSecond(0).withNano(0)
        for (i in 1..24 * 60) if (matchingWindow(start.plusMinutes(i.toLong())) != null) return i
        return -1
    }

    /** Wann endet die laufende Sperrzeit (für „Frei ab 07:00")? null = keine Sperrzeit. */
    fun currentWindowEnd(localNow: LocalDateTime): LocalDateTime? {
        if (matchingWindow(localNow) == null) return null
        val start = localNow.withSecond(0).withNano(0)
        for (i in 1..48 * 60) {
            val t = start.plusMinutes(i.toLong())
            if (matchingWindow(t) == null) return t
        }
        return null
    }

    fun matchingWindow(localNow: LocalDateTime): ScheduleWindow? {
        val minute = localNow.hour * 60 + localNow.minute
        return config.windows.firstOrNull { it.enabled && activeAt(it, localNow, minute) }
    }

    private fun activeAt(w: ScheduleWindow, localNow: LocalDateTime, minute: Int): Boolean {
        if (w.startMinute == w.endMinute) return false
        if (w.startMinute < w.endMinute)
            return w.appliesTo(localNow.dow()) && minute >= w.startMinute && minute < w.endMinute
        if (minute >= w.startMinute && w.appliesTo(localNow.dow())) return true
        if (minute < w.endMinute && w.appliesTo(localNow.minusDays(1).dow())) return true
        return false
    }

    /** Sperrzeiten von heute als Text (Status-Seite). */
    fun todaySummary(localNow: LocalDateTime): String {
        val parts = config.windows.filter { it.enabled && it.appliesTo(localNow.dow()) }
            .map { "${fmt(it.startMinute)}–${fmt(it.endMinute)}" }
        return if (parts.isEmpty()) "Heute keine feste Sperrzeit." else "Gesperrt: " + parts.joinToString(", ")
    }

    companion object {
        val HM: DateTimeFormatter = DateTimeFormatter.ofPattern("HH:mm")
        fun fmt(minute: Int) = "%02d:%02d".format(minute / 60, minute % 60)
    }
}

object BudgetPolicy {
    fun apply(base: Decision, budgetMinutes: Int, usedMinutes: Int, graceActive: Boolean, graceUntil: String?): Decision {
        if (base.locked) return base
        if (budgetMinutes <= 0) return base
        if (usedMinutes < budgetMinutes) return base
        return if (graceActive) Decision.unlocked("Zusatzzeit bis $graceUntil")
        else Decision.locked("Tageslimit erreicht ($usedMinutes/$budgetMinutes min)")
    }
}

object PenaltyPolicy {
    const val REASON_PREFIX = "Auszeit"

    fun apply(base: Decision, penaltySecondsRemaining: Long): Decision {
        if (base.locked) return base
        if (penaltySecondsRemaining <= 0) return base
        return Decision.locked("$REASON_PREFIX: noch ${formatCountdown(penaltySecondsRemaining)}")
    }

    /** Countdown läuft nur, wenn das Kind die Auszeit wirklich vor sich sieht. */
    fun shouldTick(baseUnlocked: Boolean, screenOn: Boolean) = baseUnlocked && screenOn

    fun isPenaltyReason(reason: String?) = reason?.startsWith(REASON_PREFIX) == true

    fun formatCountdown(seconds: Long): String {
        val s = seconds.coerceAtLeast(0)
        return "%d:%02d".format(s / 60, s % 60)
    }
}
