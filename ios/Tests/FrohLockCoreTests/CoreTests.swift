import XCTest
@testable import FrohLockCore

final class CoreTests: XCTestCase {
    let utc = LocalClock(timeZone: TimeZone(identifier: "UTC")!)

    func d(_ s: String) -> Date {   // "2026-11-20 21:30" in UTC
        let f = DateFormatter(); f.locale = Locale(identifier: "en_US_POSIX"); f.timeZone = TimeZone(identifier: "UTC")
        f.dateFormat = "yyyy-MM-dd HH:mm"; return f.date(from: s)!
    }

    func fixture() -> [String: Any] {
        let url = Bundle.module.url(forResource: "xlang", withExtension: "json")!
        return try! JSONSerialization.jsonObject(with: Data(contentsOf: url)) as! [String: Any]
    }

    func cfg(_ windows: [ScheduleWindow] = [], budget: Int = 0) -> LockConfig {
        let h = PinHasher.hash("2468", iterations: 1000)!
        return LockConfig(windows: windows, dailyBudgetMinutes: budget, pinHash: h.hash, pinSalt: h.salt, pinIterations: 1000)
    }

    func testServerSignaturesVerifyAndTamperedFails() {
        let f = fixture()
        let v = SignatureVerifier(publicKeyPem: f["publicKeyPem"] as! String)
        let c = LockConfig.from(v.openJson(SignedEnvelope.from(f["config"] as! [String: Any]))!)
        XCTAssertEqual(c.configVersion, 7)
        XCTAssertEqual(c.windows[0].startMinute, 1260)
        XCTAssertEqual(c.effectiveDailyBudget(dow: 6), 180)
        XCTAssertEqual(c.effectiveDailyBudget(dow: 3), 90)
        XCTAssertTrue(PinHasher.verify(f["pin"] as! String, hash: c.pinHash, salt: c.pinSalt, iterations: c.pinIterations))
        XCTAssertFalse(PinHasher.verify("2469", hash: c.pinHash, salt: c.pinSalt, iterations: c.pinIterations))
        let cmd = DeviceCommand.from(v.openJson(SignedEnvelope.from(f["command"] as! [String: Any]))!)
        XCTAssertFalse(cmd.commandId.isEmpty)
        XCTAssertNil(v.open(SignedEnvelope.from(f["tampered"] as! [String: Any])))
        // Produktiver Schlüssel lässt sich laden (falsche Signatur -> false, kein Absturz)
        XCTAssertFalse(SignatureVerifier().verify(Data("x".utf8), signature: Data(repeating: 1, count: 256)))
    }

    func testOvernightWindowCountsToStartDay() {
        // Fr 21:00 – 07:00, nur Freitag (5)
        let e = ScheduleEngine(config: cfg([ScheduleWindow(days: [5], startMinute: 1260, endMinute: 420)]), clock: utc)
        XCTAssertTrue(e.decide(d("2026-11-20 22:00"), unlockUntil: nil).locked)   // Fr
        XCTAssertTrue(e.decide(d("2026-11-21 06:59"), unlockUntil: nil).locked)   // Sa früh
        XCTAssertFalse(e.decide(d("2026-11-21 07:00"), unlockUntil: nil).locked)
        XCTAssertFalse(e.decide(d("2026-11-21 22:00"), unlockUntil: nil).locked)  // Sa abend nicht
        XCTAssertEqual(e.currentWindowEnd(d("2026-11-20 23:15")), d("2026-11-21 07:00"))
        XCTAssertEqual(e.minutesUntilNextLockStart(d("2026-11-20 20:30")), 30)
    }

    func testPinUnlocksAndWrongPinCounts() {
        var en = Enforcer(config: cfg([ScheduleWindow(startMinute: 1260, endMinute: 420)]), state: RuntimeState(), clock: utc)
        let now = d("2026-11-20 22:00")
        XCTAssertTrue(en.decide(now).locked)
        XCTAssertEqual(en.submitPin("1111", minutes: 30, now: now), .wrong(remaining: 7))
        XCTAssertEqual(en.submitPin("2468", minutes: 30, now: now), .ok)
        XCTAssertFalse(en.decide(now).locked)
        XCTAssertTrue(en.decide(now.addingTimeInterval(31 * 60)).locked)
        XCTAssertEqual(en.nextTransition(now), now.addingTimeInterval(30 * 60))
    }

    func testForceLockUntilPinAndNotConfiguredNeverLocks() {
        var en = Enforcer(config: cfg(), state: RuntimeState(), clock: utc)
        let now = d("2026-11-20 12:00")
        en.forceLock()
        XCTAssertTrue(en.decide(now).locked)
        en.remoteUnlock(minutes: 10, now: now)
        XCTAssertFalse(en.decide(now).locked)
        var raw = Enforcer(config: LockConfig(), state: RuntimeState(), clock: utc)
        raw.forceLock()
        XCTAssertFalse(raw.decide(now).locked, "ohne PIN nie sperren")
    }

    func testSharedDailyAccount() {
        var en = Enforcer(config: cfg(budget: 120), state: RuntimeState(), clock: utc)
        let now = d("2026-11-20 15:00")
        en.recordUsage(minutes: 80, now: now)
        XCTAssertFalse(en.decide(now).locked)
        XCTAssertEqual(en.minutesUntilLock(now), 40)
        XCTAssertTrue(en.setShared(day: "2026-11-20", minutes: 40, detail: "Laptop renat 40 Min", issuedUnix: 10, now: now))
        let dec = en.decide(now)
        XCTAssertTrue(dec.locked)
        XCTAssertTrue(dec.reason.contains("40 min am anderen Gerät"))
        XCTAssertFalse(en.setShared(day: "2026-11-20", minutes: 0, detail: "", issuedUnix: 5, now: now))   // älter
        XCTAssertFalse(en.setShared(day: "2026-11-19", minutes: 0, detail: "", issuedUnix: 50, now: now))  // anderer Tag
        XCTAssertEqual(en.status(now: now, appVersion: "1.0.0")["usageMinutesToday"] as? Int, 80)
        // neuer Tag
        XCTAssertFalse(en.decide(d("2026-11-21 09:00")).locked)
        en.recordUsage(minutes: 5, now: d("2026-11-21 09:00"))
        XCTAssertEqual(en.state.usageMinutesToday, 5)
    }

    func testPenaltyLocksForFixedTime() {
        var en = Enforcer(config: cfg(), state: RuntimeState(), clock: utc)
        let now = d("2026-11-20 15:00")
        en.addPenalty(minutes: 15, now: now)
        XCTAssertTrue(en.decide(now).locked)
        XCTAssertFalse(en.decide(now.addingTimeInterval(15 * 60 + 1)).locked)
    }

    func testCommandParsing() {
        let c = DeviceCommand.from(["commandId": "x", "type": 9, "issuedAtUnix": 5,
                                    "payload": ["day": "2026-11-20", "minutes": "30"]])
        XCTAssertEqual(c.type, CommandType.sharedUsage)
        XCTAssertEqual(c.payload["minutes"], "30")
    }

    func testStateRoundTrip() throws {
        var s = RuntimeState(); s.forcedLock = true; s.shared = SharedUsage(day: "d", minutes: 3, detail: "x", issuedUnix: 1)
        let back = try JSONDecoder().decode(RuntimeState.self, from: JSONEncoder().encode(s))
        XCTAssertEqual(back, s)
    }
}
