import Foundation

/// Lokale Uhrzeit-Bausteine (feste Zeitzone, damit Tests reproduzierbar sind).
public struct LocalClock: Sendable {
    public var calendar: Calendar
    public init(timeZone: TimeZone = .current) {
        var c = Calendar(identifier: .gregorian)
        c.timeZone = timeZone
        calendar = c
    }
    /// 0 = Sonntag … 6 = Samstag (wie .NET DayOfWeek und der Server).
    public func dow(_ d: Date) -> Int { calendar.component(.weekday, from: d) - 1 }
    public func minuteOfDay(_ d: Date) -> Int {
        let c = calendar.dateComponents([.hour, .minute], from: d)
        return (c.hour ?? 0) * 60 + (c.minute ?? 0)
    }
    public func dayKey(_ d: Date) -> String {
        let c = calendar.dateComponents([.year, .month, .day], from: d)
        return String(format: "%04d-%02d-%02d", c.year ?? 0, c.month ?? 0, c.day ?? 0)
    }
    public func hm(_ d: Date) -> String {
        let m = minuteOfDay(d)
        return String(format: "%02d:%02d", m / 60, m % 60)
    }
    public func startOfDay(_ d: Date) -> Date { calendar.startOfDay(for: d) }
}

/// Reine Zeitplan-Entscheidung, 1:1 wie ScheduleEngine (Windows/Android).
/// Fenster über Mitternacht (Start > Ende) zählen zum Starttag.
public struct ScheduleEngine: Sendable {
    public let config: LockConfig
    public let clock: LocalClock

    public init(config: LockConfig, clock: LocalClock = LocalClock()) { self.config = config; self.clock = clock }

    public static func fmt(_ minute: Int) -> String { String(format: "%02d:%02d", minute / 60, minute % 60) }

    public func decide(_ now: Date, unlockUntil: Date?) -> Decision {
        if let u = unlockUntil, now < u, matchingWindow(now) != nil {
            return .unlocked("Entsperrt bis \(clock.hm(u))")
        }
        if let w = matchingWindow(now) {
            return .locked("Sperrfenster \(Self.fmt(w.startMinute))–\(Self.fmt(w.endMinute))")
        }
        return .unlocked("Außerhalb aller Sperrfenster")
    }

    public func matchingWindow(_ now: Date) -> ScheduleWindow? {
        let minute = clock.minuteOfDay(now)
        let today = clock.dow(now)
        let yesterday = (today + 6) % 7
        return config.windows.first { w in
            guard w.enabled, w.startMinute != w.endMinute else { return false }
            if w.startMinute < w.endMinute { return w.applies(to: today) && minute >= w.startMinute && minute < w.endMinute }
            if minute >= w.startMinute && w.applies(to: today) { return true }
            return minute < w.endMinute && w.applies(to: yesterday)
        }
    }

    /// Minuten bis zum nächsten Sperrbeginn; -1 wenn schon Sperrzeit oder keine in 24 h.
    public func minutesUntilNextLockStart(_ now: Date) -> Int {
        if matchingWindow(now) != nil { return -1 }
        for i in 1...(24 * 60) where matchingWindow(now.addingTimeInterval(Double(i) * 60)) != nil { return i }
        return -1
    }

    /// Ende der laufenden Sperrzeit (für „Frei ab 07:00"), nil = keine Sperrzeit.
    public func currentWindowEnd(_ now: Date) -> Date? {
        guard matchingWindow(now) != nil else { return nil }
        let base = clock.calendar.date(bySetting: .second, value: 0, of: now) ?? now
        for i in 1...(48 * 60) {
            let t = base.addingTimeInterval(Double(i) * 60)
            if matchingWindow(t) == nil { return clock.calendar.date(bySetting: .second, value: 0, of: t) ?? t }
        }
        return nil
    }

    /// Alle Minuten des Tages, an denen eine Sperrzeit beginnt oder endet (für die Weck-Zeitpläne).
    public var boundaryMinutes: [Int] {
        Array(Set(config.windows.filter { $0.enabled && $0.startMinute != $0.endMinute }
            .flatMap { [$0.startMinute, $0.endMinute] })).sorted()
    }

    public func todaySummary(_ now: Date) -> String {
        let parts = config.windows.filter { $0.enabled && $0.applies(to: clock.dow(now)) }
            .map { "\(Self.fmt($0.startMinute))–\(Self.fmt($0.endMinute))" }
        return parts.isEmpty ? "Heute keine feste Sperrzeit." : "Gesperrt: " + parts.joined(separator: ", ")
    }
}

public enum BudgetPolicy {
    /// usedMinutes = Gesamtnutzung heute (inkl. andere Geräte), otherMinutes nur für den Text.
    public static func apply(_ base: Decision, budget: Int, used: Int, graceActive: Bool, graceUntil: String?,
                             otherMinutes: Int = 0) -> Decision {
        if base.locked || budget <= 0 || used < budget { return base }
        if graceActive { return .unlocked("Zusatzzeit bis \(graceUntil ?? "")") }
        return otherMinutes > 0
            ? .locked("Tageslimit erreicht (\(used)/\(budget) min, davon \(otherMinutes) min am anderen Gerät)")
            : .locked("Tageslimit erreicht (\(used)/\(budget) min)")
    }
}

/// Gemeinsames Tageskonto (19.11.2026): Minuten der ANDEREN Geräte des Kindes vom Server.
public struct SharedUsage: Codable, Equatable, Sendable {
    public var day: String
    public var minutes: Int
    public var detail: String
    public var issuedUnix: Int64

    public static func othersToday(_ s: SharedUsage?, today: String) -> Int {
        guard let s, s.day == today else { return 0 }
        return max(0, s.minutes)
    }

    /// Nur für HEUTE und nicht älter als der gespeicherte Stand – sonst nil (= verwerfen).
    public static func accept(_ current: SharedUsage?, day: String, minutes: Int, detail: String,
                              issuedUnix: Int64, today: String) -> SharedUsage? {
        guard !day.isEmpty, day == today, (0...14400).contains(minutes) else { return nil }
        if let c = current, c.day == day, issuedUnix < c.issuedUnix { return nil }
        return SharedUsage(day: day, minutes: minutes, detail: detail, issuedUnix: issuedUnix)
    }
}
