import Foundation

/// Persistierter Laufzeitzustand (App + Erweiterungen teilen ihn über die App Group). Keine Klartext-Geheimnisse.
public struct RuntimeState: Codable, Equatable, Sendable {
    public var unlockUntilUnix: Int64 = 0
    public var masterUnlockUntilUnix: Int64 = 0
    /// „Sofort sperren" der Eltern: gilt bis Eltern-PIN, Fern-Entsperren oder Master-PIN – spätestens bis zum nächsten Tageswechsel.
    public var forcedLock = false
    /// Tag (yyyy-MM-dd), an dem die Sofortsperre gesetzt wurde. Leer = unbekannt (alter Zustand) → beim nächsten Aufräumen auf heute gestempelt.
    public var forcedLockDay = ""
    public var pinFailuresToday = 0
    public var pinFailureDay = ""
    /// Eigene Bildschirmzeit heute (iOS meldet sie in 5-Minuten-Schritten über Bildschirmzeit-Ereignisse).
    public var usageDay = ""
    public var usageMinutesToday = 0
    /// Gemeinsames Tageskonto: Minuten der anderen Geräte des Kindes.
    public var shared: SharedUsage?
    /// Auszeit („gestört ohne Notfall") – auf iOS als feste Endzeit.
    public var penaltyUntilUnix: Int64 = 0
    public var penaltyCount = 0
    public var executedCommandIds: [String] = []
    /// Letzte Entscheidung (für den Sperrbildschirm der Erweiterung).
    public var lastLocked = false
    public var lastReason = ""

    public init() {}

    /// Toleranter Decoder: Zustand aus älteren Versionen (ohne neue Felder) muss weiter laden –
    /// sonst fiele `Store.state()` auf einen leeren Zustand zurück und Entsperrung/Nutzung/Auszeit gingen verloren.
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        unlockUntilUnix = try c.decodeIfPresent(Int64.self, forKey: .unlockUntilUnix) ?? 0
        masterUnlockUntilUnix = try c.decodeIfPresent(Int64.self, forKey: .masterUnlockUntilUnix) ?? 0
        forcedLock = try c.decodeIfPresent(Bool.self, forKey: .forcedLock) ?? false
        forcedLockDay = try c.decodeIfPresent(String.self, forKey: .forcedLockDay) ?? ""
        pinFailuresToday = try c.decodeIfPresent(Int.self, forKey: .pinFailuresToday) ?? 0
        pinFailureDay = try c.decodeIfPresent(String.self, forKey: .pinFailureDay) ?? ""
        usageDay = try c.decodeIfPresent(String.self, forKey: .usageDay) ?? ""
        usageMinutesToday = try c.decodeIfPresent(Int.self, forKey: .usageMinutesToday) ?? 0
        shared = try c.decodeIfPresent(SharedUsage.self, forKey: .shared)
        penaltyUntilUnix = try c.decodeIfPresent(Int64.self, forKey: .penaltyUntilUnix) ?? 0
        penaltyCount = try c.decodeIfPresent(Int.self, forKey: .penaltyCount) ?? 0
        executedCommandIds = try c.decodeIfPresent([String].self, forKey: .executedCommandIds) ?? []
        lastLocked = try c.decodeIfPresent(Bool.self, forKey: .lastLocked) ?? false
        lastReason = try c.decodeIfPresent(String.self, forKey: .lastReason) ?? ""
    }

    public mutating func markExecuted(_ id: String) -> Bool {
        if id.isEmpty { return true }
        if executedCommandIds.contains(id) { return false }
        executedCommandIds.append(id)
        if executedCommandIds.count > 100 { executedCommandIds.removeFirst(executedCommandIds.count - 100) }
        return true
    }

    /// Neuer Tag → Nutzung auf null. Liegt der „neue" Tag VOR dem gespeicherten (Uhr zurückgedreht): nicht zurücksetzen.
    public mutating func rollDay(_ day: String) {
        if usageDay == day { return }
        if !usageDay.isEmpty && usageDay > day { return }
        usageDay = day; usageMinutesToday = 0
    }

    public func ownMinutes(_ day: String) -> Int { (usageDay == day || (!usageDay.isEmpty && usageDay > day)) ? usageMinutesToday : 0 }
    public func otherMinutes(_ day: String) -> Int { SharedUsage.othersToday(shared, today: day) }
    public func budgetUsed(_ day: String) -> Int { ownMinutes(day) + otherMinutes(day) }
}

/// Sperr-Gehirn – Port von Enforcer.kt. Reihenfolge: Master > nicht eingerichtet > Sofortsperre (nur bis zum Tageswechsel) >
/// Zeitplan/Entsperrung > Tageslimit > Auszeit. Reine Logik: Zustand rein, Entscheidung raus.
public struct Enforcer {
    public static let maxPinFailures = 8
    public var config: LockConfig?
    public var state: RuntimeState
    public var clock: LocalClock

    public init(config: LockConfig?, state: RuntimeState, clock: LocalClock = LocalClock()) {
        self.config = config; self.state = state; self.clock = clock
    }

    func unix(_ d: Date) -> Int64 { Int64(d.timeIntervalSince1970) }
    func date(_ u: Int64) -> Date { Date(timeIntervalSince1970: TimeInterval(u)) }

    public var isConfigured: Bool { config?.hasPin == true }

    public func decide(_ now: Date) -> Decision {
        if state.masterUnlockUntilUnix > unix(now) { return .unlocked("Master-Entsperrung aktiv") }
        guard let c = config, c.hasPin else { return .unlocked("Noch nicht eingerichtet") }
        // Sofortsperre gilt nur bis zum Tageswechsel – rein lesend, auch bevor `expireForcedLock` den Zustand bereinigt hat.
        // Leerer Tag (alter Zustand) zählt als „heute gesetzt"; zurückgedrehte Uhr (heute < Setz-Tag) bleibt gesperrt.
        if state.forcedLock && (state.forcedLockDay.isEmpty || clock.dayKey(now) <= state.forcedLockDay) {
            return .locked("Mama oder Papa haben das iPhone für heute gesperrt. Morgen geht es weiter. 💛")
        }
        let unlockUntil = state.unlockUntilUnix > 0 ? date(state.unlockUntilUnix) : nil
        let engine = ScheduleEngine(config: c, clock: clock)
        let base = engine.decide(now, unlockUntil: unlockUntil)
        let day = clock.dayKey(now)
        let grace = unlockUntil.map { now < $0 } ?? false
        let d = BudgetPolicy.apply(base, budget: c.effectiveDailyBudget(dow: clock.dow(now)), used: state.budgetUsed(day),
                                   graceActive: grace, graceUntil: unlockUntil.map { clock.hm($0) },
                                   otherMinutes: state.otherMinutes(day))
        if !d.locked && state.penaltyUntilUnix > unix(now) {
            let left = Int(state.penaltyUntilUnix - unix(now))
            return .locked("Auszeit: noch \(left / 60):" + String(format: "%02d", left % 60))
        }
        return d
    }

    /// Wann ändert sich die Entscheidung frühestens von selbst? (Entsperrung/Auszeit endet, Sperrzeit beginnt/endet,
    /// Mitternacht – dann endet auch die Sofortsperre.) iOS weckt FrohLock zu diesem Zeitpunkt über einen Bildschirmzeit-Zeitplan.
    public func nextTransition(_ now: Date) -> Date? {
        var cands: [Date] = []
        for u in [state.unlockUntilUnix, state.penaltyUntilUnix, state.masterUnlockUntilUnix] where u > unix(now) {
            cands.append(date(u))
        }
        if let c = config {
            let e = ScheduleEngine(config: c, clock: clock)
            if let end = e.currentWindowEnd(now) { cands.append(end) }
            let m = e.minutesUntilNextLockStart(now)
            if m > 0 { cands.append(now.addingTimeInterval(Double(m) * 60)) }
        }
        if let midnight = clock.calendar.date(byAdding: .day, value: 1, to: clock.startOfDay(now)) { cands.append(midnight) }
        return cands.min()
    }

    public func minutesUntilLock(_ now: Date) -> Int {
        guard let c = config, c.hasPin, !decide(now).locked, state.unlockUntilUnix <= unix(now) else { return -1 }
        let sched = ScheduleEngine(config: c, clock: clock).minutesUntilNextLockStart(now)
        let b = c.effectiveDailyBudget(dow: clock.dow(now))
        let budget = b > 0 ? max(0, b - state.budgetUsed(clock.dayKey(now))) : -1
        if sched < 0 { return budget }
        if budget < 0 { return sched }
        return min(sched, budget)
    }

    // ---------- PIN ----------

    public enum PinResult: Equatable { case ok, master, wrong(remaining: Int), blocked }

    public mutating func submitPin(_ pin: String, minutes: Int?, now: Date) -> PinResult {
        if MasterPin.isMaster(pin) {
            state.masterUnlockUntilUnix = unix(now) + 8 * 3600
            state.unlockUntilUnix = state.masterUnlockUntilUnix
            state.forcedLock = false; state.forcedLockDay = ""; state.penaltyUntilUnix = 0; state.penaltyCount = 0; state.pinFailuresToday = 0
            return .master
        }
        guard let c = config, c.hasPin else { return .blocked }
        let day = clock.dayKey(now)
        if state.pinFailureDay != day { state.pinFailureDay = day; state.pinFailuresToday = 0 }
        if state.pinFailuresToday >= Self.maxPinFailures { return .blocked }
        guard PinHasher.verify(pin, hash: c.pinHash, salt: c.pinSalt, iterations: c.pinIterations) else {
            state.pinFailuresToday += 1
            return .wrong(remaining: max(0, Self.maxPinFailures - state.pinFailuresToday))
        }
        state.unlockUntilUnix = unix(now) + Int64(max(1, minutes ?? c.unlockGraceMinutes)) * 60
        state.forcedLock = false; state.forcedLockDay = ""; state.penaltyUntilUnix = 0; state.penaltyCount = 0
        return .ok
    }

    public mutating func endUnlock() { state.unlockUntilUnix = 0; state.masterUnlockUntilUnix = 0 }

    // ---------- Fernbefehle ----------

    /// Sofortsperre der Eltern: gilt bis PIN/Fern-Entsperren, endet aber spätestens mit dem nächsten Tageswechsel.
    public mutating func forceLock(now: Date) {
        state.unlockUntilUnix = 0; state.masterUnlockUntilUnix = 0
        state.forcedLock = true; state.forcedLockDay = clock.dayKey(now)
    }

    /// Räumt eine abgelaufene Sofortsperre auf (neuer Tag). `decide` bleibt rein lesend und wertet den Tag selbst richtig aus;
    /// diese Methode bringt den gespeicherten Zustand nach. true = Zustand geändert → speichern.
    /// - Sofortsperre ohne Tag (alter Zustand): auf heute stempeln, sie läuft dann mit dem nächsten Tageswechsel ab.
    /// - Heute ist ein späterer Tag als der Setz-Tag: Sperre beenden. Zurückgedrehte Uhr: nichts tun (bleibt gesperrt).
    public mutating func expireForcedLock(now: Date) -> Bool {
        guard state.forcedLock else { return false }
        let today = clock.dayKey(now)
        if state.forcedLockDay.isEmpty { state.forcedLockDay = today; return true }
        if today > state.forcedLockDay { state.forcedLock = false; state.forcedLockDay = ""; return true }
        return false
    }

    public mutating func remoteUnlock(minutes: Int, now: Date) {
        state.unlockUntilUnix = unix(now) + Int64(max(1, minutes)) * 60
        state.forcedLock = false; state.forcedLockDay = ""; state.penaltyUntilUnix = 0; state.penaltyCount = 0
    }

    public mutating func addPenalty(minutes: Int, now: Date) {
        guard minutes > 0 else { return }
        state.penaltyUntilUnix = max(state.penaltyUntilUnix, unix(now)) + Int64(minutes) * 60
        state.penaltyCount += 1
    }

    public mutating func clearPenalty() { state.penaltyUntilUnix = 0; state.penaltyCount = 0 }

    /// Bildschirmzeit-Ereignis „n Minuten heute erreicht" (nur steigend; Tageswechsel setzt zurück).
    public mutating func recordUsage(minutes: Int, now: Date) {
        let day = clock.dayKey(now)
        state.rollDay(day)
        if state.usageDay == day { state.usageMinutesToday = max(state.usageMinutesToday, minutes) }
    }

    /// Liefert true, wenn sich etwas geändert hat.
    public mutating func setShared(day: String, minutes: Int, detail: String, issuedUnix: Int64, now: Date) -> Bool {
        guard let next = SharedUsage.accept(state.shared, day: day, minutes: minutes, detail: detail,
                                            issuedUnix: issuedUnix, today: clock.dayKey(now)) else { return false }
        let changed = state.shared?.minutes != next.minutes || state.shared?.day != next.day || state.shared?.detail != next.detail
        state.shared = next
        return changed
    }

    /// Heartbeat-Felder wie DeviceStatus.cs / Enforcer.kt (usageMinutesToday = NUR dieses Gerät).
    public func status(now: Date, appVersion: String) -> [String: Any] {
        let d = decide(now)
        let day = clock.dayKey(now)
        return ["deviceId": config?.deviceId ?? "", "appVersion": appVersion, "configVersion": config?.configVersion ?? 0,
                "currentlyLocked": d.locked, "trustedTimeUnix": unix(now), "trustedTimeAgeSeconds": 0,
                "lockReason": d.reason, "pinFailuresToday": state.pinFailureDay == day ? state.pinFailuresToday : 0,
                "usageMinutesToday": state.ownMinutes(day), "otherDevicesMinutesToday": state.otherMinutes(day),
                "dailyBudgetMinutes": config?.effectiveDailyBudget(dow: clock.dow(now)) ?? 0, "usageDay": day,
                "usageSegments": [] as [Any], "screenActive": true,
                "penaltySecondsRemaining": max(0, state.penaltyUntilUnix - unix(now)), "penaltyCount": state.penaltyCount,
                "agentAliveAgeSeconds": 0, "platform": "ios"]
    }
}
