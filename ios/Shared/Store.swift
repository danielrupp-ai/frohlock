import Foundation
import FamilyControls

/// Gemeinsamer Speicher von App, Bildschirmzeit-Monitor und Sperrbildschirm (App Group).
/// Lesen-Ändern-Schreiben läuft unter einer Datei-Sperre (flock), weil App und Erweiterung gleichzeitig laufen können.
enum AppGroup {
    static let id = "group.de.froehlichdienste.frohlock"
    static let serverUrl = "https://frohlock.froehlichdienste.de"

    static var dir: URL {
        let u = FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: id)
            ?? FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        try? FileManager.default.createDirectory(at: u, withIntermediateDirectories: true)
        return u
    }
    static func file(_ name: String) -> URL { dir.appendingPathComponent(name) }

    static var appVersion: String { Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "1.0.0" }
}

struct Pairing: Codable, Equatable {
    var serverUrl: String = AppGroup.serverUrl
    var deviceId: String = ""
    var token: String = ""
    var deviceName: String = ""
    var registered: Bool { !deviceId.isEmpty && !token.isEmpty }
}

enum Store {
    private static func withLock<T>(_ body: () throws -> T) rethrows -> T {
        let fd = open(AppGroup.file(".lock").path, O_CREAT | O_RDWR, 0o600)
        if fd >= 0 { flock(fd, LOCK_EX) }
        defer { if fd >= 0 { flock(fd, LOCK_UN); close(fd) } }
        return try body()
    }

    static func read<T: Decodable>(_ name: String, as: T.Type) -> T? {
        guard let d = try? Data(contentsOf: AppGroup.file(name)) else { return nil }
        return try? JSONDecoder().decode(T.self, from: d)
    }

    static func write<T: Encodable>(_ name: String, _ v: T) {
        guard let d = try? JSONEncoder().encode(v) else { return }
        try? d.write(to: AppGroup.file(name), options: [.atomic, .completeFileProtectionUntilFirstUserAuthentication])
    }

    // ---- Kopplung ----
    static var pairing: Pairing? {
        get { read("pairing.json", as: Pairing.self) }
        set { if let p = newValue { write("pairing.json", p) } else { try? FileManager.default.removeItem(at: AppGroup.file("pairing.json")) } }
    }

    // ---- Signierte Config (nur verifiziert übernommen) ----
    static var configEnvelope: SignedEnvelope? {
        get { read("config.json", as: SignedEnvelope.self) }
        set { if let e = newValue { write("config.json", e) } else { try? FileManager.default.removeItem(at: AppGroup.file("config.json")) } }
    }

    static func config() -> LockConfig? {
        guard let env = configEnvelope, let o = SignatureVerifier().openJson(env) else { return nil }
        return LockConfig.from(o)
    }

    // ---- Laufzeitzustand ----
    static func state() -> RuntimeState { read("state.json", as: RuntimeState.self) ?? RuntimeState() }

    /// Zustand ändern und speichern – atomar über alle Prozesse.
    @discardableResult
    static func update<T>(_ body: (inout Enforcer) -> T) -> T {
        withLock {
            var e = Enforcer(config: config(), state: state())
            let r = body(&e)
            write("state.json", e.state)
            return r
        }
    }

    static func enforcer() -> Enforcer { Enforcer(config: config(), state: state()) }

    // ---- Auswahl in der Bildschirmzeit (Apple-Tokens, nur auf diesem Gerät gültig) ----
    static var countedSelection: FamilyActivitySelection {
        get { read("counted.json", as: FamilyActivitySelection.self) ?? FamilyActivitySelection() }
        set { write("counted.json", newValue) }
    }
    static var allowedSelection: FamilyActivitySelection {
        get { read("allowed.json", as: FamilyActivitySelection.self) ?? FamilyActivitySelection() }
        set { write("allowed.json", newValue) }
    }
}

/// Geräte-Protokoll im gleichen Zeilenformat wie Windows/Android (ISO-UTC \t KATEGORIE \t Text).
enum Audit {
    static func write(_ category: String, _ message: String) {
        let url = AppGroup.file("audit.log")
        let line = "\(ISO8601DateFormatter().string(from: Date()))\t\(category)\t\(message.replacingOccurrences(of: "\n", with: " ").replacingOccurrences(of: "\t", with: " "))\n"
        if let attrs = try? FileManager.default.attributesOfItem(atPath: url.path), (attrs[.size] as? Int ?? 0) > 400_000 {
            let keep = tail(400).joined(separator: "\n") + "\n"
            try? keep.data(using: .utf8)?.write(to: url, options: .atomic)
        }
        if let h = try? FileHandle(forWritingTo: url) {
            h.seekToEndOfFile(); h.write(line.data(using: .utf8)!); try? h.close()
        } else {
            try? line.data(using: .utf8)?.write(to: url)
        }
    }

    static func tail(_ n: Int) -> [String] {
        guard let s = try? String(contentsOf: AppGroup.file("audit.log"), encoding: .utf8) else { return [] }
        return Array(s.split(separator: "\n").suffix(n)).map(String.init)
    }
}
