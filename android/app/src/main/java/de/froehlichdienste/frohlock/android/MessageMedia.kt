package de.froehlichdienste.frohlock.android

import android.content.ContentValues
import android.content.Context
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import de.froehlichdienste.frohlock.core.ParentMessage
import java.io.File
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest

/**
 * Video zu einer Eltern-Nachricht (Geburtstagsvideo 20.11.2026): vorab laden (Prüfsumme!), beim ersten Ansehen
 * in die Galerie (Filme/FrohLock) speichern, Datei behalten zum Wiederansehen in FrohLock.
 */
object MessageMedia {
    private const val GIVE_UP_AFTER_S = 20 * 60L   // Video nach 20 Min noch nicht da → Nachricht ohne Video zeigen
    @Volatile private var lastTryMs = 0L
    @Volatile private var running = false

    private fun dir(ctx: Context) = File(ctx.filesDir, "messages").apply { mkdirs() }
    fun videoFile(ctx: Context, m: ParentMessage) = File(dir(ctx), "${m.id.filter { it.isLetterOrDigit() }}.mp4")
    fun hasVideo(ctx: Context, m: ParentMessage) = m.hasVideo && videoFile(ctx, m).length() > 0

    fun savedVideos(ctx: Context): List<File> =
        (dir(ctx).listFiles { f -> f.name.endsWith(".mp4") } ?: emptyArray()).sortedBy { it.lastModified() }

    /** Darf die Nachricht jetzt erscheinen? (Zeitpunkt erreicht und Video da – oder Video kommt nicht.) */
    fun readyToShow(ctx: Context, m: ParentMessage, nowUnix: Long): Boolean {
        if (nowUnix < m.showFromUnix) return false
        if (!m.hasVideo || hasVideo(ctx, m)) return true
        return nowUnix > maxOf(m.showFromUnix, 0) + GIVE_UP_AFTER_S && m.showFromUnix > 0
    }

    /** Läuft auf dem Netz-Thread. Höchstens alle 60 s ein Versuch. */
    fun prefetch(ctx: Context, m: ParentMessage) {
        if (!m.hasVideo || hasVideo(ctx, m) || running) return
        val now = System.currentTimeMillis()
        if (now - lastTryMs < 60_000) return
        lastTryMs = now; running = true
        try {
            val target = videoFile(ctx, m)
            val tmp = File(target.path + ".part")
            val conn = URL(m.videoUrl).openConnection() as HttpURLConnection
            conn.connectTimeout = 20_000; conn.readTimeout = 60_000
            try {
                if (conn.responseCode !in 200..299) throw IOException("HTTP ${conn.responseCode}")
                val md = MessageDigest.getInstance("SHA-256")
                conn.inputStream.use { input -> tmp.outputStream().use { out ->
                    val buf = ByteArray(64 * 1024)
                    while (true) { val n = input.read(buf); if (n < 0) break; md.update(buf, 0, n); out.write(buf, 0, n) }
                } }
                val sha = md.digest().joinToString("") { "%02x".format(it) }
                if (sha != m.videoSha256) { tmp.delete(); throw IOException("Prüfsumme falsch") }
                target.delete(); tmp.renameTo(target)
                Guard.audit.write("NACHRICHT", "Video geladen (${target.length() / 1024} KB)")
            } finally { conn.disconnect() }
        } catch (e: Exception) {
            Guard.audit.write("NACHRICHT", "Video laden fehlgeschlagen: ${e.javaClass.simpleName} ${e.message ?: ""}".trim())
        } finally { running = false }
    }

    /** In die Galerie (Filme/FrohLock) kopieren. Ab Android 10 ohne Extra-Berechtigung. Liefert true bei Erfolg. */
    fun saveToGallery(ctx: Context, m: ParentMessage): Boolean {
        val src = videoFile(ctx, m)
        if (!src.exists() || Build.VERSION.SDK_INT < 29) return false
        val name = m.videoName.ifBlank { "FrohLock_${m.id.take(8)}" }.replace(Regex("[^\\p{L}\\p{N}_ -]"), "") + ".mp4"
        return try {
            val r = ctx.contentResolver
            val values = ContentValues().apply {
                put(MediaStore.Video.Media.DISPLAY_NAME, name)
                put(MediaStore.Video.Media.MIME_TYPE, "video/mp4")
                put(MediaStore.Video.Media.RELATIVE_PATH, Environment.DIRECTORY_MOVIES + "/FrohLock")
                put(MediaStore.Video.Media.IS_PENDING, 1)
            }
            val uri = r.insert(MediaStore.Video.Media.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY), values) ?: return false
            r.openOutputStream(uri)?.use { out -> src.inputStream().use { it.copyTo(out) } } ?: return false
            values.clear(); values.put(MediaStore.Video.Media.IS_PENDING, 0)
            r.update(uri, values, null, null)
            Guard.audit.write("NACHRICHT", "Video in der Galerie gespeichert (Filme/FrohLock/$name)")
            true
        } catch (e: Exception) {
            Guard.audit.write("NACHRICHT", "Galerie speichern fehlgeschlagen: ${e.javaClass.simpleName}")
            false
        }
    }
}
