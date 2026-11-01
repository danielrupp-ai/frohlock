package de.froehlichdienste.frohlock.ui

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.widget.LinearLayout
import android.widget.ScrollView
import de.froehlichdienste.frohlock.android.DeviceOwner
import de.froehlichdienste.frohlock.android.Guard
import de.froehlichdienste.frohlock.android.GuardService
import de.froehlichdienste.frohlock.android.Sync
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * Eltern-Bereich – nur mit Eltern- oder Master-PIN. Vor der Einrichtung (noch keine PIN) offen,
 * genau wie unter Windows: ohne PIN wird nichts gesperrt, also gibt es auch nichts zu schützen.
 */
class ParentActivity : Activity() {
    private lateinit var col: LinearLayout
    private var unlocked = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        Guard.init(this)
        Ui.nightBackground(this)
        val scroll = ScrollView(this)
        col = Ui.column(this, 22)
        scroll.addView(col)
        Ui.fitSystemBars(scroll)
        setContentView(scroll)
        if (Guard.enforcer.isConfigured) showGate(null) else { unlocked = true; showMenu() }
    }

    override fun onPause() {
        super.onPause()
        // Wer den Bereich verlässt, muss beim nächsten Mal wieder die PIN eingeben.
        if (Guard.enforcer.isConfigured && !isChangingConfigurations) finish()
    }

    private fun showGate(error: String?) {
        col.removeAllViews()
        col.addView(Ui.text(this, "Eltern-Bereich", 28f, bold = true))
        col.addView(Ui.text(this, "Nur für Mama und Papa. 💛", 16f, 0xDDFFFFFF.toInt()))
        if (error != null) col.addView(Ui.text(this, error, 15f, Ui.BAD))
        val pin = Ui.pinInput(this)
        col.addView(pin)
        col.addView(Ui.keyboardToggle(this, pin))
        col.addView(Ui.button(this, "Öffnen") {
            val entered = pin.text.toString()
            it.isEnabled = false
            Thread {
                val r = Guard.enforcer.checkPin(entered)   // PBKDF2 – nicht im Bedien-Thread
                runOnUiThread {
                    if (r.ok) { unlocked = true; showMenu() }
                    else showGate(if (r.remaining == 0) "Zu viele Fehlversuche heute – nur noch die Master-PIN hilft."
                                  else "Falsche PIN. Noch ${r.remaining} Versuche heute.")
                }
            }.start()
        })
    }

    private fun showMenu() {
        if (!unlocked) return
        col.removeAllViews()
        val e = Guard.enforcer
        val p = Guard.pairing()
        val owner = DeviceOwner.isDeviceOwner(this)
        col.addView(Ui.text(this, "Eltern-Bereich", 28f, bold = true))

        col.addView(Ui.card(this).apply {
            fun line(s: String, c: Int = 0xEEFFFFFF.toInt()) = addView(Ui.text(this@ParentActivity, s, 15f, c))
            line(if (owner) "✓ Geräte-Verwaltung aktiv (nicht löschbar, nicht beendbar)" else "⚠ Geräte-Verwaltung fehlt – Sperre leicht zu umgehen",
                if (owner) Ui.GOOD else Ui.WARN)
            line(if (p?.registered == true) "Gekoppelt als „${p.deviceName}“" else "⚠ Nicht mit dem Admin gekoppelt",
                if (p?.registered == true) 0xEEFFFFFF.toInt() else Ui.WARN)
            val contact = Sync.lastContactMs
            line("Letzter Kontakt zum Admin: " + if (contact > 0) SimpleDateFormat("HH:mm:ss", Locale.GERMANY).format(Date(contact)) else "noch keiner")
            line("Version ${Guard.appVersion} · Einstellungen v${e.currentConfig?.configVersion ?: 0}")
            e.unlockUntilLabel()?.let { line("🔓 Freigegeben bis $it Uhr", Ui.ACCENT) }
            val relaxed = Guard.state.relaxedUntilUnix
            if (relaxed > System.currentTimeMillis() / 1000) line("🔧 Einstellungen offen bis ${e.localTime(relaxed)} Uhr", Ui.ACCENT)
        })

        if (e.isConfigured) {
            col.addView(Ui.text(this, "Jetzt freigeben", 18f, bold = true))
            val row = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
            for ((label, mins) in listOf("30 Min" to 30, "1 Std" to 60, "2 Std" to 120)) {
                row.addView(Ui.button(this, label) {
                    e.remoteUnlock(mins, "Eltern-PIN am Gerät"); GuardService.poke(this); showMenu()
                }.apply { layoutParams = LinearLayout.LayoutParams(0, -2, 1f).apply { marginStart = 6; marginEnd = 6 } })
            }
            col.addView(row)
            if (e.unlockUntilLabel() != null) {
                col.addView(Ui.button(this, "Freigabe beenden – Zeitplan gilt wieder", primary = false) {
                    e.endUnlock("Eltern am Gerät"); GuardService.poke(this); showMenu()
                })
            }
        }

        col.addView(Ui.button(this, "🔧  Einstellungen 15 Min freigeben", primary = false) {
            Guard.state.relaxedUntilUnix = System.currentTimeMillis() / 1000 + 15 * 60
            Guard.saveState()
            DeviceOwner.applyBaseline(this, relaxed = true)
            Guard.audit.write("POLICY", "Eltern haben Uhrzeit/Entwickleroptionen/App-Installation/Zurücksetzen 15 Min freigegeben")
            showMenu()
        })
        col.addView(Ui.text(this, "Erlaubt kurz: Uhrzeit ändern, Apps aus Dateien installieren, Entwickleroptionen, " +
            "abgesicherter Modus und Zurücksetzen. Danach sperrt FrohLock das von selbst wieder.", 13f, 0xAAFFFFFF.toInt()))

        col.addView(Ui.button(this, "🔄  Jetzt mit dem Admin abgleichen", primary = false) {
            GuardService.syncNow(this); showMenu()
        })
        col.addView(Ui.button(this, "🔗  Neu koppeln / Einrichtung", primary = false) {
            startActivity(Intent(this, SetupActivity::class.java).putExtra(SetupActivity.EXTRA_REPAIR, true))
        })
        col.addView(Ui.button(this, "📜  Geräte-Protokoll", primary = false) { showLog() })
        col.addView(Ui.button(this, "🗑  FrohLock von diesem Gerät entfernen", primary = false) { confirmRemove() })
    }

    private fun showLog() {
        val lines = Guard.audit.tail(60).reversed().joinToString("\n") { l ->
            val parts = l.split('\t')
            if (parts.size >= 3) "${parts[0].take(19).replace('T', ' ')}  ${parts[1]}  ${parts[2]}" else l
        }
        AlertDialog.Builder(this).setTitle("Geräte-Protokoll").setMessage(lines.ifBlank { "Noch leer." })
            .setPositiveButton("Schließen", null).show()
    }

    private fun confirmRemove() {
        AlertDialog.Builder(this)
            .setTitle("FrohLock entfernen?")
            .setMessage("Alle Sperren werden aufgehoben und FrohLock gibt die Geräte-Verwaltung ab. " +
                "Danach kann die App normal gelöscht werden. Im Admin bleibt das Gerät stehen, bis ihr es dort löscht.")
            .setPositiveButton("Ja, entfernen") { _, _ ->
                GuardService.releaseDevice(this, "Eltern-PIN am Gerät")
                try { startActivity(Intent(Intent.ACTION_DELETE, Uri.parse("package:$packageName"))) } catch (_: Exception) { }
                finish()
            }
            .setNegativeButton("Nein, behalten", null)
            .show()
    }
}
