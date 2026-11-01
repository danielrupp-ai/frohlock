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
    private lateinit var updateButton: android.widget.Button

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

        updateButton = Ui.button(this, "⬆️  Neue FrohLock-Version installieren") {
            de.froehlichdienste.frohlock.android.Updater.pendingConfirm?.let { i -> try { startActivity(i) } catch (_: Exception) { } }
        }
        col.addView(updateButton)
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
        updateButton.visibility = if (de.froehlichdienste.frohlock.android.Updater.pendingConfirm != null) View.VISIBLE else View.GONE
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
