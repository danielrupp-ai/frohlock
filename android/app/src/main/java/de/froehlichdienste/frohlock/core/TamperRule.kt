package de.froehlichdienste.frohlock.core

/**
 * Erkennt Einstellungs-Seiten, über die ein Kind FrohLock ohne Geräte-Verwaltung loswerden kann
 * (Lehre 16.11.2026: Renat schaltete in der freien Zeit den Geräteadministrator ab und löschte die App).
 *
 * - ADMIN_PAGE: Seite „Geräteadministrator“ (Abschalten = Voraussetzung fürs Löschen) → Löschversuch, Handy sperren.
 *   Nur solange der Administrator AN ist – zum Wieder-Einschalten muss genau diese Seite offen sein dürfen.
 * - APP_INFO: App-Info-Seite (dort gibt es „Stopp erzwingen“ und „Deinstallieren“) → zurück zum Startbildschirm.
 *   Android verrät nicht, WELCHE App gerade angezeigt wird, darum gilt das für jede App-Info.
 *
 * Android meldet nur Paket + Activity-Klasse der vordersten Seite. Hersteller benennen Klassen etwas
 * unterschiedlich, darum wird nach Wortteilen gesucht – nur innerhalb der Einstellungs-App.
 */
object TamperRule {
    enum class Kind { NONE, ADMIN_PAGE, APP_INFO }

    val SETTINGS_PACKAGES = setOf("com.android.settings", "com.samsung.android.settings", "com.miui.securitycenter")

    private val ADMIN_MARKERS = listOf("DeviceAdminAdd", "DeviceAdminSettings", "DeviceAdminApps")
    private val APP_INFO_MARKERS = listOf("InstalledAppDetails", "AppInfoDashboard", "AppInfoActivity", "ApplicationDetails")

    fun isSettings(pkg: String?) = pkg != null && pkg in SETTINGS_PACKAGES

    fun isAdminPage(pkg: String?, cls: String?) =
        isSettings(pkg) && cls != null && ADMIN_MARKERS.any { cls.contains(it) }

    fun classify(pkg: String?, cls: String?, adminActive: Boolean): Kind {
        if (!isSettings(pkg) || cls == null) return Kind.NONE
        if (isAdminPage(pkg, cls)) return if (adminActive) Kind.ADMIN_PAGE else Kind.NONE
        if (APP_INFO_MARKERS.any { cls.contains(it) }) return Kind.APP_INFO
        return Kind.NONE
    }
}
