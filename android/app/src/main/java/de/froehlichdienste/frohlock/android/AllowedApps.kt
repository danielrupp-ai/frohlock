package de.froehlichdienste.frohlock.android

import android.app.AlarmManager
import android.app.AppOpsManager
import android.app.PendingIntent
import android.app.usage.UsageEvents
import android.app.usage.UsageStatsManager
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.media.AudioManager
import android.os.Build
import android.os.Process
import android.os.SystemClock
import de.froehlichdienste.frohlock.core.LockConfig

/** Apps, die auch während einer Sperre offen bleiben (Messenger aus dem Admin + Telefon, Wecker, Systemdialoge). */
object AllowedApps {
    /** Bekannte Namen für die Knöpfe auf dem Sperrbildschirm. */
    private val LABELS = mapOf(
        "org.telegram.messenger" to "Telegram", "org.telegram.messenger.web" to "Telegram",
        "org.thoughtcrime.securesms" to "Signal", "com.whatsapp" to "WhatsApp", "com.whatsapp.w4b" to "WhatsApp Business",
        "ch.threema.app" to "Threema", "org.thunderdog.challegram" to "Telegram X",
    )

    /** Systemteile, die Messenger brauchen (Kamera-/Mikrofon-Erlaubnis, Anruf-Oberfläche). */
    private val SYSTEM = setOf(
        "com.google.android.permissioncontroller", "com.android.permissioncontroller",
        "com.android.systemui", "android",
    )

    fun configured(): List<String> = Guard.enforcer.currentConfig?.alwaysAllowedApps ?: LockConfig.DEFAULT_ALLOWED_APPS

    /** Alle Pakete, die während der Sperre vorne sein dürfen, ohne dass FrohLock eingreift. */
    fun packages(ctx: Context): Set<String> = buildSet {
        add(ctx.packageName)
        addAll(configured())
        addAll(DeviceOwner.phonePackages(ctx))
        DeviceOwner.clockPackage(ctx)?.let { add(it) }
        addAll(SYSTEM)
    }

    fun isInstalled(ctx: Context, pkg: String) = try { ctx.packageManager.getPackageInfo(pkg, 0); true } catch (_: Exception) { false }

    /** Installierte erlaubte Messenger mit Namen – für die Knöpfe auf dem Sperrbildschirm (Telegram doppelt → einmal). */
    fun installedMessengers(ctx: Context): List<Pair<String, String>> {
        val seen = mutableSetOf<String>()
        return configured().filter { isInstalled(ctx, it) }.mapNotNull { pkg ->
            val label = LABELS[pkg] ?: try {
                ctx.packageManager.getApplicationLabel(ctx.packageManager.getApplicationInfo(pkg, 0)).toString()
            } catch (_: Exception) { pkg }
            if (seen.add(label)) pkg to label else null
        }
    }

    fun launch(ctx: Context, pkg: String): Boolean = try {
        val i = ctx.packageManager.getLaunchIntentForPackage(pkg) ?: return false
        ctx.startActivity(i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
        true
    } catch (_: Exception) { false }
}

/**
 * Welche App ist vorne? Über „Nutzungsdatenzugriff" (UsageStats) – nur so kann FrohLock im einfachen
 * Modus Telegram/Signal erlauben und alles andere sofort wieder überdecken. Es wird nur das aktuell
 * vorderste Paket gelesen, nichts gespeichert außer bei einer Umgehung während der Sperre.
 */
object ForegroundApp {
    @Volatile private var lastPkg: String? = null
    @Volatile private var lastQueryMs = 0L

    fun hasAccess(ctx: Context): Boolean = try {
        val ops = ctx.getSystemService(AppOpsManager::class.java)
        val mode = if (Build.VERSION.SDK_INT >= 29)
            ops.unsafeCheckOpNoThrow(AppOpsManager.OPSTR_GET_USAGE_STATS, Process.myUid(), ctx.packageName)
        else @Suppress("DEPRECATION") ops.checkOpNoThrow(AppOpsManager.OPSTR_GET_USAGE_STATS, Process.myUid(), ctx.packageName)
        mode == AppOpsManager.MODE_ALLOWED
    } catch (_: Exception) { false }

    /** Paketname der vordersten App oder null (keine Erlaubnis / unbekannt). */
    fun current(ctx: Context): String? {
        if (!hasAccess(ctx)) return null
        val usm = ctx.getSystemService(UsageStatsManager::class.java) ?: return null
        val now = System.currentTimeMillis()
        // Nur das Neue seit der letzten Abfrage lesen (spart Akku); beim ersten Mal 10 Minuten zurück.
        val from = if (lastQueryMs == 0L) now - 10 * 60_000 else lastQueryMs - 2_000
        lastQueryMs = now
        try {
            val events = usm.queryEvents(from, now)
            val e = UsageEvents.Event()
            while (events.hasNextEvent()) {
                events.getNextEvent(e)
                @Suppress("DEPRECATION")
                if (e.eventType == UsageEvents.Event.ACTIVITY_RESUMED || e.eventType == UsageEvents.Event.MOVE_TO_FOREGROUND)
                    lastPkg = e.packageName
            }
        } catch (_: Exception) { }
        return lastPkg
    }
}

/** Läuft ein Telefonat oder klingelt es (auch Telegram-/Signal-Anrufe)? Ohne Extra-Erlaubnis über den Audio-Modus. */
object CallState {
    fun busy(ctx: Context): Boolean = try {
        when (ctx.getSystemService(AudioManager::class.java).mode) {
            AudioManager.MODE_RINGTONE, AudioManager.MODE_IN_CALL, AudioManager.MODE_IN_COMMUNICATION -> true
            else -> false
        }
    } catch (_: Exception) { false }
}

/**
 * Wächter: startet den Schutzdienst neu, falls ihn das System oder das Wegwischen beendet hat
 * (Samsung & Co. sind da streng). Wecker jede Minute, auch im Schlafmodus.
 */
class WatchdogReceiver : BroadcastReceiver() {
    override fun onReceive(ctx: Context, intent: Intent) {
        Guard.init(ctx)
        if (Guard.pairing() != null || DeviceOwner.isDeviceOwner(ctx)) {
            if (GuardService.instance == null) {
                Guard.audit.write("DIENST", "Wächter hat den Schutzdienst neu gestartet")
                GuardService.start(ctx)
            }
            schedule(ctx, 60_000)
        }
    }

    companion object {
        fun schedule(ctx: Context, inMs: Long) {
            val am = ctx.getSystemService(AlarmManager::class.java)
            val pi = PendingIntent.getBroadcast(ctx, 7, Intent(ctx, WatchdogReceiver::class.java),
                PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
            val at = SystemClock.elapsedRealtime() + inMs
            try {
                if (Build.VERSION.SDK_INT < 31 || am.canScheduleExactAlarms())
                    am.setExactAndAllowWhileIdle(AlarmManager.ELAPSED_REALTIME_WAKEUP, at, pi)
                else am.setAndAllowWhileIdle(AlarmManager.ELAPSED_REALTIME_WAKEUP, at, pi)
            } catch (_: Exception) {
                am.set(AlarmManager.ELAPSED_REALTIME_WAKEUP, at, pi)
            }
        }
    }
}
