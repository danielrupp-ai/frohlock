package de.froehlichdienste.frohlock.android

import android.Manifest
import android.app.ActivityManager
import android.app.admin.DevicePolicyManager
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.UserManager
import android.provider.AlarmClock
import android.telecom.TelecomManager
import android.view.inputmethod.InputMethodManager
import de.froehlichdienste.frohlock.BuildConfig

/**
 * Alles, was FrohLock als Geräte-Verwalter (Device Owner) darf. Ohne Device Owner sind alle
 * Aufrufe wirkungslos (die App läuft dann im schwachen Modus, der Admin zeigt eine Warnung).
 */
object DeviceOwner {
    fun admin(ctx: Context) = ComponentName(ctx, AdminReceiver::class.java)
    private fun dpm(ctx: Context) = ctx.getSystemService(DevicePolicyManager::class.java)

    fun isDeviceOwner(ctx: Context): Boolean =
        try { dpm(ctx).isDeviceOwnerApp(ctx.packageName) } catch (_: Exception) { false }

    /** Dauerhaft: kann das Kind nie lockern. */
    private val ALWAYS = listOf(
        UserManager.DISALLOW_ADD_USER,
        UserManager.DISALLOW_ADD_MANAGED_PROFILE,
    )

    /** Eltern können diese im Eltern-Bereich für eine Weile freigeben. */
    private val RELAXABLE: List<String> = buildList {
        add(UserManager.DISALLOW_FACTORY_RESET)        // Zurücksetzen über die Einstellungen
        add(UserManager.DISALLOW_SAFE_BOOT)            // abgesicherter Modus
        if (!BuildConfig.ALLOW_DEBUGGING) add(UserManager.DISALLOW_DEBUGGING_FEATURES)   // Entwickleroptionen / USB-Debugging
        add(UserManager.DISALLOW_CONFIG_DATE_TIME)     // Uhrzeit verstellen
        add(UserManager.DISALLOW_INSTALL_UNKNOWN_SOURCES)
        if (Build.VERSION.SDK_INT >= 29) add(UserManager.DISALLOW_INSTALL_UNKNOWN_SOURCES_GLOBALLY)
        if (Build.VERSION.SDK_INT >= 28) add(UserManager.DISALLOW_USER_SWITCH)
    }

    /**
     * Grundschutz setzen (idempotent, wird bei jedem Dienststart und nach jeder Lockerung erneut gesetzt).
     * @param relaxed true = Eltern haben die lockerbaren Sperren gerade freigegeben.
     */
    fun applyBaseline(ctx: Context, relaxed: Boolean) {
        if (!isDeviceOwner(ctx)) return
        val dpm = dpm(ctx)
        val a = admin(ctx)
        val audit = Guard.audit
        fun safe(what: String, block: () -> Unit) {
            try { block() } catch (e: Exception) { audit.write("POLICY", "$what nicht möglich: ${e.javaClass.simpleName}") }
        }
        ALWAYS.forEach { r -> safe(r) { dpm.addUserRestriction(a, r) } }
        RELAXABLE.forEach { r ->
            safe(r) { if (relaxed) dpm.clearUserRestriction(a, r) else dpm.addUserRestriction(a, r) }
        }
        // FrohLock selbst: nicht deinstallierbar, nicht „beenden erzwingen"/„Daten löschen".
        safe("uninstallBlocked") { dpm.setUninstallBlocked(a, ctx.packageName, true) }
        if (Build.VERSION.SDK_INT >= 30) safe("userControlDisabled") {
            dpm.setUserControlDisabledPackages(a, listOf(ctx.packageName))
        }
        // Uhrzeit bleibt automatisch (Netzzeit) – Zurückstellen der Uhr wirkt nicht.
        safe("autoTime") {
            if (Build.VERSION.SDK_INT >= 30) dpm.setAutoTimeEnabled(a, true)
            else @Suppress("DEPRECATION") dpm.setAutoTimeRequired(a, true)
        }
        // Benachrichtigungen dürfen (Erinnerungen, Schutzdienst-Hinweis).
        if (Build.VERSION.SDK_INT >= 33) safe("notifications") {
            dpm.setPermissionGrantState(a, ctx.packageName, Manifest.permission.POST_NOTIFICATIONS,
                DevicePolicyManager.PERMISSION_GRANT_STATE_GRANTED)
        }
        configureLockTask(ctx)
    }

    /** Sperr-Modus: nur FrohLock (+ Telefon für Notfälle, Wecker) darf in den Vordergrund. */
    fun configureLockTask(ctx: Context) {
        if (!isDeviceOwner(ctx)) return
        val dpm = dpm(ctx)
        val a = admin(ctx)
        try {
            dpm.setLockTaskPackages(a, lockTaskPackages(ctx).toTypedArray())
            if (Build.VERSION.SDK_INT >= 28) {
                dpm.setLockTaskFeatures(a,
                    DevicePolicyManager.LOCK_TASK_FEATURE_SYSTEM_INFO or      // Uhrzeit/Akku oben
                    DevicePolicyManager.LOCK_TASK_FEATURE_GLOBAL_ACTIONS or   // Ausschalten möglich
                    DevicePolicyManager.LOCK_TASK_FEATURE_KEYGUARD)           // eigene Bildschirmsperre bleibt
            }
        } catch (e: Exception) {
            Guard.audit.write("POLICY", "Sperr-Modus nicht einrichtbar: ${e.javaClass.simpleName}")
        }
    }

    /** Pakete, die auch während der Sperre erreichbar bleiben (Notruf, eingehende Anrufe, Wecker). */
    fun lockTaskPackages(ctx: Context): Set<String> {
        val out = linkedSetOf(ctx.packageName)
        out.addAll(phonePackages(ctx))
        out.addAll(AllowedApps.configured())          // Telegram, Signal … dürfen auch im Sperr-Modus auf
        clockPackage(ctx)?.let { out.add(it) }
        return out
    }

    fun phonePackages(ctx: Context): Set<String> {
        val out = linkedSetOf<String>()
        try { ctx.getSystemService(TelecomManager::class.java)?.defaultDialerPackage?.let { out.add(it) } } catch (_: Exception) { }
        for (p in listOf("com.android.dialer", "com.google.android.dialer", "com.samsung.android.dialer",
            "com.samsung.android.incallui", "com.android.incallui", "com.android.phone", "com.android.server.telecom")) {
            if (isInstalled(ctx, p)) out.add(p)
        }
        return out
    }

    fun clockPackage(ctx: Context): String? = try {
        ctx.packageManager.resolveActivity(Intent(AlarmClock.ACTION_SHOW_ALARMS), 0)?.activityInfo?.packageName
            ?.takeIf { it != "android" }
    } catch (_: Exception) { null }

    fun hasTelephony(ctx: Context) = ctx.packageManager.hasSystemFeature(PackageManager.FEATURE_TELEPHONY)

    private fun isInstalled(ctx: Context, pkg: String) = try {
        ctx.packageManager.getPackageInfo(pkg, 0); true
    } catch (_: Exception) { false }

    /**
     * Zweite Sicherung während der Sperre: alle Apps mit Startsymbol pausieren (graues Symbol, „App pausiert").
     * Greift auch in den Sekunden nach einem Neustart, bevor der Sperrbildschirm steht.
     * Ausgenommen: FrohLock, Startbildschirm, Telefon, Wecker, Tastaturen, Einstellungen.
     */
    fun suspendApps(ctx: Context, suspend: Boolean) {
        if (!isDeviceOwner(ctx)) return
        val dpm = dpm(ctx)
        val a = admin(ctx)
        val state = Guard.state
        try {
            if (suspend) {
                val targets = launchablePackages(ctx) - exemptPackages(ctx)
                val fresh = targets.filter { it !in state.suspendedPackages }
                if (fresh.isEmpty()) return
                val failed = dpm.setPackagesSuspended(a, fresh.toTypedArray(), true).toSet()
                state.suspendedPackages.addAll(fresh.filter { it !in failed })
                Guard.saveState()
            } else {
                if (state.suspendedPackages.isEmpty()) return
                dpm.setPackagesSuspended(a, state.suspendedPackages.toTypedArray(), false)
                state.suspendedPackages.clear()
                Guard.saveState()
            }
        } catch (e: Exception) {
            Guard.audit.write("POLICY", "Apps pausieren fehlgeschlagen: ${e.javaClass.simpleName}")
        }
    }

    private fun launchablePackages(ctx: Context): Set<String> {
        val i = Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER)
        return ctx.packageManager.queryIntentActivities(i, 0).map { it.activityInfo.packageName }.toSet()
    }

    private fun exemptPackages(ctx: Context): Set<String> {
        val out = mutableSetOf(ctx.packageName, "com.android.settings", "com.android.systemui")
        out.addAll(lockTaskPackages(ctx))
        try {
            val home = Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_HOME)
            ctx.packageManager.queryIntentActivities(home, 0).forEach { out.add(it.activityInfo.packageName) }
        } catch (_: Exception) { }
        try {
            ctx.getSystemService(InputMethodManager::class.java)?.enabledInputMethodList?.forEach { out.add(it.packageName) }
        } catch (_: Exception) { }
        return out
    }

    fun lockTaskActive(ctx: Context): Boolean = try {
        ctx.getSystemService(ActivityManager::class.java).lockTaskModeState != ActivityManager.LOCK_TASK_MODE_NONE
    } catch (_: Exception) { false }

    /**
     * FrohLock vollständig freigeben (Eltern-PIN oder Admin-Befehl „Entfernen"): alle Sperren aufheben,
     * Geräte-Verwaltung abgeben. Danach lässt sich die App ganz normal deinstallieren.
     */
    fun release(ctx: Context) {
        val dpm = dpm(ctx)
        val a = admin(ctx)
        suspendApps(ctx, false)
        if (!isDeviceOwner(ctx)) return
        (ALWAYS + RELAXABLE).forEach { r -> try { dpm.clearUserRestriction(a, r) } catch (_: Exception) { } }
        try { dpm.setUninstallBlocked(a, ctx.packageName, false) } catch (_: Exception) { }
        if (Build.VERSION.SDK_INT >= 30) try { dpm.setUserControlDisabledPackages(a, emptyList()) } catch (_: Exception) { }
        try { dpm.setLockTaskPackages(a, emptyArray()) } catch (_: Exception) { }
        try { @Suppress("DEPRECATION") dpm.clearDeviceOwnerApp(ctx.packageName) } catch (e: Exception) {
            Guard.audit.write("UNINSTALL", "Geräte-Verwaltung abgeben fehlgeschlagen: ${e.javaClass.simpleName}")
        }
    }
}
