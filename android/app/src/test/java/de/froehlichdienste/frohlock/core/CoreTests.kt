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
}
