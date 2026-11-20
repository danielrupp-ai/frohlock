import ManagedSettings
import ManagedSettingsUI
import UIKit

/// Eigener Sperrbildschirm statt Apples grauem Standard.
final class FrohLockShield: ShieldConfigurationDataSource {
    private func make() -> ShieldConfiguration {
        let s = Store.state()
        let reason = s.lastReason.isEmpty ? "Gerade ist Pause." : s.lastReason
        return ShieldConfiguration(
            backgroundBlurStyle: .systemMaterialDark,
            backgroundColor: UIColor(red: 0.10, green: 0.12, blue: 0.28, alpha: 1),
            icon: UIImage(systemName: "moon.stars.fill"),
            title: .init(text: "FrohLock 💛", color: .white),
            subtitle: .init(text: reason + "\n\nMama oder Papa können in der FrohLock-App mit der PIN entsperren.",
                            color: UIColor(white: 0.9, alpha: 1)),
            primaryButtonLabel: .init(text: "OK", color: .white),
            primaryButtonBackgroundColor: UIColor(red: 0.36, green: 0.42, blue: 0.95, alpha: 1))
    }

    override func configuration(shielding application: Application) -> ShieldConfiguration { make() }
    override func configuration(shielding application: Application, in category: ActivityCategory) -> ShieldConfiguration { make() }
    override func configuration(shielding webDomain: WebDomain) -> ShieldConfiguration { make() }
    override func configuration(shielding webDomain: WebDomain, in category: ActivityCategory) -> ShieldConfiguration { make() }
}
