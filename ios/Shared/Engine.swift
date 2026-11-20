import DeviceActivity
import FamilyControls
import Foundation
import ManagedSettings
import UserNotifications

extension DeviceActivityName {
    static let usage = Self("frohlock.usage")      // täglich 0–24 Uhr, Nutzungs-Ereignisse alle 5 Min
    static let wake = Self("frohlock.wake")        // einmalig: nächster Wechsel bzw. spätestens in 15 Min
    static func boundary(_ m: Int) -> Self { Self("frohlock.b\(m)") }  // täglich zu jeder Sperrzeit-Grenze
}

extension ManagedSettingsStore.Name { static let frohlock = Self("frohlock") }

/// Wendet die Entscheidung an (Apple-Sperrschirm), hält die Weck-Zeitpläne aktuell und gleicht mit dem Server ab.
/// Läuft in der App UND im Bildschirmzeit-Monitor (Erweiterung) – iOS hat keinen Dauer-Hintergrunddienst.
enum Engine {
    static let usageStep = 5
    static let usageMax = 600

    // ---------- Sperren ----------

    @discardableResult
    static func evaluate(now: Date = Date(), source: String) -> Decision {
        let d = Store.update { e -> Decision in
            let d = e.decide(now)
            if d.locked != e.state.lastLocked {
                Audit.write("LOCK", (d.locked ? "Gesperrt: " : "Entsperrt: ") + d.reason + " [\(source)]")
            }
            e.state.lastLocked = d.locked
            e.state.lastReason = d.reason
            return d
        }
        applyShields(locked: d.locked)
        reschedule(now: now)
        return d
    }

    static func applyShields(locked: Bool) {
        let store = ManagedSettingsStore(named: .frohlock)
        let configured = Store.enforcer().isConfigured && AuthorizationCenter.shared.authorizationStatus == .approved
        if locked && configured {
            let allowed = Store.allowedSelection
            store.shield.applicationCategories = .all(except: allowed.applicationTokens)
            store.shield.webDomainCategories = .all(except: allowed.webDomainTokens)
        } else {
            store.shield.applicationCategories = nil
            store.shield.webDomainCategories = nil
        }
        // Schutz gegen Tricks (nur wenn eingerichtet): Apps nicht löschbar, Uhrzeit automatisch.
        store.application.denyAppRemoval = configured ? true : nil
        store.dateAndTime.requireAutomaticDateAndTime = configured ? true : nil
    }

    static func releaseAll() {
        ManagedSettingsStore(named: .frohlock).clearAllSettings()
        DeviceActivityCenter().stopMonitoring()
    }

    // ---------- Weck-Zeitpläne ----------

    static func reschedule(now: Date = Date()) {
        let center = DeviceActivityCenter()
        let e = Store.enforcer()
        guard e.isConfigured, AuthorizationCenter.shared.authorizationStatus == .approved else {
            center.stopMonitoring(); return
        }
        let cal = e.clock.calendar

        // 1) Tageszähler: Ereignis je 5 Minuten Nutzung der gezählten Apps (Auswahl „alles“).
        if !center.activities.contains(.usage) || Store.read("usage-armed.json", as: String.self) != usageKey() {
            let counted = Store.countedSelection
            var events: [DeviceActivityEvent.Name: DeviceActivityEvent] = [:]
            if !counted.applicationTokens.isEmpty || !counted.categoryTokens.isEmpty || !counted.webDomainTokens.isEmpty {
                for m in stride(from: usageStep, through: usageMax, by: usageStep) {
                    events[.init("u\(m)")] = DeviceActivityEvent(
                        applications: counted.applicationTokens, categories: counted.categoryTokens,
                        webDomains: counted.webDomainTokens, threshold: DateComponents(minute: m))
                }
            }
            let day = DeviceActivitySchedule(intervalStart: DateComponents(hour: 0, minute: 0),
                                             intervalEnd: DateComponents(hour: 23, minute: 59), repeats: true)
            center.stopMonitoring([.usage])
            do {
                try center.startMonitoring(.usage, during: day, events: events)
                Store.write("usage-armed.json", usageKey())
            } catch { Audit.write("WARN", "Tageszähler nicht gestartet: \(error.localizedDescription)") }
        }

        // 2) Tägliche Sperrzeit-Grenzen (fest, falls die App lange nicht läuft).
        let wanted = Set(e.config.map { ScheduleEngine(config: $0, clock: e.clock).boundaryMinutes } ?? []).sorted().prefix(15)
        let wantedNames = Set(wanted.map { DeviceActivityName.boundary($0) })
        let stale = center.activities.filter { $0.rawValue.hasPrefix("frohlock.b") && !wantedNames.contains($0) }
        if !stale.isEmpty { center.stopMonitoring(stale) }
        for m in wanted where !center.activities.contains(.boundary(m)) {
            let end = (m + 15) % 1440
            try? center.startMonitoring(.boundary(m), during: DeviceActivitySchedule(
                intervalStart: DateComponents(hour: m / 60, minute: m % 60),
                intervalEnd: DateComponents(hour: end / 60, minute: end % 60), repeats: true))
        }

        // 3) Nächster Wechsel (Entsperrung/Auszeit endet …), spätestens in 15 Min für Abgleich mit dem Server.
        var target = now.addingTimeInterval(15 * 60)
        if let t = e.nextTransition(now), t < target { target = t }
        if target < now.addingTimeInterval(60) { target = now.addingTimeInterval(60) }
        let comps: Set<Calendar.Component> = [.year, .month, .day, .hour, .minute, .second]
        let start = cal.dateComponents(comps, from: target)
        let end = cal.dateComponents(comps, from: target.addingTimeInterval(16 * 60))
        center.stopMonitoring([.wake])
        do {
            try center.startMonitoring(.wake, during: DeviceActivitySchedule(intervalStart: start, intervalEnd: end, repeats: false))
        } catch { Audit.write("WARN", "Wecker nicht gestellt: \(error.localizedDescription)") }
    }

    /// Ändert sich die Zählauswahl, muss der Tageszähler neu gestartet werden.
    private static func usageKey() -> String {
        let c = Store.countedSelection
        return "\(c.applicationTokens.count)-\(c.categoryTokens.count)-\(c.webDomainTokens.count)-\(usageStep)"
    }

    // ---------- Server ----------

    /// Holt Config + Befehle und meldet den Stand. Gibt eine lesbare Fehlermeldung zurück (nil = ok).
    @discardableResult
    static func sync(now: Date = Date(), source: String) -> String? {
        guard let p = Store.pairing, p.registered else { return "Noch nicht gekoppelt" }
        let api = Api(p)
        var err: String?
        do {
            try fetchConfig(api)
            for env in try api.commands() { handle(env, api: api, now: now) }
        } catch { err = describe(error) }
        let d = evaluate(now: now, source: source)
        do { try api.heartbeat(heartbeat(now: now, decision: d)) } catch { err = err ?? describe(error) }
        Store.write("last-sync.json", Int64(now.timeIntervalSince1970))
        if err != nil { Audit.write("NET", "Abgleich fehlgeschlagen: \(err!)") }
        return err
    }

    static func fetchConfig(_ api: Api) throws {
        let have = Store.config()?.configVersion ?? 0
        guard let env = try api.config(have: have) else { return }
        guard let o = SignatureVerifier().openJson(env) else { Audit.write("SEC", "⚠ Config mit falscher Signatur verworfen"); return }
        let c = LockConfig.from(o)
        if !c.deviceId.isEmpty, c.deviceId != Store.pairing?.deviceId { Audit.write("SEC", "⚠ Config für fremdes Gerät verworfen"); return }
        if c.configVersion < have { return }
        Store.configEnvelope = env
        Audit.write("CONFIG", "Einstellungen v\(c.configVersion) übernommen")
    }

    static func handle(_ env: SignedEnvelope, api: Api, now: Date) {
        guard let o = SignatureVerifier().openJson(env) else { Audit.write("SEC", "⚠ Befehl mit falscher Signatur verworfen"); return }
        let cmd = DeviceCommand.from(o)
        if !cmd.deviceId.isEmpty, cmd.deviceId != Store.pairing?.deviceId { Audit.write("CMD", "⚠ Befehl für fremdes Gerät – verworfen"); return }
        if cmd.expiresAtUnix > 0, cmd.expiresAtUnix < Int64(now.timeIntervalSince1970) {
            Audit.write("CMD", "\(CommandType.name(cmd.type)) abgelaufen – verworfen"); api.ack(cmd.commandId); return
        }
        if cmd.type == CommandType.sharedUsage {
            if let m = Int(cmd.payload["minutes"] ?? "") {
                Store.update { $0.setShared(day: cmd.payload["day"] ?? "", minutes: m, detail: cmd.payload["detail"] ?? "",
                                            issuedUnix: cmd.issuedAtUnix, now: now) }
            }
            return
        }
        let fresh = cmd.type == CommandType.ping || cmd.type == CommandType.update
            || Store.update { $0.state.markExecuted(cmd.commandId) }
        if fresh {
            if cmd.type != CommandType.ping { Audit.write("CMD", "Ausführen: \(CommandType.name(cmd.type))") }
            switch cmd.type {
            case CommandType.unlock:
                Store.update { $0.remoteUnlock(minutes: cmd.unlockMinutes ?? ($0.config?.unlockGraceMinutes ?? 60), now: now) }
            case CommandType.lock: Store.update { $0.forceLock() }
            case CommandType.timeout: Store.update { $0.addPenalty(minutes: cmd.unlockMinutes ?? 15, now: now) }
            case CommandType.clearTimeout: Store.update { $0.clearPenalty() }
            case CommandType.updateConfig, CommandType.resetPin: try? fetchConfig(api)
            case CommandType.uninstall:
                api.ack(cmd.commandId)
                release(reason: "Admin-Befehl „Entfernen“")
                return
            case CommandType.message:
                let text = cmd.payload["text"] ?? ""
                if !text.isEmpty {
                    let m = ParentMessage(id: (cmd.payload["id"] ?? "").isEmpty ? cmd.commandId : cmd.payload["id"]!,
                                          title: cmd.payload["title"] ?? "", text: text, button: cmd.payload["button"] ?? "",
                                          videoUrl: cmd.payload["video"] ?? "", showFromUnix: Int64(cmd.payload["showFrom"] ?? "") ?? 0)
                    if Messages.pending?.id != m.id && !Messages.wasRead(m.id) { Messages.pending = m; Messages.notify(m) }
                }
            default: break
            }
        }
        api.ack(cmd.commandId)
    }

    static func release(reason: String) {
        Audit.write("SETUP", "Freigegeben: \(reason)")
        releaseAll()
        Store.pairing = nil
        Store.configEnvelope = nil
        Store.write("state.json", RuntimeState())
    }

    static func heartbeat(now: Date, decision d: Decision) -> [String: Any] {
        var s = Store.enforcer().status(now: now, appVersion: Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "1.0.0")
        s["deviceId"] = Store.pairing?.deviceId ?? ""
        s["recentAudit"] = Audit.tail(40).joined(separator: "\n")
        let auth = AuthorizationCenter.shared.authorizationStatus
        let counted = Store.countedSelection
        var info: [String: Any] = Device.info
        info["screenTimeAuthorized"] = auth == .approved
        info["countedApps"] = counted.applicationTokens.count
        info["countedCategories"] = counted.categoryTokens.count
        info["allowedApps"] = Store.allowedSelection.applicationTokens.count
        s["deviceInfo"] = info
        s["deviceOwner"] = auth == .approved
        if auth != .approved && Store.enforcer().isConfigured {
            s["lockReason"] = "⚠ Bildschirmzeit-Erlaubnis entzogen – FrohLock kann nicht sperren"
        }
        return s
    }

    static func describe(_ e: Error) -> String {
        if let h = e as? Api.HttpError {
            if let o = try? JSONSerialization.jsonObject(with: Data(h.body.utf8)) as? [String: Any], let d = o["detail"] as? String { return d }
            return "Server-Fehler \(h.code)"
        }
        if (e as? URLError) != nil { return "Keine Internetverbindung" }
        return e.localizedDescription
    }
}

enum Device {
    static var info: [String: Any] {
        var sys = utsname(); uname(&sys)
        let model = withUnsafePointer(to: &sys.machine) { $0.withMemoryRebound(to: CChar.self, capacity: 1) { String(cString: $0) } }
        return ["model": model, "os": "iOS \(ProcessInfo.processInfo.operatingSystemVersionString)",
                "manufacturer": "Apple"]
    }
}

// ---------- Eltern-Nachrichten ----------

struct ParentMessage: Codable, Equatable {
    var id: String
    var title: String
    var text: String
    var button: String
    var videoUrl: String
    var showFromUnix: Int64
    var due: Bool { showFromUnix <= Int64(Date().timeIntervalSince1970) }
}

enum Messages {
    static var pending: ParentMessage? {
        get { Store.read("message.json", as: ParentMessage.self) }
        set { if let m = newValue { Store.write("message.json", m) } else { try? FileManager.default.removeItem(at: AppGroup.file("message.json")) } }
    }

    static func wasRead(_ id: String) -> Bool { (Store.read("read-messages.json", as: [String].self) ?? []).contains(id) }

    static func markRead(_ m: ParentMessage) {
        var ids = Store.read("read-messages.json", as: [String].self) ?? []
        if !ids.contains(m.id) { ids.append(m.id) }
        Store.write("read-messages.json", Array(ids.suffix(50)))
        Store.write("last-message.json", m)   // für „nochmal ansehen“
        pending = nil
        Audit.write("MSG", "Nachricht gelesen: \(m.title)")
    }

    static var last: ParentMessage? { Store.read("last-message.json", as: ParentMessage.self) }

    static func notify(_ m: ParentMessage) {
        let c = UNMutableNotificationContent()
        c.title = m.title.isEmpty ? "💌 Nachricht" : m.title
        c.body = "Tippe hier, um sie zu öffnen 💛"
        c.sound = .default
        let trigger: UNNotificationTrigger? = m.due ? nil : UNTimeIntervalNotificationTrigger(
            timeInterval: max(5, TimeInterval(m.showFromUnix) - Date().timeIntervalSince1970), repeats: false)
        UNUserNotificationCenter.current().add(UNNotificationRequest(identifier: "msg-\(m.id)", content: c, trigger: trigger))
    }
}
