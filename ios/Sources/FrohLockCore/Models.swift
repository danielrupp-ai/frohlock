import Foundation

// Gleiche Datenformen wie Windows (FrohLock.Core.Models) und Android (core/Models.kt) –
// der Server signiert EIN Format für alle. Feldnamen camelCase, Enums als Zahl.

public struct ScheduleWindow: Equatable, Sendable {
    public var id: String
    public var label: String?
    /// 0 = Sonntag … 6 = Samstag. Leer = alle Tage.
    public var days: [Int]
    public var startMinute: Int
    public var endMinute: Int
    public var enabled: Bool

    public init(id: String = "", label: String? = nil, days: [Int] = [], startMinute: Int, endMinute: Int, enabled: Bool = true) {
        self.id = id; self.label = label; self.days = days
        self.startMinute = startMinute; self.endMinute = endMinute; self.enabled = enabled
    }

    public func applies(to dow: Int) -> Bool { days.isEmpty || days.contains(dow) }

    static let dayNames = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"]

    public static func from(_ o: [String: Any]) -> ScheduleWindow {
        var days: [Int] = []
        for v in (o["days"] as? [Any]) ?? [] {
            if let n = v as? NSNumber { days.append(n.intValue) }
            else if let s = v as? String {
                if let n = Int(s) { days.append(n) } else if let i = dayNames.firstIndex(of: s.lowercased()) { days.append(i) }
            }
        }
        return ScheduleWindow(id: o["id"] as? String ?? "", label: o["label"] as? String, days: days,
                              startMinute: (o["startMinute"] as? NSNumber)?.intValue ?? 0,
                              endMinute: (o["endMinute"] as? NSNumber)?.intValue ?? 0,
                              enabled: (o["enabled"] as? Bool) ?? true)
    }
}

public struct LockConfig: Equatable, Sendable {
    public var configVersion: Int
    public var deviceId: String
    public var windows: [ScheduleWindow]
    public var dailyBudgetMinutes: Int
    /// 7 Werte, Index 0 = Sonntag; Wert > 0 überschreibt den Standard.
    public var dailyBudgetByWeekday: [Int]
    public var unlockGraceMinutes: Int
    public var pinHash: String
    public var pinSalt: String
    public var pinIterations: Int
    public var serverBaseUrl: String
    public var serverPollSeconds: Int

    public init(configVersion: Int = 1, deviceId: String = "", windows: [ScheduleWindow] = [], dailyBudgetMinutes: Int = 0,
                dailyBudgetByWeekday: [Int] = [], unlockGraceMinutes: Int = 60, pinHash: String = "", pinSalt: String = "",
                pinIterations: Int = PinHasher.defaultIterations, serverBaseUrl: String = "", serverPollSeconds: Int = 30) {
        self.configVersion = configVersion; self.deviceId = deviceId; self.windows = windows
        self.dailyBudgetMinutes = dailyBudgetMinutes; self.dailyBudgetByWeekday = dailyBudgetByWeekday
        self.unlockGraceMinutes = unlockGraceMinutes; self.pinHash = pinHash; self.pinSalt = pinSalt
        self.pinIterations = pinIterations; self.serverBaseUrl = serverBaseUrl; self.serverPollSeconds = serverPollSeconds
    }

    public var hasPin: Bool { !pinHash.isEmpty }

    public func effectiveDailyBudget(dow: Int) -> Int {
        if dailyBudgetByWeekday.count == 7, dailyBudgetByWeekday[dow] > 0 { return dailyBudgetByWeekday[dow] }
        return dailyBudgetMinutes
    }

    public static func from(_ o: [String: Any]) -> LockConfig {
        func int(_ k: String, _ d: Int) -> Int { (o[k] as? NSNumber)?.intValue ?? d }
        return LockConfig(
            configVersion: int("configVersion", 0),
            deviceId: o["deviceId"] as? String ?? "",
            windows: ((o["windows"] as? [[String: Any]]) ?? []).map(ScheduleWindow.from),
            dailyBudgetMinutes: int("dailyBudgetMinutes", 0),
            dailyBudgetByWeekday: ((o["dailyBudgetByWeekday"] as? [Any]) ?? []).map { ($0 as? NSNumber)?.intValue ?? 0 },
            unlockGraceMinutes: int("unlockGraceMinutes", 60),
            pinHash: o["pinHash"] as? String ?? "",
            pinSalt: o["pinSalt"] as? String ?? "",
            pinIterations: int("pinIterations", PinHasher.defaultIterations),
            serverBaseUrl: o["serverBaseUrl"] as? String ?? "",
            serverPollSeconds: int("serverPollSeconds", 30))
    }
}

/// Werte wie FrohLock.Core.Models.CommandType (Server: signing.CMD).
public enum CommandType {
    public static let unlock = 0, lock = 1, updateConfig = 2, resetPin = 3, uninstall = 4, update = 5, ping = 6
    public static let timeout = 7, clearTimeout = 8, sharedUsage = 9, message = 10

    static let names = ["Unlock", "Lock", "UpdateConfig", "ResetPin", "Uninstall", "Update", "Ping", "Timeout",
                        "ClearTimeout", "SharedUsage", "Message"]

    public static func name(_ t: Int) -> String { names.indices.contains(t) ? names[t] : "Typ\(t)" }
}

public struct DeviceCommand: Sendable {
    public var commandId: String
    public var deviceId: String
    public var type: Int
    public var issuedAtUnix: Int64
    public var expiresAtUnix: Int64
    public var unlockMinutes: Int?
    public var payload: [String: String]

    public static func from(_ o: [String: Any]) -> DeviceCommand {
        var type = -1
        if let n = o["type"] as? NSNumber { type = n.intValue }
        else if let s = o["type"] as? String {
            type = Int(s) ?? ["unlock": 0, "lock": 1, "updateconfig": 2, "resetpin": 3, "uninstall": 4, "update": 5,
                              "ping": 6, "timeout": 7, "cleartimeout": 8, "sharedusage": 9, "message": 10][s.lowercased()] ?? -1
        }
        var payload: [String: String] = [:]
        for (k, v) in (o["payload"] as? [String: Any]) ?? [:] { payload[k] = (v as? String) ?? "\(v)" }
        return DeviceCommand(commandId: o["commandId"] as? String ?? "", deviceId: o["deviceId"] as? String ?? "",
                             type: type, issuedAtUnix: (o["issuedAtUnix"] as? NSNumber)?.int64Value ?? 0,
                             expiresAtUnix: (o["expiresAtUnix"] as? NSNumber)?.int64Value ?? 0,
                             unlockMinutes: (o["unlockMinutes"] as? NSNumber)?.intValue, payload: payload)
    }
}

/// Server-signierter Umschlag: Signatur über GENAU die Bytes in payloadBase64.
public struct SignedEnvelope: Codable, Equatable, Sendable {
    public var payloadBase64: String
    public var signatureBase64: String
    public var algorithm: String?

    public init(payloadBase64: String, signatureBase64: String, algorithm: String? = "RSASSA-PSS-SHA256") {
        self.payloadBase64 = payloadBase64; self.signatureBase64 = signatureBase64; self.algorithm = algorithm
    }

    public static func from(_ o: [String: Any]) -> SignedEnvelope {
        SignedEnvelope(payloadBase64: o["payloadBase64"] as? String ?? "", signatureBase64: o["signatureBase64"] as? String ?? "")
    }
}

public struct Decision: Equatable, Sendable {
    public var locked: Bool
    public var reason: String
    public static func locked(_ r: String) -> Decision { Decision(locked: true, reason: r) }
    public static func unlocked(_ r: String) -> Decision { Decision(locked: false, reason: r) }
}
