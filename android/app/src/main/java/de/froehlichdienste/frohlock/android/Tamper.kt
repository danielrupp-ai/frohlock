package de.froehlichdienste.frohlock.android

import android.app.admin.DevicePolicyManager
import android.content.Context
import android.content.Intent
import android.os.SystemClock

/**
 * Löschschutz im einfachen Modus (ohne Geräte-Verwaltung), seit 1.0.7.
 * Android lässt das Abschalten des Geräteadministrators und das Löschen hier grundsätzlich zu –
 * FrohLock macht es so schwer wie möglich und meldet jeden Versuch sofort an die Eltern (E-Mail über den Server):
 * - Seite „Geräteadministrator“ offen oder Abschalten angefragt → Handy sofort gesperrt bis Eltern-PIN
 * - App-Info-Seite (Stopp erzwingen / Deinstallieren) → zurück zum Startbildschirm
 * - Administrator trotzdem aus → gesperrt, bis er wieder an ist (Knopf auf dem Sperrbildschirm) oder Eltern-PIN
 * Eltern-Bereich „Einstellungen 15 Min freigeben“ schaltet das alles für 15 Minuten ab.
 */
object Tamper {
    const val MESSAGE = "Du hast versucht, FrohLock abzuschalten oder zu löschen. Darum bleibt das Handy gesperrt, " +
        "bis Mama oder Papa die PIN eingeben – sie haben schon eine Nachricht bekommen."
    const val INCOMPLETE = "Der Schutz von FrohLock ist ausgeschaltet. Tippe unten auf „Schutz wieder einschalten“ " +
        "und dann auf „Aktivieren“ – danach geht es normal weiter."

    /** FrohLock hat selbst eine Einstellungs-Seite geöffnet (Einrichtung, Updates): kurz nicht dazwischenfunken. */
    @Volatile private var ownSettingsUntilUptime = 0L
    fun allowOwnSettings() { ownSettingsUntilUptime = SystemClock.uptimeMillis() + 3 * 60_000 }
    fun ownSettingsActive() = SystemClock.uptimeMillis() < ownSettingsUntilUptime

    /** Gilt der Löschschutz gerade? (eingerichtet, gekoppelt, keine Eltern-Freigabe, nicht beim Entfernen) */
    fun active(ctx: Context): Boolean {
        Guard.init(ctx)
        if (DeviceOwner.isDeviceOwner(ctx) || GuardService.releasing) return false
        if (Guard.pairing() == null || !Guard.enforcer.isConfigured) return false
        return Guard.state.relaxedUntilUnix <= System.currentTimeMillis() / 1000
    }

    /** Löschversuch: sperren bis Eltern-PIN, Bildschirm aus, Sperrbildschirm nach vorn, Eltern sofort benachrichtigen. */
    fun lock(ctx: Context, what: String) {
        if (!active(ctx)) return
        val st = Guard.state
        if (!(st.forcedLock && st.forcedLockMessage == MESSAGE)) {
            Guard.enforcer.forceLock("Löschversuch", MESSAGE)
            Guard.audit.write("UMGEHUNG", "⚠ LÖSCHVERSUCH: $what – Handy gesperrt bis Eltern-PIN")
        }
        WeakGuard.setOverlay(ctx, true)
        WeakGuard.openLock(ctx)
        try { ctx.getSystemService(DevicePolicyManager::class.java).lockNow() } catch (_: Exception) { }
        GuardService.poke(ctx)
        GuardService.syncNow(ctx)
    }

    fun goHome(ctx: Context) {
        try {
            ctx.startActivity(Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_HOME).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
        } catch (_: Exception) { }
    }

    /** Geräteadministrator wieder einschalten (Knopf auf dem Sperrbildschirm / in der Einrichtung). */
    fun requestAdmin(ctx: Context) {
        allowOwnSettings()
        try {
            ctx.startActivity(Intent(DevicePolicyManager.ACTION_ADD_DEVICE_ADMIN)
                .putExtra(DevicePolicyManager.EXTRA_DEVICE_ADMIN, DeviceOwner.admin(ctx))
                .putExtra(DevicePolicyManager.EXTRA_ADD_EXPLANATION,
                    "FrohLock schützt die Bildschirmzeit. Als Geräteadministrator kann die App nicht einfach gelöscht werden.")
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
        } catch (_: Exception) { }
    }
}
