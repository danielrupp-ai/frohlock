package de.froehlichdienste.frohlock.ui

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.View
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import de.froehlichdienste.frohlock.android.DeviceOwner
import de.froehlichdienste.frohlock.android.Guard
import de.froehlichdienste.frohlock.android.GuardService
import de.froehlichdienste.frohlock.core.PenaltyPolicy
import de.froehlichdienste.frohlock.core.UsageTimeline
import java.time.LocalTime

/** „FrohLock Übersicht" – was gilt heute, wie viel Zeit ist noch übrig (nur ansehen, nichts ändern). */
class MainActivity : Activity() {
    private val ui = Handler(Looper.getMainLooper())
    private lateinit var setupBanner: LinearLayout
    private lateinit var state: TextView
    private lateinit var usage: TextView
    private lateinit var timeline: TimelineView
    private lateinit var calc: TextView
    private lateinit var schedule: TextView
    private lateinit var left: TextView
    private lateinit var updateCard: LinearLayout
    private lateinit var updateMsg: TextView

    private val refresher = object : Runnable { override fun run() { refresh(); ui.postDelayed(this, 5000) } }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        Guard.init(this)
        GuardService.start(this)
        Ui.nightBackground(this)
        val scroll = ScrollView(this)
        val col = Ui.column(this, 22)
        col.addView(Ui.text(this, "FrohLock", 30f, bold = true))

        setupBanner = Ui.card(this).apply {
            addView(Ui.text(this@MainActivity, "Noch nicht fertig eingerichtet", 18f, Ui.WARN, bold = true))
            addView(Ui.text(this@MainActivity, "Ohne Eltern-PIN sperrt FrohLock noch nicht.", 15f))
            addView(Ui.button(this@MainActivity, "Einrichtung abschließen") {
                startActivity(Intent(this@MainActivity, SetupActivity::class.java))
            })
        }
        col.addView(setupBanner)

        val card = Ui.card(this)
        state = Ui.text(this, "", 22f, bold = true).also { card.addView(it) }
        usage = Ui.text(this, "", 17f).also { card.addView(it) }
        timeline = TimelineView(this).also { card.addView(it) }
        calc = Ui.text(this, "", 14f, 0xCCFFFFFF.toInt()).also { card.addView(it) }
        left = Ui.text(this, "", 16f, Ui.ACCENT).also { card.addView(it) }
        schedule = Ui.text(this, "", 15f, 0xDDFFFFFF.toInt()).also { card.addView(it) }
        col.addView(card)

        // Ohne Geräte-Verwaltung: einmal bestätigen, danach aktualisiert sich FrohLock von selbst.
        updateCard = Ui.card(this).apply {
            addView(Ui.text(this@MainActivity, "Automatische Updates einrichten", 18f, Ui.ACCENT, bold = true))
            addView(Ui.text(this@MainActivity, "Einmal tippen und „Aktualisieren“ bestätigen (fragt Android nach der Quelle: " +
                "FrohLock zulassen, zurück, nochmal tippen). Danach kommen alle Updates von selbst.", 14f, 0xCCFFFFFF.toInt()))
            updateMsg = Ui.text(this@MainActivity, "", 14f, Ui.WARN)
            addView(Ui.button(this@MainActivity, "⬆️  Jetzt einrichten") { b ->
                val pending = de.froehlichdienste.frohlock.android.Updater.pendingConfirm
                if (pending != null) { try { startActivity(pending); return@button } catch (_: Exception) { } }
                if (!packageManager.canRequestPackageInstalls()) {
                    updateMsg.text = "Zuerst FrohLock als Quelle zulassen – Schalter einschalten, dann zurück und nochmal tippen."
                    try { startActivity(Intent(android.provider.Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,
                        android.net.Uri.parse("package:$packageName"))) } catch (_: Exception) { }
                    return@button
                }
                b.isEnabled = false
                updateMsg.text = "Lade die aktuelle Version …"
                Thread {
                    val err = de.froehlichdienste.frohlock.android.Updater.setupAutoUpdate(this@MainActivity)
                    ui.post { b.isEnabled = true; updateMsg.text = err ?: "" }
                }.start()
            })
            addView(updateMsg)
        }
        col.addView(updateCard)
        col.addView(Ui.button(this, "🔑  Eltern-Bereich", primary = false) {
            startActivity(Intent(this, ParentActivity::class.java))
        })
        scroll.addView(col)
        Ui.fitSystemBars(scroll)
        setContentView(scroll)
    }

    override fun onResume() { super.onResume(); ui.post(refresher) }
    override fun onPause() { ui.removeCallbacks(refresher); super.onPause() }

    private fun refresh() {
        val e = Guard.enforcer
        setupBanner.visibility = if (e.isConfigured) View.GONE else View.VISIBLE
        updateCard.visibility = if (Guard.pairing()?.registered == true &&
            !de.froehlichdienste.frohlock.android.Updater.canUpdateSilently(this)) View.VISIBLE else View.GONE
        val d = e.decide()
        state.text = when {
            !e.isConfigured -> "Noch nicht eingerichtet"
            PenaltyPolicy.isPenaltyReason(d.reason) -> "⏳ Auszeit – noch ${PenaltyPolicy.formatCountdown(e.penaltySeconds())}"
            d.locked -> "🌙 Gesperrt"
            e.unlockUntilLabel() != null -> "🔓 Freigegeben bis ${e.unlockUntilLabel()} Uhr"
            else -> "✓ Frei"
        }
        state.setTextColor(if (!e.isConfigured) 0xDDFFFFFF.toInt() else if (d.locked) Ui.WARN else Ui.GOOD)
        val used = e.todayUsageMinutes()
        val budget = e.todayBudgetMinutes()
        usage.text = "Bildschirmzeit heute: ${UsageTimeline.fmtMinutes(used)}" + if (budget > 0) " von ${UsageTimeline.fmtMinutes(budget)}" else ""
        val segs = e.todaySegments()
        timeline.segments = segs
        timeline.nowMinute = LocalTime.now().let { it.hour * 60 + it.minute }
        calc.text = calcLine(segs, used)
        calc.visibility = if (calc.text.isNullOrEmpty()) View.GONE else View.VISIBLE
        val m = e.minutesUntilLock()
        left.text = if (m > 0) "Noch ${UsageTimeline.fmtMinutes(m)} bis zur nächsten Pause" else ""
        left.visibility = if (m > 0) View.VISIBLE else View.GONE
        val miss = if (!DeviceOwner.isDeviceOwner(this) && e.isConfigured) de.froehlichdienste.frohlock.android.WeakGuard.missing(this) else emptyList()
        schedule.text = e.todaySummary() + if (miss.isNotEmpty()) "\n⚠ Es fehlt noch: ${miss.joinToString(", ")} – Eltern-Bereich → „Erlaubnisse prüfen“" else ""
    }
}
