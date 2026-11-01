package de.froehlichdienste.frohlock.android

import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import android.os.Build
import de.froehlichdienste.frohlock.core.sha256Hex
import java.io.File
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import java.util.Base64

/**
 * Fern-Update: signiertes Manifest („android|Version|SHA-256") → Download mit Inaktivitäts-Frist →
 * Prüfsumme → stille Installation (als Geräte-Verwalter ohne Rückfrage). Android prüft zusätzlich,
 * dass das neue APK mit DEMSELBEN Schlüssel signiert ist.
 * Lehre aus Windows v0.28.0: ein hängender Download darf das Update nie für immer blockieren.
 */
object Updater {
    private const val DEADLINE_MS = 15 * 60_000L
    private const val INACTIVITY_MS = 60_000
    @Volatile private var runningSince = 0L
    @Volatile private var lastNoUpdateLog = 0L

    /** Zuletzt angebotene, noch nicht bestätigte Installation (für den Knopf „Update installieren"). */
    @Volatile var pendingConfirm: Intent? = null

    /** Wer hat FrohLock installiert? Nur wenn FrohLock selbst die Quelle ist, darf es sich still aktualisieren. */
    fun installerOfRecord(ctx: Context): String? = try {
        if (Build.VERSION.SDK_INT >= 30) ctx.packageManager.getInstallSourceInfo(ctx.packageName).installingPackageName
        else @Suppress("DEPRECATION") ctx.packageManager.getInstallerPackageName(ctx.packageName)
    } catch (_: Exception) { null }

    /**
     * Klappt das nächste Update ohne Rückfrage? Ja, als Geräte-Verwalter – oder ab Android 12, wenn FrohLock
     * selbst die Installationsquelle ist (nach dem ersten bestätigten Update) und Apps installieren darf.
     */
    fun canUpdateSilently(ctx: Context): Boolean =
        DeviceOwner.isDeviceOwner(ctx) || (Build.VERSION.SDK_INT >= 31 &&
            installerOfRecord(ctx) == ctx.packageName && ctx.packageManager.canRequestPackageInstalls())

    fun isNewer(current: String, target: String): Boolean {
        fun parts(v: String) = v.split(".").map { it.toIntOrNull() ?: 0 }.let { it + List(maxOf(0, 3 - it.size)) { 0 } }
        val a = parts(current); val b = parts(target)
        for (i in 0 until maxOf(a.size, b.size)) {
            val x = a.getOrElse(i) { 0 }; val y = b.getOrElse(i) { 0 }
            if (x != y) return x < y
        }
        return false
    }

    /** Vom Knopf in der App angestoßen: Bestätigungsdialog sofort zeigen statt Benachrichtigung. */
    @Volatile var userInitiated = false

    /**
     * Knopf „Automatische Updates einrichten": installiert die aktuelle Version (notfalls dieselbe noch einmal)
     * über FrohLock selbst. Danach ist FrohLock die Installationsquelle und darf sich still aktualisieren.
     */
    fun setupAutoUpdate(ctx: Context): String? {
        val p = Guard.pairing()?.takeIf { it.registered } ?: return "Nicht mit dem Admin verbunden"
        userInitiated = true
        return try {
            tryUpdate(ctx, ServerApi(p.serverUrl, p.deviceId, p.token), force = true)
            null
        } catch (e: Exception) { e.message ?: "Keine Verbindung" }
    }

    fun tryUpdate(ctx: Context, api: ServerApi, force: Boolean = false) {
        val now = System.currentTimeMillis()
        if (runningSince > 0 && now - runningSince < DEADLINE_MS + 60_000) return
        // Ohne Geräte-Verwaltung muss ein Mensch „Installieren" tippen – höchstens alle 6 Stunden anbieten,
        // sonst käme bei jedem Abruf (alle 30 s) ein neuer Download und Dialog.
        val silent = canUpdateSilently(ctx)
        if (!force && !silent && now / 1000 - Guard.state.lastUpdatePromptUnix < 6 * 3600) return
        if (runningSince > 0) Guard.audit.write("UPDATE", "⚠ Hängendes Update nach ${(now - runningSince) / 60_000} Min aufgegeben – neuer Versuch")
        runningSince = now
        try {
            val m = api.androidManifest() ?: return
            // force: auch dieselbe Version neu installieren (macht FrohLock zur Installationsquelle).
            if (!isNewer(Guard.appVersion, m.version) && !(force && !isNewer(m.version, Guard.appVersion))) {
                if (now - lastNoUpdateLog > 6 * 3600_000) { lastNoUpdateLog = now; Guard.audit.write("UPDATE", "Kein Update (${Guard.appVersion} aktuell)") }
                return
            }
            val sigOk = try {
                Guard.verifier.verify("android|${m.version}|${m.sha256}".toByteArray(), Base64.getDecoder().decode(m.signatureBase64))
            } catch (_: Exception) { false }
            if (!sigOk) { Guard.audit.write("UPDATE", "⚠ Manifest-Signatur ungültig – verworfen"); return }

            Guard.audit.write("UPDATE", "Lade ${m.version} …")
            val apk = File(ctx.cacheDir, "update.apk")
            download(m.url, apk, now)
            val hash = sha256Hex(apk.readBytes())
            if (hash != m.sha256) { apk.delete(); Guard.audit.write("UPDATE", "⚠ Prüfsumme falsch – verworfen"); return }
            Guard.audit.write("UPDATE", if (silent) "Installiere ${m.version} automatisch …"
                else "${m.version} bereit – einmal am Gerät bestätigen, danach kommen Updates automatisch")
            if (!silent && !force) { Guard.state.lastUpdatePromptUnix = now / 1000; Guard.saveState() }
            install(ctx, apk)
        } catch (e: Exception) {
            Guard.audit.write("UPDATE", "Fehler: ${e.javaClass.simpleName} ${e.message ?: ""}".trim())
        } finally {
            runningSince = 0
        }
    }

    private fun download(url: String, target: File, started: Long) {
        val conn = URL(url).openConnection() as HttpURLConnection
        conn.connectTimeout = 20_000
        conn.readTimeout = INACTIVITY_MS          // 60 s ohne Bytes → Abbruch
        conn.instanceFollowRedirects = true
        try {
            if (conn.responseCode !in 200..299) throw IOException("HTTP ${conn.responseCode}")
            val tmp = File(target.path + ".part")
            conn.inputStream.use { input ->
                tmp.outputStream().use { out ->
                    val buf = ByteArray(64 * 1024)
                    while (true) {
                        val n = input.read(buf)
                        if (n < 0) break
                        out.write(buf, 0, n)
                        if (System.currentTimeMillis() - started > DEADLINE_MS) throw IOException("Zeitlimit 15 Min")
                    }
                }
            }
            target.delete(); tmp.renameTo(target)
        } finally {
            conn.disconnect()
        }
    }

    private fun install(ctx: Context, apk: File) {
        val installer = ctx.packageManager.packageInstaller
        val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL)
        params.setAppPackageName(ctx.packageName)
        if (Build.VERSION.SDK_INT >= 31) params.setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_NOT_REQUIRED)
        // Ab Android 14: Update-Hoheit beanspruchen – künftige Updates nur noch über FrohLock selbst.
        if (Build.VERSION.SDK_INT >= 34) params.setRequestUpdateOwnership(true)
        val id = installer.createSession(params)
        installer.openSession(id).use { s ->
            s.openWrite("frohlock.apk", 0, apk.length()).use { out -> apk.inputStream().use { it.copyTo(out) }; s.fsync(out) }
            val pi = PendingIntent.getBroadcast(ctx, id, Intent(ctx, InstallResultReceiver::class.java),
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_MUTABLE)
            s.commit(pi.intentSender)
        }
    }
}

class InstallResultReceiver : BroadcastReceiver() {
    override fun onReceive(ctx: Context, intent: Intent) {
        Guard.init(ctx)
        val status = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE)
        when (status) {
            PackageInstaller.STATUS_PENDING_USER_ACTION -> {
                // Ohne Geräte-Verwaltung muss jemand „Aktualisieren" tippen. Nicht mitten ins Spiel springen:
                // nur eine Benachrichtigung, die Eltern antippen, wenn es passt.
                @Suppress("DEPRECATION")
                val confirm = intent.getParcelableExtra<Intent>(Intent.EXTRA_INTENT) ?: return
                confirm.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                Updater.pendingConfirm = confirm
                if (Updater.userInitiated) {
                    // Jemand hat in der App getippt und schaut drauf → Dialog direkt öffnen.
                    Updater.userInitiated = false
                    try { ctx.startActivity(confirm) } catch (_: Exception) { Reminders.notifyUpdate(ctx, confirm) }
                } else Reminders.notifyUpdate(ctx, confirm)
            }
            PackageInstaller.STATUS_SUCCESS -> { Updater.pendingConfirm = null; Guard.audit.write("UPDATE", "Installiert ✓") }
            else -> {
                Updater.userInitiated = false
                // Abgebrochen/abgelehnt: bald wieder anbieten statt 6 Stunden zu warten.
                Guard.state.lastUpdatePromptUnix = System.currentTimeMillis() / 1000 - 5 * 3600 - 30 * 60
                Guard.saveState()
                Guard.audit.write("UPDATE", "⚠ Installation nicht abgeschlossen ($status): ${intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE) ?: ""} – neuer Versuch in 30 Min")
            }
        }
    }
}
