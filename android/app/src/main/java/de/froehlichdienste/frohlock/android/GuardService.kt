package de.froehlichdienste.frohlock.android

import android.app.KeyguardManager
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.Handler
import android.os.HandlerThread
import android.os.IBinder
import android.os.PowerManager
import android.os.SystemClock
import android.provider.Settings
import de.froehlichdienste.frohlock.BuildConfig
import de.froehlichdienste.frohlock.R
import de.froehlichdienste.frohlock.ui.LockActivity
import de.froehlichdienste.frohlock.ui.MainActivity
import de.froehlichdienste.frohlock.ui.SetupActivity
import org.json.JSONObject
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

/**
 * Schutzdienst (läuft immer, startet nach Neustart und Update von selbst).
 * Takt alle 5 s: entscheiden → sperren/freigeben → Bildschirmzeit zählen → Auszeit → Erinnerungen.
 * Netz läuft getrennt (eigener Thread mit Fristen), damit ein hängender Server nie die Sperre aufhält.
 */
class GuardService : Service() {
    private lateinit var tickThread: HandlerThread
    private lateinit var handler: Handler
    private val net = Executors.newSingleThreadScheduledExecutor()
    private var lastTickUptime = 0L
    private var lastFullSync = 0L
    private var lastLockLaunch = 0L
    private var lastBypassAudit = 0L
    private var lastNotifText = ""
    private var lastSave = 0L
    private var lastCrossCheck = 0L
    private var lastZoneWarned = ""
    /** Kind ist gerade in der Sperre in einer erlaubten App (Telegram, Signal, Telefon) – zählt nicht aufs Limit. */
    @Volatile private var lockedInAllowed = false

    private val screenReceiver = object : BroadcastReceiver() {
        override fun onReceive(c: Context, i: Intent) {
            handler.post { tick() }
            if (i.action == Intent.ACTION_USER_PRESENT || i.action == Intent.ACTION_SCREEN_ON) net.execute { netRound(force = true) }
        }
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        Guard.init(this)
        instance = this
        startInForeground()
        tickThread = HandlerThread("frohlock-tick").also { it.start() }
        handler = Handler(tickThread.looper)
        registerReceiver(screenReceiver, IntentFilter().apply {
            addAction(Intent.ACTION_SCREEN_ON); addAction(Intent.ACTION_SCREEN_OFF); addAction(Intent.ACTION_USER_PRESENT)
        })
        handler.post {
            detectGap()
            DeviceOwner.applyBaseline(this, relaxed = isRelaxed())
            val owner = DeviceOwner.isDeviceOwner(this)
            Guard.audit.write("DIENST", "Gestartet (v${Guard.appVersion}, " +
                (if (owner) "Geräte-Verwaltung aktiv)" else "einfacher Schutz ohne Geräte-Verwaltung)"))
            if (!owner && Guard.enforcer.isConfigured) {
                val miss = WeakGuard.missing(this)
                if (miss.isNotEmpty()) Guard.audit.write("UMGEHUNG", "⚠ Einfacher Schutz – es fehlt: ${miss.joinToString(", ")}")
            }
            // Gleich beim Start gegenprüfen: lief FrohLock nicht, hat Android trotzdem mitgeschrieben.
            crossCheck()
            loop()
        }
        net.scheduleWithFixedDelay({ netRound(force = false) }, 2, 10, TimeUnit.SECONDS)
        WatchdogReceiver.schedule(this, 60_000)
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_POKE) handler.post { tick() }
        if (intent?.action == ACTION_SYNC) net.execute { netRound(force = true) }
        return START_STICKY
    }

    /** App aus der Übersicht gewischt: sofort wiederkommen (manche Hersteller beenden dabei den Dienst). */
    override fun onTaskRemoved(rootIntent: Intent?) {
        Guard.audit.write("DIENST", "FrohLock aus der App-Übersicht entfernt – Schutz läuft weiter")
        WatchdogReceiver.schedule(this, 1_000)
        handler.post { tick() }
        super.onTaskRemoved(rootIntent)
    }

    override fun onDestroy() {
        instance = null
        if (!releasing) WatchdogReceiver.schedule(this, 2_000)
        try { unregisterReceiver(screenReceiver) } catch (_: Exception) { }
        net.shutdownNow()
        tickThread.quitSafely()
        Guard.saveState()
        super.onDestroy()
    }

    // ---------- Takt ----------

    private fun loop() {
        try { tick() } catch (e: Exception) { Guard.audit.write("DIENST", "Takt-Fehler: ${e.javaClass.simpleName} ${e.message}") }
        // Einfacher Modus (ohne Geräte-Verwaltung) während einer Sperre: schneller nachsehen, ob das Kind weggewechselt hat.
        val fast = !DeviceOwner.isDeviceOwner(this) && lastLocked
        handler.postDelayed({ loop() }, if (fast) 1_500 else 5_000)
    }

    @Volatile private var lastLocked = false

    private fun screenOn(): Boolean {
        val pm = getSystemService(PowerManager::class.java)
        val km = getSystemService(KeyguardManager::class.java)
        return pm.isInteractive && !km.isKeyguardLocked
    }

    @Synchronized
    private fun tick() {
        val e = Guard.enforcer
        val up = SystemClock.uptimeMillis()
        // Sekunden seit dem letzten Takt (Wach-Uhr; Tiefschlaf zählt nicht). Gedeckelt gegen Sprünge.
        val dt = if (lastTickUptime == 0L) 0 else ((up - lastTickUptime) / 1000).toInt().coerceIn(0, 30)
        if (dt == 0 && lastTickUptime != 0L) return     // doppelter Aufruf im selben Moment
        lastTickUptime = up
        val on = screenOn()

        val d = e.decide()
        // Während der Handy-Einrichtung (Setup-Assistent, FrohLock-Einrichtung) nie dazwischenfunken.
        val settingUp = !userSetupComplete() || SetupActivity.isActive
        val owner = DeviceOwner.isDeviceOwner(this)
        val enforcing = d.locked && !settingUp
        lastLocked = enforcing
        var lockVisible = false
        lockedInAllowed = false
        if (enforcing) {
            DeviceOwner.suspendApps(this, true)
            lockVisible = if (owner) enforceOwner(on) else enforceWeak(on)
        } else {
            if (!owner) WeakGuard.setOverlay(this, false)
            outsideSince = 0
            if (!d.locked) {
                DeviceOwner.suspendApps(this, false)
                if (on && dt > 0) e.accrueUsage(dt)
            }
        }
        // Jede Sekunde „Bildschirm an" merken (gezählt oder gesperrt) – Basis für die Gegenprobe mit Android.
        if (on && dt > 0) e.accrueOn(dt, enforcing && lockedInAllowed)
        if (up - lastCrossCheck > 120_000) { lastCrossCheck = up; crossCheck() }
        checkZone()
        // Auszeit läuft nur ab, solange das Kind sie wirklich vor sich sieht (nicht im Spiel, nicht in Telegram).
        if (dt > 0) e.tickPenalty(dt, on && lockVisible)

        if (isRelaxedExpired()) {
            Guard.state.relaxedUntilUnix = 0
            DeviceOwner.applyBaseline(this, relaxed = false)
            Guard.audit.write("POLICY", "Eltern-Freigabe der Einstellungen abgelaufen – Schutz wieder voll aktiv")
        }

        Reminders.check(this, e.minutesUntilLock())

        val st = Guard.state
        st.lastTickWallUnix = System.currentTimeMillis() / 1000
        st.lastTickUptimeMs = up
        st.lastTickBootCount = bootCount()
        if (up - lastSave > 60_000) { lastSave = up; Guard.saveState() }
        updateNotification(d.locked, d.reason)
    }

    private var outsideSince = 0L
    private var lastForeignAudit = 0L
    private var lastLockNowAudit = 0L

    /** Voller Schutz: Im Sperr-Modus kann nur Erlaubtes vorne sein (Telegram, Signal, Telefon, Wecker). */
    private fun enforceOwner(on: Boolean): Boolean {
        if (LockActivity.isResumed) return true
        if (DeviceOwner.lockTaskActive(this)) { lockedInAllowed = true; return false }   // Kind ist in einer erlaubten App
        if (on) relaunchLock()
        val now = SystemClock.uptimeMillis()
        val hiddenFor = now - LockActivity.lastVisibleUptime
        if (LockActivity.lastVisibleUptime > 0 && hiddenFor > 20_000 && now - lastBypassAudit > 120_000) {
            lastBypassAudit = now
            Guard.audit.write("UMGEHUNG", "⚠ Gesperrt, aber Sperr-Modus nicht aktiv (seit ${hiddenFor / 1000} s) – wird neu gestartet")
        }
        return false
    }

    /**
     * Einfacher Schutz (ohne Geräte-Verwaltung), bewusst hart:
     * - erlaubte App vorne (Telegram, Signal, Telefon, Wecker) → in Ruhe lassen
     * - alles andere → Vollbild-Overlay + Sperrbildschirm sofort zurück
     * - bleibt das Kind > 12 s woanders (Overlay weggewischt, App geschlossen, Trick) → Bildschirm aus (Geräteadministrator)
     */
    private fun enforceWeak(on: Boolean): Boolean {
        val now = SystemClock.uptimeMillis()
        if (LockActivity.isResumed) { WeakGuard.setOverlay(this, false); outsideSince = 0; return true }
        val fg = ForegroundApp.current(this)
        // Eigene App zählt NICHT als erlaubt: außer dem Sperrbildschirm hat FrohLock in der Sperre nichts vorne zu suchen
        // (und direkt nach dem Wegwischen meldet Android kurz noch FrohLock als vorderste App).
        val inAllowed = (fg != null && fg != packageName && fg in AllowedApps.packages(this)) ||
            now < AllowedApps.launchGraceUntilUptime
        if (BuildConfig.ALLOW_DEBUGGING) android.util.Log.i("FrohLock", "weak fg=$fg allowed=$inAllowed resumed=${LockActivity.isResumed}")
        // Ohne Nutzungsdaten weiß FrohLock nicht, was vorne ist: dann nur echte Telefonate und die
        // ersten 60 s nach „Anrufen" frei lassen – Messenger brauchen die Nutzungsdaten-Erlaubnis.
        val free = inAllowed || (fg == null && (CallState.busy(this) || now < LockActivity.callGraceUntilUptime))
        if (free) { lockedInAllowed = true; WeakGuard.setOverlay(this, false); outsideSince = 0; return false }

        WeakGuard.setOverlay(this, true)
        if (!on) { outsideSince = 0; return WeakGuard.isShowing }
        relaunchLock()
        if (fg != null && fg != homePackage() && now - lastForeignAudit > 120_000) {
            lastForeignAudit = now
            Guard.audit.write("UMGEHUNG", "⚠ Während der Sperre war „${appLabel(fg)}“ vorne – Sperre wieder darübergelegt")
        }
        if (outsideSince == 0L) outsideSince = now
        if (now - outsideSince > 12_000 && WeakGuard.isAdminActive(this)) {
            outsideSince = now
            try { getSystemService(android.app.admin.DevicePolicyManager::class.java).lockNow() } catch (_: Exception) { }
            if (now - lastLockNowAudit > 120_000) {
                lastLockNowAudit = now
                Guard.audit.write("UMGEHUNG", "⚠ Sperrbildschirm wurde umgangen" + (if (fg != null) " („${appLabel(fg)}“ vorne)" else "") +
                    " – Bildschirm ausgeschaltet")
            }
        }
        return WeakGuard.isShowing
    }

    private fun relaunchLock() {
        val now = SystemClock.uptimeMillis()
        if (now - lastLockLaunch < 1_500) return
        lastLockLaunch = now
        WeakGuard.openLock(this)
    }

    private fun homePackage(): String? = try {
        packageManager.resolveActivity(Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_HOME), 0)?.activityInfo?.packageName
    } catch (_: Exception) { null }

    private fun appLabel(pkg: String): String = try {
        packageManager.getApplicationLabel(packageManager.getApplicationInfo(pkg, 0)).toString()
    } catch (_: Exception) { pkg }

    /**
     * Gegenprobe: Androids eigene Bildschirm-Ereignisse (seit Mitternacht) gegen das, was FrohLock gesehen hat.
     * Fehlt FrohLock ≥ 5 Min, wird nachgezählt und der Admin sieht es unter „Auffälligkeiten".
     */
    private fun crossCheck() {
        val e = Guard.enforcer
        if (!e.isConfigured) return
        // Androids Zeitstempel laufen mit der Geräteuhr – Mitternacht der Vertrauenszeit darauf umrechnen.
        val dayStartWall = e.todayStartMillis() - Guard.clock.offsetMillis
        val android = ScreenEvents.interactiveUnlockedSecondsSince(this, dayStartWall)
        if (android < 0) return
        val missed = e.crossCheck(android)
        if (missed > 0) {
            Guard.saveState()
            net.execute { netRound(force = true) }
        }
    }

    /** Zeitzone des Handys umgestellt? FrohLock rechnet fest in der gemerkten Zone; bei Eltern-Freigabe übernehmen. */
    private fun checkZone() {
        val other = Guard.clock.systemZoneMismatch() ?: run { lastZoneWarned = ""; return }
        if (isRelaxed()) {
            Guard.clock.adoptSystemZone()
            Guard.audit.write("POLICY", "Neue Zeitzone übernommen (Eltern-Freigabe): $other")
            return
        }
        if (lastZoneWarned == other) return
        lastZoneWarned = other
        Guard.audit.write("UMGEHUNG", "⚠ Zeitzone des Handys auf $other umgestellt – FrohLock rechnet weiter in ${Guard.clock.zone.id} " +
            "(Eltern-Bereich → Einstellungen freigeben, dann wird die neue Zone übernommen)")
    }

    /** Lief FrohLock nicht, obwohl das Gerät wach war? (Prozess beendet – z. B. durch das System.) */
    private fun detectGap() {
        val st = Guard.state
        if (st.lastTickWallUnix == 0L) return
        val nowWall = System.currentTimeMillis() / 1000
        val sameBoot = st.lastTickBootCount == bootCount() && st.lastTickBootCount >= 0
        val awakeGap = (SystemClock.uptimeMillis() - st.lastTickUptimeMs) / 1000
        if (sameBoot && awakeGap > 120) {
            Guard.audit.write("LUECKE", "⚠ FrohLock lief nicht ${Guard.enforcer.localTime(st.lastTickWallUnix)}–${Guard.enforcer.localTime(nowWall)} " +
                "(${awakeGap / 60} Min wach) – Bildschirmzeit wird aus Androids Aufzeichnung nachgezählt")
        } else if (!sameBoot) {
            Guard.audit.write("DIENST", "Gerät wurde neu gestartet (zuletzt aktiv ${Guard.enforcer.localTime(st.lastTickWallUnix)})")
        }
    }

    private fun userSetupComplete(): Boolean =
        try { Settings.Secure.getInt(contentResolver, "user_setup_complete", 1) == 1 } catch (_: Exception) { true }

    private fun bootCount(): Int = try { Settings.Global.getInt(contentResolver, Settings.Global.BOOT_COUNT) } catch (_: Exception) { -1 }

    private fun isRelaxed() = Guard.state.relaxedUntilUnix > System.currentTimeMillis() / 1000
    private fun isRelaxedExpired() = Guard.state.relaxedUntilUnix in 1..(System.currentTimeMillis() / 1000)

    // ---------- Netz ----------

    private fun netRound(force: Boolean) {
        try {
            val p = Guard.pairing() ?: return
            if (!p.registered) { Sync.tryPendingRegistration(this); return }
            val now = SystemClock.elapsedRealtime()
            val on = screenOn()
            val fullEvery = if (on) 30_000L else 120_000L
            if (force || now - lastFullSync >= fullEvery) {
                lastFullSync = now
                Sync.fullSync(this)
            } else if (on) {
                Sync.fastCommands(this)
            }
        } catch (e: Exception) {
            Guard.audit.write("NETZ", "Fehler: ${e.javaClass.simpleName}")
        }
    }

    // ---------- Benachrichtigung ----------

    private fun startInForeground() {
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel(CH_GUARD, "Schutz", NotificationManager.IMPORTANCE_MIN).apply {
            setShowBadge(false)
        })
        Reminders.ensureChannel(this)
        val n = notification("FrohLock passt auf")
        if (Build.VERSION.SDK_INT >= 34) {
            val type = if (DeviceOwner.isDeviceOwner(this)) ServiceInfo.FOREGROUND_SERVICE_TYPE_SYSTEM_EXEMPTED
                       else ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE
            try { startForeground(1, n, type) } catch (_: Exception) {
                startForeground(1, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
            }
        } else startForeground(1, n)
    }

    private fun notification(text: String): Notification {
        val pi = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        return Notification.Builder(this, CH_GUARD)
            .setSmallIcon(R.drawable.ic_stat_lock)
            .setContentTitle("FrohLock")
            .setContentText(text)
            .setOngoing(true)
            .setContentIntent(pi)
            .build()
    }

    private fun updateNotification(locked: Boolean, reason: String) {
        val e = Guard.enforcer
        val text = when {
            !e.isConfigured -> "Noch nicht eingerichtet"
            locked -> "Gesperrt"
            else -> {
                val b = e.todayBudgetMinutes()
                if (b > 0) "Heute ${e.todayUsageMinutes()} von $b Min" else "Heute ${e.todayUsageMinutes()} Min"
            }
        }
        if (text == lastNotifText) return
        lastNotifText = text
        getSystemService(NotificationManager::class.java).notify(1, notification(text))
    }

    companion object {
        const val CH_GUARD = "guard"
        const val ACTION_POKE = "poke"
        const val ACTION_SYNC = "sync"
        @Volatile var instance: GuardService? = null; private set
        /** Eltern geben FrohLock gerade frei – Abschalten des Geräteadministrators ist dann gewollt. */
        @Volatile var releasing = false; private set

        fun start(ctx: Context) {
            try {
                ctx.startForegroundService(Intent(ctx, GuardService::class.java))
            } catch (e: Exception) {
                Guard.init(ctx)
                Guard.audit.write("DIENST", "⚠ Start fehlgeschlagen: ${e.javaClass.simpleName}")
            }
        }

        /** Sofort neu entscheiden (nach PIN, Fernbefehl …). */
        fun poke(ctx: Context) {
            instance?.handler?.post { instance?.tick() } ?: start(ctx)
        }

        fun syncNow(ctx: Context) {
            try { ctx.startForegroundService(Intent(ctx, GuardService::class.java).setAction(ACTION_SYNC)) } catch (_: Exception) { }
        }

        /** Heartbeat inkl. Android-Angaben (Admin zeigt „Geräte-Verwaltung ✓/❌"). */
        fun buildStatus(ctx: Context): JSONObject {
            val pm = ctx.getSystemService(PowerManager::class.java)
            val km = ctx.getSystemService(KeyguardManager::class.java)
            val on = pm.isInteractive && !km.isKeyguardLocked
            val locked = Guard.enforcer.decide().locked
            // „Overlay"-Alter für den Admin: 0 = alles gut; bei Sperre ohne sichtbaren Sperrbildschirm wächst es.
            val agentAge = if (!locked || !on || LockActivity.isResumed) 0
                           else ((SystemClock.uptimeMillis() - LockActivity.lastVisibleUptime) / 1000).toInt().coerceAtLeast(0)
            return Guard.enforcer.buildStatus(Guard.appVersion, on, agentAge)
                .put("platform", "android")
                .put("deviceOwner", DeviceOwner.isDeviceOwner(ctx))
                .put("lockTaskActive", DeviceOwner.lockTaskActive(ctx))
                .put("deviceInfo", Sync.deviceInfo(ctx)
                    .put("overlay", WeakGuard.canOverlay(ctx))
                    .put("deviceAdmin", WeakGuard.isAdminActive(ctx))
                    .put("usageAccess", ForegroundApp.hasAccess(ctx))
                    .put("autoUpdate", Updater.canUpdateSilently(ctx) && !Guard.state.silentUpdateBlocked)
                    .put("installer", Updater.installerOfRecord(ctx) ?: "")
                    .put("allowedApps", org.json.JSONArray(AllowedApps.installedMessengers(ctx).map { it.second }))
                    // Gegenprobe für den Admin: was Android sah, was FrohLock sah, was nachgezählt wurde.
                    .put("onMinutes", Guard.enforcer.todayOnMinutes())
                    .put("androidOnMinutes", Guard.enforcer.todayAndroidOnMinutes())
                    .put("missedMinutes", Guard.enforcer.todayMissedMinutes())
                    .put("allowedInLockMinutes", Guard.enforcer.todayAllowedInLockMinutes())
                    .put("zone", Guard.clock.zone.id)
                    .put("zoneMismatch", Guard.clock.systemZoneMismatch() ?: "")
                    .put("missing", org.json.JSONArray(if (DeviceOwner.isDeviceOwner(ctx)) emptyList() else WeakGuard.missing(ctx))))
                .put("relaxedUntilUnix", Guard.state.relaxedUntilUnix)
                .put("lockVisible", LockActivity.isResumed || WeakGuard.isShowing)
        }

        /** Alles freigeben und FrohLock zum Entfernen vorbereiten (Eltern-PIN oder Admin-Befehl). */
        fun releaseDevice(ctx: Context, reason: String) {
            releasing = true
            Guard.audit.write("UNINSTALL", "Freigabe: $reason")
            DeviceOwner.release(ctx)
            WeakGuard.setOverlay(ctx, false)
            try {
                ctx.getSystemService(android.app.admin.DevicePolicyManager::class.java).removeActiveAdmin(DeviceOwner.admin(ctx))
            } catch (_: Exception) { }
            Guard.clearAll()
            LockActivity.finishAll()
            Reminders.notifyRemoved(ctx)
            instance?.stopSelf()
        }
    }
}
