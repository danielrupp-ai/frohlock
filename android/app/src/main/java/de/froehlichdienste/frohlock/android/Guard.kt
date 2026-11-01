package de.froehlichdienste.frohlock.android

import android.content.Context
import android.content.res.Configuration
import de.froehlichdienste.frohlock.BuildConfig
import de.froehlichdienste.frohlock.core.Enforcer
import de.froehlichdienste.frohlock.core.FileAudit
import de.froehlichdienste.frohlock.core.LockConfig
import de.froehlichdienste.frohlock.core.RuntimeState
import de.froehlichdienste.frohlock.core.SignatureVerifier
import de.froehlichdienste.frohlock.core.SignedEnvelope
import de.froehlichdienste.frohlock.core.TrustedClock
import org.json.JSONObject
import java.io.File
import java.time.ZoneId

/** Kopplung mit der Brücke. Token liegt nur im App-privaten Speicher (für das Kind unerreichbar). */
data class Pairing(
    val serverUrl: String,
    val deviceId: String = "",
    val token: String = "",
    val deviceName: String = "",
    /** Kopplungscode aus dem QR-Code, solange die Registrierung noch aussteht. */
    val pendingCode: String = "",
) {
    val registered get() = deviceId.isNotEmpty() && token.isNotEmpty()

    fun toJson(): JSONObject = JSONObject().put("serverUrl", serverUrl).put("deviceId", deviceId)
        .put("token", token).put("deviceName", deviceName).put("pendingCode", pendingCode)

    companion object {
        const val DEFAULT_SERVER = "https://frohlock.froehlichdienste.de"
        fun fromJson(o: JSONObject) = Pairing(
            o.optString("serverUrl", DEFAULT_SERVER), o.optString("deviceId", ""), o.optString("token", ""),
            o.optString("deviceName", ""), o.optString("pendingCode", ""),
        )
    }
}

/**
 * Vertrauenszeit: System-Uhr (vom Gerät erzwungen automatisch) + Korrektur aus dem Date-Header
 * des Servers + „Boden", unter den die Zeit nie zurückläuft (Schutz gegen Uhr-Zurückstellen offline).
 */
class AndroidClock(private val floorFile: File) : TrustedClock {
    @Volatile private var offsetMs = 0L
    @Volatile private var floorMs = try { floorFile.readText().trim().toLong() } catch (_: Exception) { 0L }
    private var lastPersisted = floorMs

    override val zone: ZoneId get() = ZoneId.systemDefault()

    @Synchronized
    override fun nowMillis(): Long {
        val raw = System.currentTimeMillis() + offsetMs
        val t = maxOf(raw, floorMs)
        floorMs = t
        if (t - lastPersisted > 5 * 60_000) persist()
        return t
    }

    /** Server-Zeit ist maßgeblich (korrigiert in beide Richtungen). Kleine Abweichungen ignorieren. */
    @Synchronized
    fun acceptServerTime(serverMs: Long) {
        val diff = serverMs - System.currentTimeMillis()
        offsetMs = if (kotlin.math.abs(diff) > 120_000) diff else 0L
        floorMs = serverMs
        persist()
    }

    val offsetMinutes get() = offsetMs / 60_000

    private fun persist() {
        lastPersisted = floorMs
        try { floorFile.writeText(floorMs.toString()) } catch (_: Exception) { }
    }
}

/** Ein Prozess, ein Gehirn: Dienst, Sperrbildschirm und Eltern-Bereich teilen sich diesen Zustand. */
object Guard {
    lateinit var app: Context; private set
    lateinit var audit: FileAudit; private set
    lateinit var clock: AndroidClock; private set
    lateinit var state: RuntimeState; private set
    lateinit var enforcer: Enforcer; private set
    val verifier by lazy { SignatureVerifier() }

    private val dir get() = app.filesDir
    private val pairingFile get() = File(dir, "pairing.json")
    private val configFile get() = File(dir, "config.json")
    private val stateFile get() = File(dir, "state.json")
    private var initialized = false

    val appVersion: String get() = BuildConfig.VERSION_NAME

    @Synchronized
    fun init(ctx: Context) {
        if (initialized) return
        app = ctx.applicationContext
        audit = FileAudit(File(dir, "audit.log"))
        clock = AndroidClock(File(dir, "timefloor"))
        state = try { RuntimeState.fromJson(JSONObject(stateFile.readText())) } catch (_: Exception) { RuntimeState() }
        enforcer = Enforcer(clock, audit, state, { saveState() }, deviceWord())
        enforcer.setConfig(loadConfig())
        initialized = true
    }

    /** „das Handy" oder „das Tablet" – für liebe, passende Texte. */
    fun deviceWord(): String =
        if (app.resources.configuration.smallestScreenWidthDp >= 600) "das Tablet" else "das Handy"

    fun deviceNoun(): String = deviceWord().removePrefix("das ")

    @Synchronized
    fun saveState() {
        try {
            val tmp = File(dir, "state.json.tmp")
            tmp.writeText(state.toJson().toString())
            tmp.renameTo(stateFile)
        } catch (_: Exception) { }
    }

    // ---------- Kopplung ----------

    @Synchronized
    fun pairing(): Pairing? = try { Pairing.fromJson(JSONObject(pairingFile.readText())) } catch (_: Exception) { null }

    @Synchronized
    fun savePairing(p: Pairing) {
        pairingFile.writeText(p.toJson().toString())
    }

    @Synchronized
    fun clearAll() {
        pairingFile.delete(); configFile.delete()
        enforcer.setConfig(null)
    }

    // ---------- Signierte Konfiguration ----------

    private fun loadConfig(): LockConfig? = try {
        val env = SignedEnvelope.fromJson(JSONObject(configFile.readText()))
        verifier.openJson(env)?.let { LockConfig.fromJson(it) }
    } catch (_: Exception) { null }

    /** Neue signierte Config übernehmen: Signatur gültig + Version nicht älter (Anti-Rollback). */
    @Synchronized
    fun applyConfig(env: SignedEnvelope): LockConfig? {
        val json = verifier.openJson(env) ?: run { audit.write("CONFIG", "⚠ Ungültige Signatur – verworfen"); return null }
        val incoming = LockConfig.fromJson(json)
        val current = enforcer.currentConfig
        if (current != null && incoming.configVersion < current.configVersion) {
            audit.write("CONFIG", "Ältere Version v${incoming.configVersion} abgelehnt (habe v${current.configVersion})")
            return null
        }
        val p = pairing()
        if (p != null && p.deviceId.isNotEmpty() && incoming.deviceId != p.deviceId) {
            audit.write("CONFIG", "⚠ Config für fremdes Gerät – verworfen")
            return null
        }
        configFile.writeText(env.toJson().toString())
        val firstPin = current?.hasPin != true && incoming.hasPin
        enforcer.setConfig(incoming)
        // Erlaubte Apps können sich geändert haben → Sperr-Modus neu einrichten.
        if (current?.alwaysAllowedApps != incoming.alwaysAllowedApps) DeviceOwner.configureLockTask(app)
        audit.write("CONFIG", "v${incoming.configVersion} übernommen (${incoming.windows.size} Sperrzeiten, Limit ${incoming.dailyBudgetMinutes} min)")
        if (firstPin) audit.write("SETUP", "Eltern-PIN vorhanden – Schutz ist jetzt aktiv")
        return incoming
    }

    fun isTablet() = (app.resources.configuration.screenLayout and Configuration.SCREENLAYOUT_SIZE_MASK) >=
        Configuration.SCREENLAYOUT_SIZE_LARGE
}
