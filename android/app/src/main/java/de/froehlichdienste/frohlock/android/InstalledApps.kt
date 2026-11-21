package de.froehlichdienste.frohlock.android

import android.content.Context
import android.content.Intent
import android.os.SystemClock
import org.json.JSONArray
import org.json.JSONObject

/**
 * Liste der Apps mit Startsymbol (Name + Paketname) für die Admin-Karte „Ausnahme für eine App“ –
 * damit die Eltern dort die App auswählen können, statt einen Paketnamen abzutippen.
 * Nur WELCHE Apps installiert sind, keine Nutzung. Geht nur mit, wenn sie sich geändert hat oder
 * alle 30 Minuten (der Server behält die letzte Liste), damit der Heartbeat klein bleibt.
 */
object InstalledApps {
    private const val RESEND_MS = 30 * 60_000L
    @Volatile private var lastHash = 0
    @Volatile private var lastSentUptime = 0L
    @Volatile private var lastCheckUptime = 0L

    fun list(ctx: Context): List<Pair<String, String>> = try {
        val pm = ctx.packageManager
        val i = Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER)
        pm.queryIntentActivities(i, 0)
            .mapNotNull { ri ->
                val pkg = ri.activityInfo?.packageName ?: return@mapNotNull null
                if (pkg == ctx.packageName) return@mapNotNull null
                pkg to (try { ri.loadLabel(pm).toString() } catch (_: Exception) { pkg })
            }
            .distinctBy { it.first }
            .sortedBy { it.second.lowercase() }
            .take(400)
    } catch (_: Exception) { emptyList() }

    /** Legt `installedApps` in [info] ab, wenn fällig. */
    fun maybePut(ctx: Context, info: JSONObject) {
        val now = SystemClock.elapsedRealtime()
        // Liste lesen kostet etwas – höchstens alle 5 Minuten nachsehen.
        if (lastCheckUptime != 0L && now - lastCheckUptime < 5 * 60_000L) return
        lastCheckUptime = now
        val apps = list(ctx)
        if (apps.isEmpty()) return
        val hash = apps.hashCode()
        if (hash == lastHash && lastSentUptime != 0L && now - lastSentUptime < RESEND_MS) return
        info.put("installedApps", JSONArray().also { a ->
            apps.forEach { (p, n) -> a.put(JSONObject().put("p", p).put("n", n.take(60))) }
        })
        lastHash = hash
        lastSentUptime = now
    }
}
