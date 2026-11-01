package de.froehlichdienste.frohlock.android

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.admin.DeviceAdminReceiver
import android.app.admin.DevicePolicyManager
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.PersistableBundle
import android.app.Notification
import de.froehlichdienste.frohlock.R
import de.froehlichdienste.frohlock.ui.MainActivity
import de.froehlichdienste.frohlock.ui.SetupActivity

/** Geräte-Verwalter. Wird über den QR-Code bei der Einrichtung des Handys zum Device Owner. */
class AdminReceiver : DeviceAdminReceiver() {
    override fun onProfileProvisioningComplete(ctx: Context, intent: Intent) {
        Guard.init(ctx)
        @Suppress("DEPRECATION")
        val extras: PersistableBundle? = intent.getParcelableExtra(DevicePolicyManager.EXTRA_PROVISIONING_ADMIN_EXTRAS_BUNDLE)
        Provisioning.onProvisioned(ctx, extras)
        // Android 7–9 kennen den Einrichtungs-Schritt der App nicht – Einrichtung danach öffnen.
        if (Build.VERSION.SDK_INT < 29 && !Guard.enforcer.isConfigured) {
            ctx.startActivity(Intent(ctx, SetupActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
        }
    }

    /** Einfacher Modus: Kind will den Geräteadministrator abschalten (Voraussetzung fürs Löschen). */
    override fun onDisableRequested(ctx: Context, intent: Intent): CharSequence {
        Guard.init(ctx)
        Guard.audit.write("UMGEHUNG", "⚠ Jemand will den FrohLock-Geräteadministrator abschalten")
        GuardService.syncNow(ctx)
        return "FrohLock schützt dieses Gerät für deine Eltern. Bitte frag Mama oder Papa, bevor du das abschaltest – sie sehen es sonst im Admin."
    }

    override fun onDisabled(ctx: Context, intent: Intent) {
        Guard.init(ctx)
        if (GuardService.releasing || Guard.pairing() == null) return   // Eltern haben FrohLock selbst entfernt
        Guard.audit.write("UMGEHUNG", "⚠ FrohLock-Geräteadministrator wurde abgeschaltet – die App kann jetzt gelöscht werden")
        GuardService.syncNow(ctx)
    }

    override fun onEnabled(ctx: Context, intent: Intent) {
        Guard.init(ctx)
        Guard.audit.write("SETUP", "Geräte-Verwaltung aktiviert")
        GuardService.start(ctx)
    }
}

/** Gemeinsamer Ablauf nach dem QR-Code (beide Android-Wege rufen ihn auf, er ist wiederholbar). */
object Provisioning {
    const val EXTRA_CODE = "pairingCode"
    const val EXTRA_SERVER = "serverUrl"
    const val EXTRA_NAME = "deviceName"

    fun onProvisioned(ctx: Context, extras: PersistableBundle?) {
        Guard.init(ctx)
        val code = extras?.getString(EXTRA_CODE).orEmpty()
        val server = extras?.getString(EXTRA_SERVER)?.takeIf { it.startsWith("https://") } ?: Pairing.DEFAULT_SERVER
        val name = extras?.getString(EXTRA_NAME).orEmpty()
        val current = Guard.pairing()
        if (code.isNotBlank() && current?.registered != true) {
            Guard.savePairing(Pairing(server, deviceName = name.ifBlank { Sync.defaultDeviceName(ctx) }, pendingCode = code))
            Guard.audit.write("SETUP", "QR-Code gelesen – Kopplung für „$name“ vorgemerkt")
        }
        DeviceOwner.applyBaseline(ctx, relaxed = false)
        GuardService.start(ctx)
    }
}

/** Nach Neustart und nach jedem Update sofort wieder schützen. */
class BootReceiver : BroadcastReceiver() {
    override fun onReceive(ctx: Context, intent: Intent) {
        Guard.init(ctx)
        if (intent.action == Intent.ACTION_MY_PACKAGE_REPLACED) {
            Guard.audit.write("UPDATE", "Neue Version ${Guard.appVersion} läuft" +
                if (Updater.canUpdateSilently(ctx)) " – künftige Updates automatisch" else "")
            try { ctx.getSystemService(NotificationManager::class.java).cancel(4) } catch (_: Exception) { }
        }
        if (Guard.pairing() != null || DeviceOwner.isDeviceOwner(ctx)) GuardService.start(ctx)
    }
}

/** Freundliche Schlafenszeit-/Limit-Erinnerung (10, 5 und 1 Minute vorher). */
object Reminders {
    private const val CH = "reminder"

    fun ensureChannel(ctx: Context) {
        ctx.getSystemService(NotificationManager::class.java).createNotificationChannel(
            NotificationChannel(CH, "Erinnerungen", NotificationManager.IMPORTANCE_HIGH))
    }

    fun check(ctx: Context, minutesUntilLock: Int) {
        if (minutesUntilLock !in listOf(10, 5, 1)) return
        val e = Guard.enforcer
        // Schlüssel: Minute der Sperre, damit jede Stufe genau einmal kommt.
        val lockAt = (System.currentTimeMillis() / 60_000) + minutesUntilLock
        val key = "$lockAt/$minutesUntilLock"
        if (Guard.state.lastReminderKey == key) return
        Guard.state.lastReminderKey = key
        val word = Guard.deviceNoun()
        val budget = e.todayBudgetMinutes()
        val byBudget = budget > 0 && budget - e.todayUsageMinutes() == minutesUntilLock
        val text = when {
            byBudget && minutesUntilLock == 1 -> "Noch 1 Minute Bildschirmzeit – such dir schon mal einen guten Halt. 💛"
            byBudget -> "Noch $minutesUntilLock Minuten Bildschirmzeit für heute."
            minutesUntilLock == 1 -> "In 1 Minute ist Schlafenszeit für dein $word. 🌙"
            else -> "In $minutesUntilLock Minuten ist Schlafenszeit für dein $word. 🌙"
        }
        notify(ctx, 2, "Gleich ist Pause", text)
    }

    fun notifyUpdate(ctx: Context, confirm: Intent) {
        ensureChannel(ctx)
        val pi = PendingIntent.getActivity(ctx, 4, confirm, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val text = "Neue FrohLock-Version bereit: antippen und „Aktualisieren“ wählen. Das ist nur EINMAL nötig – " +
            "danach aktualisiert sich FrohLock von selbst."
        val n = Notification.Builder(ctx, CH).setSmallIcon(R.drawable.ic_stat_lock)
            .setContentTitle("FrohLock-Update").setContentText(text).setStyle(Notification.BigTextStyle().bigText(text))
            .setAutoCancel(true).setContentIntent(pi).build()
        try { ctx.getSystemService(NotificationManager::class.java).notify(4, n) } catch (_: Exception) { }
    }

    fun notifyRemoved(ctx: Context) {
        ensureChannel(ctx)
        notify(ctx, 3, "FrohLock wurde freigegeben", "Alle Sperren sind aufgehoben. Die App kann jetzt normal deinstalliert werden.")
    }

    private fun notify(ctx: Context, id: Int, title: String, text: String) {
        val pi = PendingIntent.getActivity(ctx, id, Intent(ctx, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        val n = Notification.Builder(ctx, CH).setSmallIcon(R.drawable.ic_stat_lock)
            .setContentTitle(title).setContentText(text).setStyle(Notification.BigTextStyle().bigText(text))
            .setAutoCancel(true).setContentIntent(pi).build()
        try { ctx.getSystemService(NotificationManager::class.java).notify(id, n) } catch (_: Exception) { }
    }
}
