import Foundation

/// Geräte-API der FrohLock-Brücke (gleiche Endpunkte wie Windows/Android). Bewusst synchron:
/// die Bildschirmzeit-Erweiterung hat nur kurze, synchrone Rückrufe.
struct Api {
    struct HttpError: Error { let code: Int; let body: String }

    var base: String
    var deviceId: String = ""
    var token: String = ""

    init(_ p: Pairing) { base = p.serverUrl; deviceId = p.deviceId; token = p.token }
    init(base: String) { self.base = base }

    private func request(_ method: String, _ path: String, json: Any? = nil, timeout: TimeInterval = 12) throws -> (Int, Data) {
        var r = URLRequest(url: URL(string: base.trimmingCharacters(in: CharacterSet(charactersIn: "/")) + path)!)
        r.httpMethod = method
        r.timeoutInterval = timeout
        if !token.isEmpty { r.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization") }
        if let json {
            r.httpBody = try JSONSerialization.data(withJSONObject: json)
            r.setValue("application/json", forHTTPHeaderField: "Content-Type")
        }
        let cfg = URLSessionConfiguration.ephemeral
        cfg.timeoutIntervalForRequest = timeout
        cfg.timeoutIntervalForResource = timeout * 2
        let session = URLSession(configuration: cfg)
        let sem = DispatchSemaphore(value: 0)
        var result: (Int, Data)?
        var failure: Error?
        session.dataTask(with: r) { d, resp, err in
            if let err { failure = err } else { result = ((resp as? HTTPURLResponse)?.statusCode ?? 0, d ?? Data()) }
            sem.signal()
        }.resume()
        if sem.wait(timeout: .now() + timeout * 2 + 2) == .timedOut { session.invalidateAndCancel(); throw URLError(.timedOut) }
        session.finishTasksAndInvalidate()
        if let failure { throw failure }
        guard let result else { throw URLError(.badServerResponse) }
        if result.0 == 304 { return result }
        if !(200..<300).contains(result.0) { throw HttpError(code: result.0, body: String(data: result.1, encoding: .utf8) ?? "") }
        return result
    }

    private func object(_ d: Data) -> Any? { try? JSONSerialization.jsonObject(with: d) }

    func register(code: String, name: String, info: [String: Any]) throws -> (deviceId: String, token: String) {
        let (_, d) = try request("POST", "/devices/register",
                                 json: ["pairingCode": code, "deviceName": name, "platform": "ios", "deviceInfo": info])
        let o = object(d) as? [String: Any] ?? [:]
        guard let id = o["deviceId"] as? String, let t = o["token"] as? String else { throw URLError(.cannotParseResponse) }
        return (id, t)
    }

    /// nil = nichts Neues (304) oder keine Config hinterlegt.
    func config(have: Int) throws -> SignedEnvelope? {
        do {
            let (code, d) = try request("GET", "/devices/\(deviceId)/config?have=\(have)")
            if code == 304 { return nil }
            return (object(d) as? [String: Any]).map(SignedEnvelope.from)
        } catch let e as HttpError where e.code == 404 { return nil }
    }

    func commands() throws -> [SignedEnvelope] {
        let (_, d) = try request("GET", "/devices/\(deviceId)/commands")
        return ((object(d) as? [[String: Any]]) ?? []).map(SignedEnvelope.from)
    }

    func ack(_ commandId: String) { _ = try? request("POST", "/devices/\(deviceId)/commands/\(commandId)/ack", timeout: 6) }

    func heartbeat(_ status: [String: Any]) throws { _ = try request("POST", "/devices/\(deviceId)/heartbeat", json: status) }

    func configDraft(_ draft: [String: Any]) throws { _ = try request("POST", "/devices/\(deviceId)/config-draft", json: draft) }

    /// Neueste iOS-Version auf dem Server (nur Anzeige – iOS installiert nie still).
    func latestVersion() -> String? {
        guard let (_, d) = try? request("GET", "/updates/manifest?platform=ios", timeout: 6) else { return nil }
        return (object(d) as? [String: Any])?["version"] as? String
    }
}
