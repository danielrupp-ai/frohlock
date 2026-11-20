import AVKit
import BackgroundTasks
import FamilyControls
import Photos
import SwiftUI
import UserNotifications

@main
struct FrohLockApp: App {
    @UIApplicationDelegateAdaptor(AppDelegate.self) var delegate
    @StateObject private var model = AppModel()
    @Environment(\.scenePhase) private var phase

    var body: some Scene {
        WindowGroup {
            RootView().environmentObject(model)
                .onOpenURL { model.handle(url: $0) }
                .onChange(of: phase) { p in if p == .active { model.refresh(sync: true) } }
        }
    }
}

final class AppDelegate: NSObject, UIApplicationDelegate, UNUserNotificationCenterDelegate {
    static let refreshId = "de.froehlichdienste.frohlock.refresh"

    func application(_ app: UIApplication, didFinishLaunchingWithOptions _: [UIApplication.LaunchOptionsKey: Any]? = nil) -> Bool {
        UNUserNotificationCenter.current().delegate = self
        BGTaskScheduler.shared.register(forTaskWithIdentifier: Self.refreshId, using: nil) { task in
            Self.schedule()
            DispatchQueue.global().async {
                Engine.sync(source: "Hintergrund")
                task.setTaskCompleted(success: true)
            }
            task.expirationHandler = { task.setTaskCompleted(success: false) }
        }
        Self.schedule()
        return true
    }

    static func schedule() {
        let r = BGAppRefreshTaskRequest(identifier: refreshId)
        r.earliestBeginDate = Date(timeIntervalSinceNow: 15 * 60)
        try? BGTaskScheduler.shared.submit(r)
    }

    func userNotificationCenter(_: UNUserNotificationCenter, willPresent _: UNNotification,
                                withCompletionHandler done: @escaping (UNNotificationPresentationOptions) -> Void) {
        done([.banner, .sound])
    }
}

// MARK: - Modell

@MainActor
final class AppModel: ObservableObject {
    @Published var pairing: Pairing? = Store.pairing
    @Published var authorized = AuthorizationCenter.shared.authorizationStatus == .approved
    @Published var counted = Store.countedSelection
    @Published var allowed = Store.allowedSelection
    @Published var decision = Decision.unlocked("")
    @Published var configured = Store.enforcer().isConfigured
    @Published var minutesLeft = -1
    @Published var usedToday = 0
    @Published var otherToday = 0
    @Published var budget = 0
    @Published var unlockUntil: Date?
    @Published var lastError: String?
    @Published var busy = false
    @Published var pendingCode = ""
    @Published var message: ParentMessage?
    @Published var newerVersion: String?
    @Published var parentUntil: Date?       // Eltern-Bereich offen bis

    var appVersion: String { Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "1.0.0" }
    var parentOpen: Bool { (parentUntil ?? .distantPast) > Date() }
    var selectionsDone: Bool {
        !(counted.applicationTokens.isEmpty && counted.categoryTokens.isEmpty) && !allowed.applicationTokens.isEmpty
    }

    init() { refresh(sync: true) }

    func refresh(sync: Bool) {
        pairing = Store.pairing
        authorized = AuthorizationCenter.shared.authorizationStatus == .approved
        load()
        guard sync, pairing?.registered == true else { return }
        Task.detached {
            let err = Engine.sync(source: "App")
            let newer = Store.pairing.flatMap { Api($0).latestVersion() }
            await MainActor.run {
                self.lastError = err
                if let n = newer, n.compare(self.appVersion, options: .numeric) == .orderedDescending { self.newerVersion = n }
                else { self.newerVersion = nil }
                self.load()
            }
        }
    }

    func load() {
        let now = Date()
        let e = Store.enforcer()
        decision = e.decide(now)
        configured = e.isConfigured
        minutesLeft = e.minutesUntilLock(now)
        let day = e.clock.dayKey(now)
        usedToday = e.state.ownMinutes(day)
        otherToday = e.state.otherMinutes(day)
        budget = e.config?.effectiveDailyBudget(dow: e.clock.dow(now)) ?? 0
        unlockUntil = e.state.unlockUntilUnix > Int64(now.timeIntervalSince1970) ? Date(timeIntervalSince1970: TimeInterval(e.state.unlockUntilUnix)) : nil
        if let m = Messages.pending, m.due { message = m }
    }

    func handle(url: URL) {
        guard url.scheme == "frohlock" else { return }
        let code = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems?.first { $0.name == "code" }?.value ?? ""
        if pairing?.registered != true { pendingCode = code.filter(\.isNumber) }
    }

    // ---- Schritt 1: Koppeln ----
    func pair(code: String, name: String) {
        busy = true; lastError = nil
        Task.detached {
            do {
                let r = try Api(base: AppGroup.serverUrl).register(code: code.filter(\.isNumber), name: name, info: Device.info)
                Store.pairing = Pairing(deviceId: r.deviceId, token: r.token, deviceName: name)
                Audit.write("SETUP", "Gekoppelt als „\(name)“ (\(r.deviceId.prefix(8))…)")
                try? Engine.fetchConfig(Api(Store.pairing!))
                await MainActor.run { self.busy = false; self.refresh(sync: true) }
            } catch {
                await MainActor.run { self.busy = false; self.lastError = Engine.describe(error) }
            }
        }
    }

    // ---- Schritt 2: Bildschirmzeit ----
    func authorize() async {
        do {
            try await AuthorizationCenter.shared.requestAuthorization(for: .individual)
            Audit.write("SETUP", "Bildschirmzeit-Erlaubnis erteilt")
            _ = try? await UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound, .badge])
        } catch { lastError = "Bildschirmzeit nicht erlaubt: \(error.localizedDescription)" }
        refresh(sync: false)
    }

    func saveSelections() {
        Store.countedSelection = counted
        Store.allowedSelection = allowed
        Audit.write("SETUP", "Auswahl: \(counted.categoryTokens.count) Kategorien/\(counted.applicationTokens.count) Apps zählen, \(allowed.applicationTokens.count) Apps immer erlaubt")
        Engine.evaluate(source: "Auswahl")
        refresh(sync: true)
    }

    // ---- Schritt 3: PIN + Schlafenszeit ----
    func savePin(pin: String, start: Int, end: Int, budget: Int, email: String) {
        busy = true; lastError = nil
        Task.detached {
            guard let p = Store.pairing, let h = PinHasher.hash(pin) else { return }
            let draft: [String: Any] = [
                "windows": [["id": "schlafenszeit", "label": "Schlafenszeit", "days": [Int](), "startMinute": start,
                             "endMinute": end, "enabled": true]],
                "pinHash": h.hash, "pinSalt": h.salt, "pinIterations": PinHasher.defaultIterations,
                "unlockGraceMinutes": 60, "dailyBudgetMinutes": max(0, budget),
                "maxTrustedTimeStalenessMinutes": 4320, "resetEmail": email]
            do {
                try Api(p).configDraft(draft)
                try Engine.fetchConfig(Api(p))
                let ok = Store.enforcer().isConfigured
                await MainActor.run {
                    self.busy = false
                    self.lastError = ok ? nil : "Server hat die Einstellungen nicht bestätigt."
                    self.refresh(sync: true)
                }
            } catch {
                await MainActor.run { self.busy = false; self.lastError = Engine.describe(error) }
            }
        }
    }

    // ---- Eltern-PIN ----
    func submitPin(_ pin: String, minutes: Int?, done: @escaping (String?) -> Void) {
        busy = true
        Task.detached {
            let r = Store.update { $0.submitPin(pin, minutes: minutes, now: Date()) }
            if case .wrong = r {} else if r != .blocked { Engine.evaluate(source: "PIN") }
            if r == .ok || r == .master { Audit.write("PIN", r == .master ? "Master-PIN" : "Eltern-PIN: entsperrt für \(minutes ?? 60) Min") }
            else { Audit.write("PIN", "Falsche PIN") }
            await MainActor.run {
                self.busy = false
                switch r {
                case .ok, .master: self.parentUntil = Date().addingTimeInterval(5 * 60); done(nil)
                case .wrong(let left): done("Falsche PIN – noch \(left) Versuche heute")
                case .blocked: done("Zu viele falsche Versuche. Eltern können im Admin entsperren.")
                }
                self.refresh(sync: true)
            }
        }
    }

    func parentAction(_ f: @escaping (inout Enforcer) -> Void, _ label: String) {
        Store.update(f)
        Audit.write("PIN", label)
        Engine.evaluate(source: label)
        refresh(sync: true)
    }

    func readMessage(_ m: ParentMessage) { Messages.markRead(m); message = nil; refresh(sync: false) }
}

// MARK: - Oberfläche

struct RootView: View {
    @EnvironmentObject var m: AppModel
    var body: some View {
        NavigationView {
            Group {
                if m.pairing?.registered != true { PairView() }
                else if !m.authorized { AuthorizeView() }
                else if !m.selectionsDone { SelectionView(firstRun: true) }
                else if !m.configured { PinSetupView() }
                else { StatusView() }
            }
            .navigationBarTitleDisplayMode(.inline)
        }
        .navigationViewStyle(.stack)
        .sheet(item: $m.message) { MessageView(message: $0) }
    }
}

struct Header: View {
    let step: String; let title: String; let text: String
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(step).font(.caption.bold()).foregroundColor(.secondary)
            Text(title).font(.title2.bold())
            Text(text).foregroundColor(.secondary)
        }.frame(maxWidth: .infinity, alignment: .leading)
    }
}

struct ErrorText: View {
    @EnvironmentObject var m: AppModel
    var body: some View {
        if let e = m.lastError { Text(e).foregroundColor(.red).font(.callout) }
    }
}

struct PairView: View {
    @EnvironmentObject var m: AppModel
    @State private var code = ""
    @State private var name = ""
    var body: some View {
        Form {
            Section {
                Header(step: "Schritt 1 von 4", title: "FrohLock einrichten 💛",
                       text: "Im Admin (frohlock.froehlichdienste.de/admin) „iPhone einrichten“ wählen. Den 6-stelligen Code hier eintragen – oder den Link am iPhone öffnen, dann ist er schon drin.")
            }
            Section("Kopplungscode") {
                TextField("123456", text: $code).keyboardType(.numberPad).font(.title2.monospacedDigit())
                TextField("Name (z. B. iPhone Renat)", text: $name)
            }
            Section {
                Button(m.busy ? "Verbinde …" : "Koppeln") { m.pair(code: code, name: name.isEmpty ? UIDevice.current.name : name) }
                    .disabled(m.busy || code.filter(\.isNumber).count != 6)
                ErrorText()
            }
        }
        .onAppear { if !m.pendingCode.isEmpty { code = m.pendingCode } }
        .onChange(of: m.pendingCode) { code = $0 }
        .navigationTitle("FrohLock")
    }
}

struct AuthorizeView: View {
    @EnvironmentObject var m: AppModel
    var body: some View {
        Form {
            Section {
                Header(step: "Schritt 2 von 4", title: "Bildschirmzeit erlauben",
                       text: "FrohLock nutzt Apples Bildschirmzeit, um Apps zu sperren und die Zeit zu zählen. iOS fragt gleich nach Face ID oder dem Gerätecode – das muss ein Elternteil bestätigen.")
            }
            Section {
                Button("Erlauben") { Task { await m.authorize() } }
                ErrorText()
            }
            Section(footer: Text("Tipp: In Einstellungen → Bildschirmzeit einen eigenen Bildschirmzeit-Code setzen, den nur ihr kennt. Dann kann die Erlaubnis nicht einfach wieder entzogen werden.")) { EmptyView() }
        }
        .navigationTitle("FrohLock")
    }
}

struct SelectionView: View {
    @EnvironmentObject var m: AppModel
    let firstRun: Bool
    @State private var pickCounted = false
    @State private var pickAllowed = false
    var body: some View {
        Form {
            Section {
                Header(step: firstRun ? "Schritt 3 von 4" : "Eltern-Bereich", title: "Welche Apps?",
                       text: "Zuerst: was gesperrt wird und zur Tageszeit zählt – am besten ALLES (oben „Alle“ antippen). Dann: was immer geht, auch in der Sperrzeit.")
            }
            Section(footer: Text("Empfohlen: im Auswahlfenster ganz oben alle Kategorien wählen.")) {
                Button { pickCounted = true } label: {
                    Label("Gezählt & gesperrt: \(m.counted.categoryTokens.count) Kategorien, \(m.counted.applicationTokens.count) Apps", systemImage: "hourglass")
                }
            }
            Section(footer: Text("Unbedingt FrohLock selbst + Telefon + Nachrichten wählen, sonst kommt ihr in der Sperrzeit nicht zur PIN.")) {
                Button { pickAllowed = true } label: {
                    Label("Immer erlaubt: \(m.allowed.applicationTokens.count) Apps", systemImage: "checkmark.shield")
                }
            }
            Section {
                Button("Speichern") { m.saveSelections() }.disabled(!m.selectionsDone)
            }
        }
        .familyActivityPicker(isPresented: $pickCounted, selection: $m.counted)
        .familyActivityPicker(isPresented: $pickAllowed, selection: $m.allowed)
        .navigationTitle("Apps")
    }
}

struct PinSetupView: View {
    @EnvironmentObject var m: AppModel
    @State private var pin = ""
    @State private var pin2 = ""
    @State private var start = Calendar.current.date(from: DateComponents(hour: 21))!
    @State private var end = Calendar.current.date(from: DateComponents(hour: 7))!
    @State private var budget = "120"
    @State private var email = ""
    private func minute(_ d: Date) -> Int { let c = Calendar.current.dateComponents([.hour, .minute], from: d); return c.hour! * 60 + c.minute! }
    var body: some View {
        Form {
            Section { Header(step: "Schritt 4 von 4", title: "Eltern-PIN & Schlafenszeit",
                             text: "Die PIN wird nur als Prüfwert gespeichert. Alles Weitere stellt ihr später im Admin ein.") }
            Section("Eltern-PIN (4–8 Ziffern)") {
                SecureField("PIN", text: $pin).keyboardType(.numberPad)
                SecureField("PIN wiederholen", text: $pin2).keyboardType(.numberPad)
            }
            Section("Schlafenszeit") {
                DatePicker("Ab", selection: $start, displayedComponents: .hourAndMinute)
                DatePicker("Bis", selection: $end, displayedComponents: .hourAndMinute)
            }
            Section(header: Text("Tageszeit in Minuten"), footer: Text("0 = kein Tageslimit. Bei gemeinsamem Tageskonto gilt der Wert des Kindes.")) {
                TextField("120", text: $budget).keyboardType(.numberPad)
            }
            Section("E-Mail für „PIN vergessen“") { TextField("eltern@beispiel.de", text: $email).keyboardType(.emailAddress).textInputAutocapitalization(.never) }
            Section {
                Button(m.busy ? "Speichere …" : "Fertig") {
                    m.savePin(pin: pin, start: minute(start), end: minute(end), budget: Int(budget) ?? 0, email: email)
                }.disabled(m.busy || pin.count < 4 || pin.count > 8 || pin != pin2 || !pin.allSatisfy(\.isNumber))
                if !pin2.isEmpty && pin != pin2 { Text("Die PINs sind verschieden.").foregroundColor(.orange) }
                ErrorText()
            }
        }
        .navigationTitle("PIN")
    }
}

struct StatusView: View {
    @EnvironmentObject var m: AppModel
    @State private var askPin = false
    var body: some View {
        ScrollView {
            VStack(spacing: 18) {
                if let v = m.newerVersion {
                    Link(destination: URL(string: AppGroup.serverUrl + "/ios")!) {
                        Label("Neue Version \(v) – jetzt installieren", systemImage: "arrow.down.circle.fill")
                            .frame(maxWidth: .infinity).padding().background(Color.orange.opacity(0.2)).cornerRadius(14)
                    }
                }
                VStack(spacing: 10) {
                    Image(systemName: m.decision.locked ? "moon.stars.fill" : "sun.max.fill")
                        .font(.system(size: 64)).foregroundColor(m.decision.locked ? .indigo : .orange)
                    Text(m.decision.locked ? "Gerade ist Pause" : "Viel Spaß! 💛").font(.title.bold())
                    Text(m.decision.reason).multilineTextAlignment(.center).foregroundColor(.secondary)
                }.padding(.top, 24)

                if m.budget > 0 {
                    let used = m.usedToday + m.otherToday
                    VStack(alignment: .leading, spacing: 6) {
                        Text("Heute: \(used) von \(m.budget) Min").font(.headline)
                        ProgressView(value: Double(min(used, m.budget)), total: Double(m.budget))
                        if m.otherToday > 0 { Text("davon \(m.otherToday) Min am anderen Gerät").font(.caption).foregroundColor(.secondary) }
                    }.padding().background(Color(.secondarySystemBackground)).cornerRadius(14)
                }
                if m.minutesLeft >= 0 && !m.decision.locked {
                    Text("Noch \(m.minutesLeft) Min bis zur Pause").font(.headline)
                }
                if let u = m.unlockUntil { Text("Entsperrt bis \(u.formatted(date: .omitted, time: .shortened))").foregroundColor(.green) }
                if let last = Messages.last {
                    Button("💌 Nachricht nochmal ansehen") { m.message = last }
                }
                Button { askPin = true } label: {
                    Label("Eltern (PIN)", systemImage: "lock.open").frame(maxWidth: .infinity).padding()
                }.buttonStyle(.borderedProminent)
                if m.parentOpen { ParentPanel() }
                ErrorText()
            }.padding()
        }
        .refreshable { m.refresh(sync: true) }
        .sheet(isPresented: $askPin) { PinSheet() }
        .navigationTitle("FrohLock")
    }
}

struct PinSheet: View {
    @EnvironmentObject var m: AppModel
    @Environment(\.dismiss) private var dismiss
    @State private var pin = ""
    @State private var minutes = 60
    @State private var error: String?
    var body: some View {
        NavigationView {
            Form {
                Section("Eltern-PIN") { SecureField("PIN", text: $pin).keyboardType(.numberPad).font(.title2) }
                Section("Freigeben für") {
                    Picker("Dauer", selection: $minutes) {
                        Text("15 Min").tag(15); Text("30 Min").tag(30); Text("1 Std").tag(60); Text("2 Std").tag(120); Text("4 Std").tag(240)
                    }.pickerStyle(.segmented)
                }
                Section {
                    Button(m.busy ? "Prüfe …" : "Entsperren") {
                        m.submitPin(pin, minutes: minutes) { err in
                            if let err { error = err; pin = "" } else { dismiss() }
                        }
                    }.disabled(pin.isEmpty || m.busy)
                    if let error { Text(error).foregroundColor(.red) }
                }
            }
            .navigationTitle("Eltern")
            .toolbar { ToolbarItem(placement: .cancellationAction) { Button("Abbrechen") { dismiss() } } }
        }
    }
}

struct ParentPanel: View {
    @EnvironmentObject var m: AppModel
    @State private var showLog = false
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Eltern-Bereich (5 Min offen)").font(.headline)
            Button("🔒 Jetzt sperren") { m.parentAction({ $0.forceLock() }, "Eltern: sofort sperren") }
            Button("⏹ Entsperrung beenden") { m.parentAction({ $0.endUnlock() }, "Eltern: Entsperrung beendet") }
            NavigationLink("📱 Apps-Auswahl ändern") { SelectionView(firstRun: false) }
            Button("🔄 Jetzt abgleichen") { m.refresh(sync: true) }
            Button("📜 Protokoll") { showLog = true }
            Text("Gerät \(m.pairing?.deviceName ?? "") · v\(m.appVersion)").font(.caption).foregroundColor(.secondary)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding().background(Color(.secondarySystemBackground)).cornerRadius(14)
        .sheet(isPresented: $showLog) {
            NavigationView {
                List(Audit.tail(200).reversed(), id: \.self) { Text($0).font(.caption.monospaced()) }.navigationTitle("Protokoll")
            }
        }
    }
}

extension ParentMessage: Identifiable {}

struct MessageView: View {
    @EnvironmentObject var m: AppModel
    @Environment(\.dismiss) private var dismiss
    let message: ParentMessage
    @State private var player: AVPlayer?
    @State private var saved: String?
    var body: some View {
        ScrollView {
            VStack(spacing: 18) {
                if let player {
                    VideoPlayer(player: player).aspectRatio(9 / 16, contentMode: .fit).cornerRadius(16)
                        .onAppear { player.play() }
                    Button(saved ?? "📥 In Fotos speichern") { save() }.disabled(saved != nil)
                }
                if !message.title.isEmpty { Text(message.title).font(.largeTitle.bold()).multilineTextAlignment(.center) }
                Text(message.text).font(.title3).multilineTextAlignment(.center)
                Button(message.button.isEmpty ? "OK 💛" : message.button) { m.readMessage(message); dismiss() }
                    .buttonStyle(.borderedProminent).controlSize(.large)
            }.padding()
        }
        .onAppear { if let u = URL(string: message.videoUrl), !message.videoUrl.isEmpty { player = AVPlayer(url: u) } }
        .interactiveDismissDisabled()
    }

    private func save() {
        guard let u = URL(string: message.videoUrl) else { return }
        saved = "Lade …"
        PHPhotoLibrary.requestAuthorization(for: .addOnly) { status in
            guard status == .authorized || status == .limited else { DispatchQueue.main.async { saved = "Kein Zugriff auf Fotos" }; return }
            URLSession.shared.downloadTask(with: u) { tmp, _, _ in
                guard let tmp else { DispatchQueue.main.async { saved = "Download fehlgeschlagen" }; return }
                let dst = FileManager.default.temporaryDirectory.appendingPathComponent("frohlock-\(message.id).mp4")
                try? FileManager.default.removeItem(at: dst)
                try? FileManager.default.moveItem(at: tmp, to: dst)
                PHPhotoLibrary.shared().performChanges({
                    PHAssetChangeRequest.creationRequestForAssetFromVideo(atFileURL: dst)
                }) { ok, _ in DispatchQueue.main.async { saved = ok ? "✅ In Fotos gespeichert" : "Speichern fehlgeschlagen" } }
            }.resume()
        }
    }
}
