package de.froehlichdienste.frohlock.ui

import android.annotation.SuppressLint
import android.app.Activity
import android.app.admin.DevicePolicyManager
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.PersistableBundle
import android.os.PowerManager
import android.provider.Settings
import android.text.InputType
import android.view.View
import android.widget.LinearLayout
import android.widget.ScrollView
import de.froehlichdienste.frohlock.android.DeviceOwner
import de.froehlichdienste.frohlock.android.Guard
import de.froehlichdienste.frohlock.android.GuardService
import de.froehlichdienste.frohlock.android.Pairing
import de.froehlichdienste.frohlock.android.Provisioning
import de.froehlichdienste.frohlock.android.ServerApi
import de.froehlichdienste.frohlock.android.Sync
import de.froehlichdienste.frohlock.android.WeakGuard
import de.froehlichdienste.frohlock.core.PinHasher
import org.json.JSONArray
import org.json.JSONObject
import kotlin.concurrent.thread

/**
 * Einrichtung für Eltern – in drei Schritten: koppeln → PIN & Schlafenszeit → fertig.
 * Nach dem QR-Code ruft Android diesen Schritt mitten in der Handy-Einrichtung auf
 * (ADMIN_POLICY_COMPLIANCE); dann ist der Kopplungscode schon bekannt und alles läuft von selbst.
 */
class SetupActivity : Activity() {
    private val ui = Handler(Looper.getMainLooper())
    private lateinit var col: LinearLayout
    private var compliance = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        Guard.init(this)
        Ui.nightBackground(this)
        compliance = intent?.action == DevicePolicyManager.ACTION_ADMIN_POLICY_COMPLIANCE
        if (compliance) {
            @Suppress("DEPRECATION")
            val extras: PersistableBundle? = intent.getParcelableExtra(DevicePolicyManager.EXTRA_PROVISIONING_ADMIN_EXTRAS_BUNDLE)
            Provisioning.onProvisioned(this, extras)
        } else GuardService.start(this)
        val scroll = ScrollView(this)
        col = Ui.column(this, 24)
        scroll.addView(col)
        Ui.fitSystemBars(scroll)
        setContentView(scroll)
        next()
    }

    /** Nächsten offenen Schritt zeigen. */
    private fun next() {
        val p = Guard.pairing()
        when {
            intent.getBooleanExtra(EXTRA_REPAIR, false) -> { intent.removeExtra(EXTRA_REPAIR); showPairForm(null) }
            p == null || !p.registered -> if (p != null && p.pendingCode.isNotBlank()) autoPair(p) else showPairForm(null)
            !Guard.enforcer.isConfigured -> loadConfigThenPin()
            else -> showDone()
        }
    }

    private fun header(step: String, title: String, text: String) {
        col.removeAllViews()
        col.addView(Ui.text(this, "FrohLock einrichten · $step", 14f, 0xAAFFFFFF.toInt()))
        col.addView(Ui.text(this, title, 26f, bold = true))
        col.addView(Ui.text(this, text, 16f, 0xDDFFFFFF.toInt()))
    }

    private fun busy(title: String, text: String) {
        header("…", title, text)
        col.addView(android.widget.ProgressBar(this).apply { isIndeterminate = true })
    }

    // ---------- Schritt 1: Koppeln ----------

    private fun autoPair(p: Pairing) {
        busy("Verbinde mit FrohLock …", "Das Handy meldet sich mit dem Code aus dem QR-Code beim FrohLock-Admin an.")
        thread {
            val err = try {
                Sync.register(this, p.serverUrl, p.pendingCode, p.deviceName.ifBlank { Sync.defaultDeviceName(this) }); null
            } catch (e: Exception) { e.message ?: "Keine Verbindung" }
            ui.post { if (err == null) next() else showPairForm("Automatisch hat es nicht geklappt: $err") }
        }
    }

    private fun showPairForm(error: String?) {
        header("Schritt 1 von 3", "Mit dem Admin verbinden",
            "Den 6-stelligen Kopplungscode zeigt der FrohLock-Admin unter „Android-Gerät einrichten“ (unter dem QR-Code).")
        if (error != null) col.addView(Ui.text(this, "⚠ $error", 15f, Ui.WARN))
        val p = Guard.pairing()
        val code = Ui.input(this, "Kopplungscode (6 Ziffern)", InputType.TYPE_CLASS_NUMBER).apply { setText(p?.pendingCode ?: "") }
        val name = Ui.input(this, "Name des Geräts, z. B. Handy Danyil").apply { setText(p?.deviceName?.ifBlank { null } ?: Sync.defaultDeviceName(this@SetupActivity)) }
        val server = Ui.input(this, "Server-Adresse", InputType.TYPE_TEXT_VARIATION_URI).apply {
            setText(p?.serverUrl ?: Pairing.DEFAULT_SERVER); visibility = View.GONE
        }
        val msg = Ui.text(this, "", 15f, Ui.WARN)
        col.addView(code); col.addView(name); col.addView(server)
        col.addView(Ui.text(this, "Andere Server-Adresse …", 13f, 0x99FFFFFF.toInt()).apply {
            setOnClickListener { server.visibility = View.VISIBLE; visibility = View.GONE }
        })
        col.addView(msg)
        col.addView(Ui.button(this, "Verbinden") {
            val c = code.text.toString().trim()
            if (c.length < 6) { msg.text = "Bitte den 6-stelligen Code eingeben."; return@button }
            val url = server.text.toString().trim().trimEnd('/')
            if (!url.startsWith("https://")) { msg.text = "Die Server-Adresse muss mit https:// beginnen."; return@button }
            busy("Verbinde …", "Einen Moment.")
            thread {
                val err = try { Sync.register(this, url, c, name.text.toString().trim().ifBlank { Sync.defaultDeviceName(this) }); null }
                          catch (e: Exception) { e.message ?: "Keine Verbindung" }
                ui.post { if (err == null) next() else showPairForm(err) }
            }
        })
        if (compliance) col.addView(Ui.button(this, "Später einrichten", primary = false) { finishOk() })
    }

    // ---------- Schritt 2: PIN & Schlafenszeit ----------

    private fun loadConfigThenPin() {
        busy("Einstellungen laden …", "Vielleicht wurden sie schon im Admin vorbereitet.")
        thread {
            try { Sync.fetchConfig() } catch (_: Exception) { }
            ui.post { if (Guard.enforcer.isConfigured) showDone() else showPinForm(null) }
        }
    }

    private fun showPinForm(error: String?) {
        header("Schritt 2 von 3", "Eltern-PIN und Schlafenszeit",
            "Mit der PIN entsperrt ihr das Handy am Gerät. Alles Weitere (mehr Sperrzeiten, Tageslimit je Wochentag) " +
            "könnt ihr jederzeit im Admin ändern.")
        if (error != null) col.addView(Ui.text(this, "⚠ $error", 15f, Ui.WARN))
        val pin1 = Ui.pinInput(this, "Eltern-PIN (mind. 4 Ziffern)")
        val pin2 = Ui.pinInput(this, "PIN wiederholen")
        val email = Ui.input(this, "E-Mail für „PIN vergessen?“ (optional)", InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_EMAIL_ADDRESS)
        col.addView(pin1); col.addView(pin2); col.addView(email)
        col.addView(Ui.text(this, "Schlafenszeit (jeden Tag)", 17f, bold = true))
        val row = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        val from = Ui.input(this, "von", InputType.TYPE_CLASS_DATETIME or InputType.TYPE_DATETIME_VARIATION_TIME).apply { setText("20:30") }
        val to = Ui.input(this, "bis", InputType.TYPE_CLASS_DATETIME or InputType.TYPE_DATETIME_VARIATION_TIME).apply { setText("07:00") }
        row.addView(from, LinearLayout.LayoutParams(0, -2, 1f).apply { marginEnd = Ui.dpi(this@SetupActivity, 6) })
        row.addView(to, LinearLayout.LayoutParams(0, -2, 1f).apply { marginStart = Ui.dpi(this@SetupActivity, 6) })
        col.addView(row)
        val budget = Ui.input(this, "Bildschirmzeit pro Tag in Minuten (leer = unbegrenzt)", InputType.TYPE_CLASS_NUMBER)
        col.addView(budget)
        col.addView(Ui.button(this, "Speichern") {
            val a = pin1.text.toString(); val b = pin2.text.toString()
            val start = parseHm(from.text.toString()); val end = parseHm(to.text.toString())
            when {
                a.length < 4 -> showPinForm("Die PIN braucht mindestens 4 Zeichen.")
                a != b -> showPinForm("Die beiden PINs sind nicht gleich.")
                start == null || end == null -> showPinForm("Uhrzeiten bitte als HH:MM, z. B. 20:30.")
                else -> savePin(a, email.text.toString().trim(), start, end, budget.text.toString().toIntOrNull() ?: 0)
            }
        })
        if (compliance) col.addView(Ui.button(this, "Später einrichten", primary = false) { finishOk() })
    }

    private fun parseHm(s: String): Int? {
        val m = Regex("""^\s*(\d{1,2})[:.](\d{2})\s*$""").find(s) ?: return null
        val h = m.groupValues[1].toInt(); val mi = m.groupValues[2].toInt()
        return if (h in 0..23 && mi in 0..59) h * 60 + mi else null
    }

    private fun savePin(pin: String, email: String, start: Int, end: Int, budget: Int) {
        busy("Speichere …", "Die PIN wird auf dem Handy verschlüsselt – zum Server geht nur ein Prüfwert.")
        thread {
            val err = try {
                val p = Guard.pairing()!!
                val (hash, salt) = PinHasher.hash(pin)
                val draft = JSONObject()
                    .put("windows", JSONArray().put(JSONObject().put("id", "schlafenszeit").put("label", "Schlafenszeit")
                        .put("days", JSONArray()).put("startMinute", start).put("endMinute", end).put("enabled", true)))
                    .put("pinHash", hash).put("pinSalt", salt).put("pinIterations", PinHasher.DEFAULT_ITERATIONS)
                    .put("unlockGraceMinutes", 60).put("dailyBudgetMinutes", budget.coerceAtLeast(0))
                    .put("maxTrustedTimeStalenessMinutes", 4320).put("resetEmail", email)
                ServerApi(p.serverUrl, p.deviceId, p.token).setConfigDraft(draft)
                Sync.fetchConfig(p)
                if (Guard.enforcer.isConfigured) null else "Server hat die Einstellungen nicht bestätigt."
            } catch (e: Exception) { e.message ?: "Keine Verbindung" }
            ui.post { if (err == null) showDone() else showPinForm(err) }
        }
    }

    // ---------- Schritt 3: Fertig ----------

    @SuppressLint("BatteryLife")
    private fun showDone() {
        val owner = DeviceOwner.isDeviceOwner(this)
        onDoneStep = true
        header("Schritt 3 von 3", if (owner || WeakGuard.missing(this).isEmpty()) "Fertig – FrohLock passt auf 💛" else "Fast fertig – noch Erlaubnisse geben",
            "Sperrzeiten, Tageslimit, Auszeit und Fern-Entsperren steuert ihr ab jetzt im Admin " +
            "(frohlock.froehlichdienste.de/admin).")
        col.addView(Ui.card(this).apply {
            addView(Ui.text(this@SetupActivity, if (owner) "✓ Geräte-Verwaltung aktiv – FrohLock kann nicht gelöscht oder beendet werden."
                else "Einfacher Schutz (ohne Zurücksetzen): Der Sperrbildschirm liegt über allen Apps. Ein Kind, das sich auskennt, " +
                     "kann FrohLock aber in den Einstellungen abschalten – das seht ihr dann im Admin.",
                15f, if (owner) Ui.GOOD else 0xEEFFFFFF.toInt()))
            addView(Ui.text(this@SetupActivity, Guard.enforcer.todaySummary(), 15f))
        })
        if (!owner) addWeakModePermissions()
        val pm = getSystemService(PowerManager::class.java)
        if (!pm.isIgnoringBatteryOptimizations(packageName)) {
            col.addView(Ui.button(this, "Akku-Sparen für FrohLock ausschalten (empfohlen)", primary = false) {
                try { startActivity(Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:$packageName"))) } catch (_: Exception) { }
            })
        }
        col.addView(Ui.button(this, if (compliance) "Weiter mit der Handy-Einrichtung" else "Fertig") { finishOk() })
        GuardService.syncNow(this)
    }

    private var onDoneStep = false

    override fun onResume() {
        super.onResume(); isActive = true
        // Nach dem Erteilen einer Erlaubnis kommt man hierher zurück – Häkchen auffrischen.
        if (onDoneStep) showDone()
    }

    /** Einfacher Modus: die nötigen Erlaubnisse einzeln, mit Häkchen und Klartext. */
    private fun addWeakModePermissions() {
        col.addView(Ui.text(this, "Damit FrohLock sperren kann", 18f, bold = true))
        fun item(done: Boolean, title: String, why: String, action: () -> Unit) {
            val card = Ui.card(this)
            card.addView(Ui.text(this, (if (done) "✓ " else "○ ") + title, 17f, if (done) Ui.GOOD else Ui.ACCENT, bold = true))
            card.addView(Ui.text(this, why, 14f, 0xCCFFFFFF.toInt()))
            if (!done) card.addView(Ui.button(this, "Erlauben") { action() })
            col.addView(card)
        }
        item(WeakGuard.canOverlay(this), "Über anderen Apps einblenden",
            "Damit der Sperrbildschirm über Spielen und Videos liegt. In der Liste FrohLock antippen und einschalten.") {
            try { startActivity(Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:$packageName"))) } catch (_: Exception) { openAppInfo() }
        }
        item(WeakGuard.isAdminActive(this), "Geräteadministrator",
            "Damit FrohLock nicht einfach gelöscht werden kann. Auf „Aktivieren“ tippen.") {
            try {
                startActivity(Intent(DevicePolicyManager.ACTION_ADD_DEVICE_ADMIN)
                    .putExtra(DevicePolicyManager.EXTRA_DEVICE_ADMIN, DeviceOwner.admin(this))
                    .putExtra(DevicePolicyManager.EXTRA_ADD_EXPLANATION,
                        "FrohLock schützt die Bildschirmzeit. Als Geräteadministrator kann die App nicht einfach gelöscht werden."))
            } catch (_: Exception) { openAppInfo() }
        }
        item(WeakGuard.notificationsAllowed(this), "Benachrichtigungen",
            "Für die freundliche Erinnerung kurz vor der Schlafenszeit.") {
            if (android.os.Build.VERSION.SDK_INT >= 33) requestPermissions(arrayOf(android.Manifest.permission.POST_NOTIFICATIONS), 1)
            else openAppInfo()
        }
        col.addView(Ui.text(this, "Kommt die Meldung „Eingeschränkte Einstellung“? Dann: App-Info öffnen → oben rechts ⋮ → " +
            "„Eingeschränkte Einstellungen zulassen“ → zurück und noch einmal „Erlauben“.", 14f, 0xAAFFFFFF.toInt()))
        col.addView(Ui.button(this, "App-Info öffnen", primary = false) { openAppInfo() })
    }

    private fun openAppInfo() {
        try { startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:$packageName"))) } catch (_: Exception) { }
    }
    override fun onPause() { isActive = false; super.onPause() }

    private fun finishOk() {
        setResult(RESULT_OK)
        finish()
    }

    @Deprecated("Während der Handy-Einrichtung nicht abbrechen")
    override fun onBackPressed() {
        if (!compliance) @Suppress("DEPRECATION") super.onBackPressed()
    }

    companion object {
        const val EXTRA_REPAIR = "repair"
        /** Solange Eltern hier einrichten, holt der Dienst den Sperrbildschirm nicht nach vorn. */
        @Volatile var isActive = false; private set
    }
}

/** Android 10+: Welche Art Einrichtung? → Ganzes Gerät verwalten (Device Owner). */
class ProvisioningModeActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val result = Intent().putExtra(DevicePolicyManager.EXTRA_PROVISIONING_MODE,
            DevicePolicyManager.PROVISIONING_MODE_FULLY_MANAGED_DEVICE)
        setResult(RESULT_OK, result)
        finish()
    }
}
