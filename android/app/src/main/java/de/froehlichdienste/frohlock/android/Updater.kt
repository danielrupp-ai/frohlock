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

    fun isNewer(current: String, target: String): Boolean {
        fun parts(v: String) = v.split(".").map { it.toIntOrNull() ?: 0 }.let { it + List(maxOf(0, 3 - it.size)) { 0 } }
        val a = parts(current); val b = parts(target)
        for (i in 0 until maxOf(a.size, b.size)) {
            val x = a.getOrElse(i) { 0 }; val y = b.getOrElse(i) { 0 }
            if (x != y) return x < y
        }
        return false
    }

    fun tryUpdate(ctx: Context, api: ServerApi) {
        val now = System.currentTimeMillis()
        if (runningSince > 0 && now - runningSince < DEADLINE_MS + 60_000) return
        if (runningSince > 0) Guard.audit.write("UPDATE", "⚠ Hängendes Update nach ${(now - runningSince) / 60_000} Min aufgegeben – neuer Versuch")
        runningSince = now
        try {
            val m = api.androidManifest() ?: return
            if (!isNewer(Guard.appVersion, m.version)) {
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
            Guard.audit.write("UPDATE", "Installiere ${m.version} …")
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
                // Ohne Geräte-Verwaltung muss jemand „Aktualisieren" tippen.
                Guard.audit.write("UPDATE", "Wartet auf Bestätigung am Gerät")
                @Suppress("DEPRECATION")
                val confirm = intent.getParcelableExtra<Intent>(Intent.EXTRA_INTENT)
                confirm?.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                try { if (confirm != null) ctx.startActivity(confirm) } catch (_: Exception) { }
            }
            PackageInstaller.STATUS_SUCCESS -> Guard.audit.write("UPDATE", "Installiert ✓")
            else -> Guard.audit.write("UPDATE", "⚠ Installation fehlgeschlagen ($status): ${intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE) ?: ""}")
        }
    }
}
