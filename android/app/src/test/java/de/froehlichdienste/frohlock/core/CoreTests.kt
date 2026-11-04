package de.froehlichdienste.frohlock.core

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.LocalDateTime
import java.time.ZoneId
import java.time.ZoneOffset

private fun fixture(): JSONObject =
    JSONObject(CoreTests::class.java.getResource("/xlang.json")!!.readText())

private fun cfg(windows: List<ScheduleWindow> = emptyList(), budget: Int = 0, pin: Boolean = true) = LockConfig(
    configVersion = 1, deviceId = "t", windows = windows, dailyBudgetMinutes = budget, dailyBudgetByWeekday = emptyList(),
    unlockGraceMinutes = 60,
    pinHash = if (pin) PinHasher.hash("2468", 1000).first else "",
    pinSalt = "", pinIterations = 1000, serverBaseUrl = "", tlsSpkiPins = emptyList(), serverPollSeconds = 30,
    maxTrustedTimeStalenessMinutes = 720,
)

private class FakeClock(var t: LocalDateTime) : TrustedClock {
    override val zone: ZoneId = ZoneOffset.UTC
    override fun nowMillis() = t.toInstant(ZoneOffset.UTC).toEpochMilli()
}

private class MemAudit : Audit {
    val lines = mutableListOf<String>()
    override fun write(category: String, message: String) { lines.add("$category\t$message") }
    override fun tail(n: Int) = lines.takeLast(n)
}

class CoreTests {
    // 2026-11-02 ist ein Montag.
    private val monday2130 = LocalDateTime.of(2026, 11, 2, 21, 30)
    private val night = ScheduleWindow("n", null, listOf(1), 1260, 420)   // Mo 21:00 – Di 07:00

    @Test fun overnightWindowBelongsToStartDay() {
        val e = ScheduleEngine(cfg(listOf(night)))
        assertTrue(e.decide(monday2130, 0, null).locked)
        assertTrue(e.decide(LocalDateTime.of(2026, 11, 3, 6, 59), 0, null).locked)   // Di früh
        assertFalse(e.decide(LocalDateTime.of(2026, 11, 3, 7, 0), 0, null).locked)
        assertFalse(e.decide(LocalDateTime.of(2026, 11, 3, 21, 30), 0, null).locked) // Di abends nicht
        assertEquals("07:00", e.currentWindowEnd(monday2130)!!.format(ScheduleEngine.HM))
        assertEquals(30, e.minutesUntilNextLockStart(LocalDateTime.of(2026, 11, 2, 20, 30)))
    }

    @Test fun budgetLocksAfterLimitButGraceWins() {
        assertTrue(BudgetPolicy.apply(Decision.unlocked("x"), 60, 60, false, null).locked)
        assertFalse(BudgetPolicy.apply(Decision.unlocked("x"), 60, 59, false, null).locked)
        assertFalse(BudgetPolicy.apply(Decision.unlocked("x"), 60, 80, true, "18:00").locked)
        assertFalse(BudgetPolicy.apply(Decision.unlocked("x"), 0, 999, false, null).locked)
    }

    @Test fun notConfiguredNeverLocks() {
        val clock = FakeClock(monday2130)
        val en = Enforcer(clock, MemAudit(), RuntimeState(), {})
        en.setConfig(cfg(listOf(night), pin = false))
        assertFalse(en.decide().locked)
        en.forceLock("test")
        assertFalse("ohne PIN nie sperren", en.decide().locked)
    }

    @Test fun forcedLockPenaltyAndPinFlow() {
        val clock = FakeClock(LocalDateTime.of(2026, 11, 2, 15, 0))
        val (h, s) = PinHasher.hash("2468", 1000)
        val c = cfg().copy(pinHash = h, pinSalt = s)
        val en = Enforcer(clock, MemAudit(), RuntimeState(), {})
        en.setConfig(c)
        assertFalse(en.decide().locked)

        en.forceLock("Fern")
        assertTrue(en.decide().locked)
        assertFalse(en.submitPin("1111").ok)
        assertTrue(en.submitPin("2468").ok)
        assertFalse(en.decide().locked)

        en.endUnlock("test")
        en.addPenalty(15, "gestört")
        assertTrue(PenaltyPolicy.isPenaltyReason(en.decide().reason))
        en.tickPenalty(600, screenOn = false)          // Bildschirm aus: Countdown steht
        assertEquals(900L, en.penaltySeconds())
        en.tickPenalty(900, screenOn = true)
        assertFalse(en.decide().locked)
    }

    @Test fun pinLockoutAfterEightFailures() {
        val clock = FakeClock(LocalDateTime.of(2026, 11, 2, 15, 0))
        val (h, s) = PinHasher.hash("2468", 1000)
        val en = Enforcer(clock, MemAudit(), RuntimeState(), {})
        en.setConfig(cfg().copy(pinHash = h, pinSalt = s))
        repeat(8) { assertFalse(en.checkPin("0000").ok) }
        assertFalse("nach 8 Fehlversuchen auch richtige PIN gesperrt", en.checkPin("2468").ok)
    }

    @Test fun usageCountsTowardBudget() {
        val clock = FakeClock(LocalDateTime.of(2026, 11, 2, 15, 0))
        val (h, s) = PinHasher.hash("2468", 1000)
        val en = Enforcer(clock, MemAudit(), RuntimeState(), {})
        en.setConfig(cfg(budget = 2).copy(pinHash = h, pinSalt = s))
        repeat(24) { clock.t = clock.t.plusSeconds(5); en.accrueUsage(5) }
        assertTrue(en.decide().locked)
        assertEquals(1, en.todaySegments().size)
        assertEquals("15:00", en.todaySegments()[0].start)
    }

    @Test fun pbkdf2MatchesPython() {
        val f = fixture()
        val c = LockConfig.fromJson(SignatureVerifier(f.getString("publicKeyPem")).openJson(SignedEnvelope.fromJson(f.getJSONObject("config")))!!)
        assertTrue(PinHasher.verify(f.getString("pin"), c.pinHash, c.pinSalt, c.pinIterations))
        assertFalse(PinHasher.verify("2469", c.pinHash, c.pinSalt, c.pinIterations))
    }

    @Test fun serverSignaturesVerify() {
        val f = fixture()
        val v = SignatureVerifier(f.getString("publicKeyPem"))
        val c = LockConfig.fromJson(v.openJson(SignedEnvelope.fromJson(f.getJSONObject("config")))!!)
        assertEquals(7L, c.configVersion)
        assertEquals(1260, c.windows[0].startMinute)
        assertEquals(180, c.effectiveDailyBudget(6))
        assertEquals(90, c.effectiveDailyBudget(3))

        val cmd = DeviceCommand.fromJson(v.openJson(SignedEnvelope.fromJson(f.getJSONObject("command")))!!)
        assertEquals(CommandType.TIMEOUT, cmd.type)
        assertEquals(15, cmd.unlockMinutes)

        assertNull("manipulierte Nutzlast", v.openJson(SignedEnvelope.fromJson(f.getJSONObject("tampered"))))
        assertTrue(v.verify("android|1.2.3|abc".toByteArray(), java.util.Base64.getDecoder().decode(f.getString("manifestSignature"))))
        assertFalse(v.verify("android|1.2.4|abc".toByteArray(), java.util.Base64.getDecoder().decode(f.getString("manifestSignature"))))
    }

    @Test fun productionKeyParses() {
        assertNotNull(SignatureVerifier())
    }

    @Test fun windowDaysAcceptNamesAndNumbers() {
        val w = ScheduleWindow.fromJson(JSONObject("""{"days":["monday",3,"6"],"startMinute":1,"endMinute":2}"""))
        assertEquals(listOf(1, 3, 6), w.days)
    }

    @Test fun stateRoundTrip() {
        val s = RuntimeState().apply { forcedLock = true; penaltySecondsRemaining = 42; suspendedPackages.add("a.b") }
        s.addUsage(java.time.LocalDate.of(2026, 11, 2), 1000, 30)
        val r = RuntimeState.fromJson(JSONObject(s.toJson().toString()))
        assertTrue(r.forcedLock); assertEquals(42L, r.penaltySecondsRemaining)
        assertEquals(listOf("a.b"), r.suspendedPackages); assertEquals(1, r.usageSegmentsToday.size)
    }

    // ---------- 1.0.5: Gegenprobe mit Android, Tag rückwärts, Messenger-Zeit ----------

    @Test fun screenLedgerCountsOnlyInteractiveAndUnlocked() {
        val L = ScreenLedger
        fun ev(t: Long, type: Int) = ScreenLedger.Ev(t, type)
        val ev = listOf(
            ev(1_000, L.SCREEN_ON), ev(1_000, L.KEYGUARD_SHOWN),   // an, aber Sperrbildschirm
            ev(11_000, L.KEYGUARD_HIDDEN),                          // ab 11 s wirklich benutzbar
            ev(71_000, L.SCREEN_OFF),                               // 60 s benutzt
            ev(200_000, L.SCREEN_ON), ev(200_000, L.KEYGUARD_SHOWN), // nur Uhrzeit geguckt
            ev(205_000, L.SCREEN_OFF),
        )
        assertEquals(60L, L.interactiveUnlockedSeconds(ev, 0, 300_000))
        // Zustand vor dem Fenster zählt: ab 11 s offen, Fenster beginnt bei 31 s → 40 s
        assertEquals(40L, L.interactiveUnlockedSeconds(ev, 31_000, 300_000))
        // Ohne jedes Sperrbildschirm-Ereignis (Gerät ohne Sperre): „an" allein zählt
        val noKg = listOf(ev(0, L.SCREEN_ON), ev(30_000, L.SCREEN_OFF))
        assertEquals(30L, L.interactiveUnlockedSeconds(noKg, 0, 100_000))
        // Unbekannter Anfangszustand = aus
        assertEquals(0L, L.interactiveUnlockedSeconds(emptyList(), 0, 100_000))
    }

    @Test fun crossCheckAddsOnlyGrowthSinceLastCheckAndOnlyAboveThreshold() {
        val clock = FakeClock(LocalDateTime.of(2026, 11, 4, 12, 0))
        val audit = MemAudit()
        val st = RuntimeState()
        val en = Enforcer(clock, audit, st, {})
        en.setConfig(cfg(budget = 60))
        en.accrueUsage(600); en.accrueOn(600, false)   // FrohLock zählte 10 Min (Dienst ruft beides je Takt)
        assertEquals(10, en.todayUsageMinutes())
        // Erster Abgleich mitten am Tag: nur Basis setzen, nie nachzählen (Update mitten am Tag).
        assertEquals(0, en.crossCheck(3_600))
        // Android wächst um 20 Min, FrohLock sah nur 2 Min → 18 Min nachgezählt
        en.accrueUsage(120); en.accrueOn(120, false)
        assertEquals(18, en.crossCheck(3_600 + 1_200))
        assertEquals(10 + 2 + 18, en.todayUsageMinutes())
        assertEquals(18, en.todayMissedMinutes())
        assertTrue(audit.lines.any { it.startsWith("LUECKE\t⚠") && it.contains("nachgezählt") })
        // Kleine Takt-Abweichung (< 5 Min) wird ignoriert
        en.accrueUsage(100); en.accrueOn(100, false)
        assertEquals(0, en.crossCheck(3_600 + 1_200 + 100 + 200))
        assertEquals(31, en.todayUsageMinutes())
        // Nachgezählte Zeit zählt aufs Limit
        en.accrueUsage(30 * 60)
        assertTrue(en.decide().locked)
    }

    @Test fun backwardsDayDoesNotResetUsage() {
        val clock = FakeClock(LocalDateTime.of(2026, 11, 4, 20, 0))
        val audit = MemAudit()
        val st = RuntimeState()
        val en = Enforcer(clock, audit, st, {})
        en.setConfig(cfg(budget = 60))
        en.accrueUsage(50 * 60)
        assertEquals(50, en.todayUsageMinutes())
        // Uhr/Zeitzone zurückgedreht: „gestern"
        clock.t = LocalDateTime.of(2026, 11, 3, 23, 0)
        assertEquals(50, en.todayUsageMinutes())
        en.accrueUsage(10 * 60)
        assertEquals(60, en.todayUsageMinutes())
        assertTrue(en.decide().locked)
        assertEquals(1, audit.lines.count { it.contains("NICHT zurückgesetzt") })
        // Echter neuer Tag setzt zurück
        clock.t = LocalDateTime.of(2026, 11, 5, 8, 0)
        assertEquals(0, en.todayUsageMinutes())
        assertFalse(en.decide().locked)
    }

    @Test fun allowedAppTimeInLockIsReportedButNotCounted() {
        val clock = FakeClock(LocalDateTime.of(2026, 11, 4, 12, 0))
        val st = RuntimeState()
        val en = Enforcer(clock, MemAudit(), st, {})
        en.setConfig(cfg(budget = 60))
        en.accrueOn(300, lockedInAllowedApp = true)
        en.accrueOn(120, lockedInAllowedApp = false)
        assertEquals(5, en.todayAllowedInLockMinutes())
        assertEquals(7, en.todayOnMinutes())
        assertEquals(0, en.todayUsageMinutes())
    }

    @Test fun stateKeepsNewCountersAndDefaultsOnSecondsForOldState() {
        val s = RuntimeState()
        s.addUsage(java.time.LocalDate.of(2026, 11, 4), 1000, 600)
        s.addOn(java.time.LocalDate.of(2026, 11, 4), 700, true)
        s.addMissedUsage(java.time.LocalDate.of(2026, 11, 4), 300)
        val r = RuntimeState.fromJson(JSONObject(s.toJson().toString()))
        assertEquals(900L, r.usageSecondsToday); assertEquals(1000L, r.onSecondsToday)
        assertEquals(700L, r.allowedInLockSecondsToday); assertEquals(300L, r.missedSecondsToday)
        // Zustand aus 1.0.4 (ohne die Felder): Gesehenes = Gezähltes
        val old = RuntimeState.fromJson(JSONObject("""{"usageDay":"2026-11-04","usageSecondsToday":1234}"""))
        assertEquals(1234L, old.onSecondsToday); assertEquals(-1L, old.ledgerBaseAndroidSeconds)
    }
}
