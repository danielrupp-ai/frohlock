import DeviceActivity
import Foundation

/// Bildschirmzeit-Monitor: iOS weckt diese Erweiterung bei Tagesbeginn, alle 5 Min Nutzung,
/// zu jeder Sperrzeit-Grenze und zum „Wecker“. Jeder Aufruf: Nutzung buchen, mit dem Server abgleichen, Sperre anwenden.
final class FrohLockMonitor: DeviceActivityMonitor {
    override func intervalDidStart(for activity: DeviceActivityName) {
        super.intervalDidStart(for: activity)
        if activity == .usage { Store.update { $0.recordUsage(minutes: 0, now: Date()) } }
        run("Zeitplan \(activity.rawValue.replacingOccurrences(of: "frohlock.", with: ""))")
    }

    override func intervalDidEnd(for activity: DeviceActivityName) {
        super.intervalDidEnd(for: activity)
        if activity == .wake { run("Wecker") }
    }

    override func eventDidReachThreshold(_ event: DeviceActivityEvent.Name, activity: DeviceActivityName) {
        super.eventDidReachThreshold(event, activity: activity)
        guard activity == .usage, let m = Int(event.rawValue.dropFirst()) else { return }
        Store.update { $0.recordUsage(minutes: m, now: Date()) }
        run("Nutzung \(m) Min")
    }

    private func run(_ source: String) {
        // Erst sofort lokal sperren (ohne Netz), dann abgleichen.
        Engine.evaluate(source: source)
        Engine.sync(source: source)
    }
}
