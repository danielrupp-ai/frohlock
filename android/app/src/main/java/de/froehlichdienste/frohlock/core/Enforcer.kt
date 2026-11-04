package de.froehlichdienste.frohlock.core

import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.time.Instant
import java.time.LocalDate
import java.time.LocalDateTime
import java.time.ZoneId
import java.time.ZonedDateTime

interface TrustedClock {
    fun nowMillis(): Long
    val zone: ZoneId
}

interface Audit {
    fun write(category: String, message: String)
    fun tail(n: Int): List<String>
}

/** Gleiches Zeilenformat wie Windows (ISO-UTC \t KATEGORIE \t Text) – der Server wertet ⚠-Zeilen aus. */
class FileAudit(private val file: File, private val maxBytes: Long = 1_000_000) : Audit {
    @Synchronized
    override fun write(category: String, message: String) {
        try {
            file.parentFile?.mkdirs()
            if (file.exists() && file.length() > maxBytes) {
                val bak = File(file.path + ".1")
                bak.delete(); file.renameTo(bak)
            }
            val clean = message.replace('\n', ' ').replace('\t', ' ')
            file.appendText("${Instant.now()}\t$category\t$clean\n")
        } catch (_: Exception) { }
    }

    @Synchronized
    override fun tail(n: Int): List<String> = try {
        if (!file.exists()) emptyList() else file.readLines().takeLast(n)
    } catch (_: Exception) { emptyList() }
}

/**
 * Sperr-Gehirn – Port von EnforcementController.cs (ohne Windows-Sitzungslogik).
 * Reihenfolge: Master > nicht eingerichtet > Sofortsperre > Zeitplan/Entsperrung > Tageslimit > Auszeit.
 */
class Enforcer(
    private val clock: TrustedClock,
    private val audit: Audit,
    private val state: RuntimeState,
    private val persist: (RuntimeState) -> Unit,
    /** „das Handy" / „das Tablet" für kindgerechte Texte. */
    private val deviceWord: String = "das Handy",
) {
    private val lock = Any()
    private var config: LockConfig? = null
    private var engine: ScheduleEngine? = null

    fun setConfig(c: LockConfig?) = synchronized(lock) {
        config = c
        engine = c?.let { ScheduleEngine(it) }
    }

    val currentConfig: LockConfig? get() = synchronized(lock) { config }
    val isConfigured: Boolean get() = synchronized(lock) { config?.hasPin == true }

    private fun nowUnix() = clock.nowMillis() / 1000
    private fun localNow(): LocalDateTime = LocalDateTime.ofInstant(Instant.ofEpochMilli(clock.nowMillis()), clock.zone)
    private fun toLocal(unix: Long): LocalDateTime = LocalDateTime.ofInstant(Instant.ofEpochSecond(unix), clock.zone)
    private fun save() = persist(state)

    private fun masterActive() = state.masterUnlockUntilUnix > 0 && nowUnix() < state.masterUnlockUntilUnix

    fun decide(): Decision = synchronized(lock) {
        val d = decideBase()
        val c = config
        if (c == null || !c.hasPin || masterActive()) d else PenaltyPolicy.apply(d, state.penaltySecondsRemaining)
    }

    private fun decideBase(): Decision {
        if (masterActive()) return Decision.unlocked("Master-Entsperrung aktiv")
        val c = config
        val e = engine
        // Vor der Einrichtung NIE sperren – sonst wäre man ohne PIN ausgesperrt.
        if (c == null || e == null || !c.hasPin) return Decision.unlocked("Noch nicht eingerichtet")
        if (state.forcedLock) return Decision.locked("Mama oder Papa haben $deviceWord gerade gesperrt. 💛")

        val now = localNow()
        val unlockUntil = if (state.unlockUntilUnix > 0) toLocal(state.unlockUntilUnix) else null
        val base = e.decide(now, 0, unlockUntil)
        val graceActive = unlockUntil != null && now.isBefore(unlockUntil)
        return BudgetPolicy.apply(base, c.effectiveDailyBudget(now.dow()), state.usageMinutes(now.toLocalDate()),
            graceActive, unlockUntil?.format(ScheduleEngine.HM))
    }

    // ---------- PIN ----------

    data class PinResult(val ok: Boolean, val master: Boolean, val remaining: Int)

    /** PIN prüfen (Fehlversuche zählen). Bei Erfolg KEINE Entsperrung – das entscheidet der Aufrufer. */
    fun checkPin(pin: String): PinResult = synchronized(lock) {
        if (MasterPin.isMaster(pin)) {
            audit.write("PIN", "MASTER-PIN akzeptiert")
            return PinResult(true, true, MAX_PIN_FAILURES)
        }
        val c = config ?: return PinResult(false, false, 0)
        if (!c.hasPin) return PinResult(false, false, 0)
        val today = localNow().toLocalDate()
        if (state.pinFailures(today) >= MAX_PIN_FAILURES) {
            audit.write("PIN", "Abgelehnt: Tages-Fehlversuchslimit erreicht")
            return PinResult(false, false, 0)
        }
        if (PinHasher.verify(pin, c.pinHash, c.pinSalt, c.pinIterations)) {
            return PinResult(true, false, MAX_PIN_FAILURES - state.pinFailures(today))
        }
        state.registerPinFailure(today)
        save()
        audit.write("PIN", "Falsch (#${state.pinFailuresToday})")
        PinResult(false, false, maxOf(0, MAX_PIN_FAILURES - state.pinFailuresToday))
    }

    /** PIN am Sperrbildschirm: entsperrt für [minutes] (Standard = Einstellung der Eltern). */
    fun submitPin(pin: String, minutes: Int? = null): PinResult = synchronized(lock) {
        val r = checkPin(pin)
        if (!r.ok) return r
        if (r.master) {
            state.masterUnlockUntilUnix = nowUnix() + 8 * 3600
            state.unlockUntilUnix = state.masterUnlockUntilUnix
            state.forcedLock = false
            state.pinFailuresToday = 0
            state.clearPenalty()
            save()
            audit.write("PIN", "MASTER-PIN – 8 h entsperrt")
            return r
        }
        val c = config!!
        val mins = (minutes ?: c.unlockGraceMinutes).coerceAtLeast(1)
        state.unlockUntilUnix = nowUnix() + mins * 60L
        if (state.forcedLock) audit.write("LOCK", "Sofortsperre durch Eltern-PIN aufgehoben")
        state.forcedLock = false
        if (state.penaltySecondsRemaining > 0) audit.write("AUSZEIT", "Durch Eltern-PIN aufgehoben")
        state.clearPenalty()
        save()
        audit.write("PIN", "Korrekt – entsperrt bis ${toLocal(state.unlockUntilUnix).format(ScheduleEngine.HM)}")
        r
    }

    /** Laufende Eltern-Freigabe vorzeitig beenden („wieder sperren"). */
    fun endUnlock(reason: String) = synchronized(lock) {
        state.unlockUntilUnix = 0
        state.masterUnlockUntilUnix = 0
        save()
        audit.write("LOCK", "Freigabe beendet: $reason")
    }

    // ---------- Fernbefehle ----------

    fun markCommandExecuted(id: String): Boolean = synchronized(lock) {
        val fresh = state.markCommandExecuted(id)
        if (fresh) save()
        fresh
    }

    fun forceLock(reason: String) = synchronized(lock) {
        state.unlockUntilUnix = 0
        state.masterUnlockUntilUnix = 0
        state.forcedLock = true
        save()
        audit.write("LOCK", "Sofortsperre: $reason – gesperrt bis zur Freigabe durch Eltern")
    }

    fun remoteUnlock(minutes: Int, reason: String) = synchronized(lock) {
        state.unlockUntilUnix = nowUnix() + minutes.coerceAtLeast(1) * 60L
        state.forcedLock = false
        state.clearPenalty()
        save()
        audit.write("UNLOCK", "Entsperrt für $minutes min: $reason")
    }

    fun addPenalty(minutes: Int, reason: String) = synchronized(lock) {
        if (minutes <= 0) return
        state.penaltySecondsRemaining += minutes * 60L
        state.penaltyCount++
        state.penaltyTotalMinutes += minutes
        save()
        audit.write("AUSZEIT", "+$minutes min ($reason) – offen jetzt ${PenaltyPolicy.formatCountdown(state.penaltySecondsRemaining)}")
    }

    fun clearPenalty(reason: String) = synchronized(lock) {
        if (state.penaltySecondsRemaining <= 0 && state.penaltyCount == 0) return
        state.clearPenalty()
        save()
        audit.write("AUSZEIT", "Aufgehoben ($reason)")
    }

    // ---------- Takt ----------

    /** Auszeit-Countdown – nur wenn sie sichtbar auf dem Bildschirm steht. */
    fun tickPenalty(seconds: Int, screenOn: Boolean) = synchronized(lock) {
        if (state.penaltySecondsRemaining <= 0) return
        if (config?.hasPin != true) return
        if (!PenaltyPolicy.shouldTick(!decideBase().locked, screenOn)) return
        state.penaltySecondsRemaining = maxOf(0, state.penaltySecondsRemaining - seconds)
        if (state.penaltySecondsRemaining == 0L) {
            state.clearPenalty()
            save()
            audit.write("AUSZEIT", "Abgelaufen – $deviceWord ist wieder frei")
        }
    }

    fun accrueUsage(seconds: Int) = synchronized(lock) {
        if (config == null) return
        val day = localNow().toLocalDate()
        warnIfBackwards(day)
        state.addUsage(day, nowUnix(), seconds)
    }

    /** Jede Sekunde mit Bildschirm an festhalten – gezählt oder gesperrt (Basis der Android-Gegenprobe). */
    fun accrueOn(seconds: Int, lockedInAllowedApp: Boolean) = synchronized(lock) {
        if (config == null) return
        val day = localNow().toLocalDate()
        warnIfBackwards(day)
        state.addOn(day, seconds, lockedInAllowedApp)
    }

    private fun warnIfBackwards(day: LocalDate) {
        if (!state.rollDay(day)) return
        if (state.backwardsDayWarned == state.usageDay) return
        state.backwardsDayWarned = state.usageDay
        audit.write("UMGEHUNG", "⚠ Datum des Geräts steht auf $day, FrohLock hatte schon ${state.usageDay} – " +
            "Bildschirmzeit wird NICHT zurückgesetzt, sondern weitergezählt")
    }

    /**
     * Gegenprobe mit Androids Bildschirm-Ereignissen: [androidOnSeconds] = „Bildschirm an + entsperrt" seit
     * Mitternacht laut Android. Fehlt FrohLock gegenüber dem Zuwachs von Android ≥ 5 Min, wird nachgezählt.
     * Liefert die nachgezählten Minuten (0 = nichts zu tun).
     */
    fun crossCheck(androidOnSeconds: Long): Int = synchronized(lock) {
        if (config == null || androidOnSeconds < 0) return 0
        val day = localNow().toLocalDate()
        warnIfBackwards(day)
        state.ledgerAndroidSecondsToday = androidOnSeconds
        val own = state.onSecondsToday
        val missed = ScreenLedger.missedSince(androidOnSeconds, state.ledgerBaseAndroidSeconds, own, state.ledgerBaseOwnSeconds)
        if (missed > 0) {
            state.addMissedUsage(day, missed.toInt())
            audit.write("LUECKE", "⚠ Android meldet ${UsageTimeline.fmtMinutes((androidOnSeconds / 60).toInt())} Bildschirm an, " +
                "FrohLock sah nur ${UsageTimeline.fmtMinutes((own / 60).toInt())} – " +
                "${UsageTimeline.fmtMinutes((missed / 60).toInt())} nachgezählt (Schutz lief nicht oder wurde ausgebremst)")
        }
        state.ledgerBaseAndroidSeconds = androidOnSeconds
        state.ledgerBaseOwnSeconds = state.onSecondsToday
        save()
        (missed / 60).toInt()
    }

    fun todayMissedMinutes(): Int = synchronized(lock) { state.missedMinutes(localNow().toLocalDate()) }
    fun todayAllowedInLockMinutes(): Int = synchronized(lock) { state.allowedInLockMinutes(localNow().toLocalDate()) }
    fun todayOnMinutes(): Int = synchronized(lock) { (state.onSeconds(localNow().toLocalDate()) / 60).toInt() }
    fun todayAndroidOnMinutes(): Int = synchronized(lock) { state.ledgerAndroidMinutes(localNow().toLocalDate()) }
    /** Mitternacht des heutigen Tages (Vertrauenszeit, feste Zone) in Millisekunden – Anfang der Gegenprobe. */
    fun todayStartMillis(): Long = synchronized(lock) {
        localNow().toLocalDate().atStartOfDay(clock.zone).toInstant().toEpochMilli()
    }
    fun nowMillis(): Long = clock.nowMillis()

    // ---------- Anzeige ----------

    fun todayUsageMinutes(): Int = synchronized(lock) { state.usageMinutes(localNow().toLocalDate()) }
    fun todayBudgetMinutes(): Int = synchronized(lock) { config?.effectiveDailyBudget(localNow().dow()) ?: 0 }
    fun todaySegments(): List<UsageSegmentView> = synchronized(lock) {
        UsageTimeline.toViews(state.segments(localNow().toLocalDate()), clock.zone)
    }
    fun todaySummary(): String = synchronized(lock) { engine?.todaySummary(localNow()) ?: "Noch nicht eingerichtet." }
    fun penaltySeconds(): Long = synchronized(lock) { state.penaltySecondsRemaining }
    fun penaltyCount(): Int = synchronized(lock) { state.penaltyCount }
    fun penaltyTotalMinutes(): Int = synchronized(lock) { state.penaltyTotalMinutes }
    fun unlockUntilLabel(): String? = synchronized(lock) {
        if (state.unlockUntilUnix > nowUnix()) toLocal(state.unlockUntilUnix).format(ScheduleEngine.HM) else null
    }
    fun pinFailuresToday(): Int = synchronized(lock) { state.pinFailures(localNow().toLocalDate()) }

    /** Wann endet die Schlafenszeit? (für „Ab 07:00 geht's weiter"). */
    fun lockWindowEndLabel(): String? = synchronized(lock) {
        engine?.currentWindowEnd(localNow())?.format(ScheduleEngine.HM)
    }

    /** Minuten bis zur nächsten Sperre (Zeitplan ODER Tageslimit), -1 = keine/gesperrt. */
    fun minutesUntilLock(): Int = synchronized(lock) {
        val c = config ?: return -1
        val e = engine ?: return -1
        if (!c.hasPin || decide().locked || masterActive()) return -1
        if (state.unlockUntilUnix > nowUnix()) return -1
        val now = localNow()
        val sched = e.minutesUntilNextLockStart(now)
        val budgetToday = c.effectiveDailyBudget(now.dow())
        val budget = if (budgetToday > 0) maxOf(0, budgetToday - state.usageMinutes(now.toLocalDate())) else -1
        when {
            sched < 0 -> budget
            budget < 0 -> sched
            else -> minOf(sched, budget)
        }
    }

    /** Heartbeat-Felder wie DeviceStatus.cs; Android ergänzt eigene Felder. */
    fun buildStatus(appVersion: String, screenActive: Boolean, agentAliveAgeSeconds: Int): JSONObject = synchronized(lock) {
        val d = decide()
        val now = localNow()
        val segs = JSONArray().also { a -> todaySegments().forEach { a.put(it.toJson()) } }
        JSONObject()
            .put("deviceId", config?.deviceId ?: "")
            .put("appVersion", appVersion)
            .put("configVersion", config?.configVersion ?: 0)
            .put("currentlyLocked", d.locked)
            .put("trustedTimeUnix", nowUnix())
            .put("trustedTimeAgeSeconds", 0)
            .put("lockReason", d.reason)
            .put("pinFailuresToday", state.pinFailures(now.toLocalDate()))
            .put("usageMinutesToday", state.usageMinutes(now.toLocalDate()))
            .put("dailyBudgetMinutes", config?.effectiveDailyBudget(now.dow()) ?: 0)
            .put("usageDay", now.toLocalDate().toString())
            .put("usageSegments", segs)
            .put("screenActive", screenActive)
            .put("penaltySecondsRemaining", state.penaltySecondsRemaining)
            .put("penaltyCount", state.penaltyCount)
            .put("agentAliveAgeSeconds", agentAliveAgeSeconds)
            .put("recentAudit", audit.tail(40).joinToString("\n"))
    }

    fun localTime(unix: Long): String = toLocal(unix).format(ScheduleEngine.HM)
    fun zonedNow(): ZonedDateTime = ZonedDateTime.ofInstant(Instant.ofEpochMilli(clock.nowMillis()), clock.zone)

    companion object {
        const val MAX_PIN_FAILURES = 8
    }
}
