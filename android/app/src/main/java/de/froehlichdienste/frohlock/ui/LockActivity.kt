package de.froehlichdienste.frohlock.ui

import android.app.Activity
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.view.Gravity
import android.view.View
import android.view.WindowInsets
import android.view.WindowInsetsController
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.window.OnBackInvokedDispatcher
import de.froehlichdienste.frohlock.android.DeviceOwner
import de.froehlichdienste.frohlock.android.Guard
import de.froehlichdienste.frohlock.android.GuardService
import de.froehlichdienste.frohlock.core.PenaltyPolicy
import de.froehlichdienste.frohlock.core.UsageTimeline
import java.lang.ref.WeakReference
import java.time.LocalTime

/**
 * Sperrbildschirm. Als Geräte-Verwalter läuft er im Sperr-Modus (Lock Task): Home, Übersicht und
 * Benachrichtigungen sind aus, nur Telefon/Wecker bleiben erreichbar. Aufheben nur per Eltern-PIN,
 * Fern-Entsperren, Master-PIN – oder wenn die Sperrzeit von selbst endet.
 */
class LockActivity : Activity() {
    private val ui = Handler(Looper.getMainLooper())
    private lateinit var emoji: TextView
    private lateinit var title: TextView
    private lateinit var body: TextView
    private lateinit var big: TextView
    private lateinit var timeline: TimelineView
    private lateinit var calc: TextView
    private lateinit var sweet: TextView
    private lateinit var pinPanel: LinearLayout
    private lateinit var pinMsg: TextView
    private var sweetIdx = 0
    private var leaving = false

    private val refresher = object : Runnable {
        override fun run() { refresh(); ui.postDelayed(this, 1000) }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        Guard.init(this)
        instances.add(WeakReference(this))
        Ui.nightBackground(this)
        if (Build.VERSION.SDK_INT >= 33) {
            onBackInvokedDispatcher.registerOnBackInvokedCallback(OnBackInvokedDispatcher.PRIORITY_DEFAULT) { /* Zurück = nichts */ }
        }

        val root = FrameLayout(this)
        root.addView(StarSkyView(this), FrameLayout.LayoutParams(-1, -1))
        val scroll = ScrollView(this).apply { isFillViewport = true }
        val col = Ui.column(this, 28).apply { gravity = Gravity.CENTER_HORIZONTAL }
        col.addView(View(this), LinearLayout.LayoutParams(1, Ui.dpi(this, 80)))
        emoji = Ui.text(this, "🌙", 64f, center = true).also { col.addView(it) }
        title = Ui.text(this, "", 28f, bold = true, center = true).also { col.addView(it) }
        body = Ui.text(this, "", 18f, 0xE6FFFFFF.toInt(), center = true).also { col.addView(it) }
        big = Ui.text(this, "", 54f, Ui.ACCENT, bold = true, center = true).also { col.addView(it) }
        timeline = TimelineView(this).also { col.addView(it) }
        calc = Ui.text(this, "", 15f, 0xCCFFFFFF.toInt(), center = true).also { col.addView(it) }
        sweet = Ui.text(this, "", 16f, 0xB3FFFFFF.toInt(), center = true).also {
            (it.layoutParams as LinearLayout.LayoutParams).topMargin = Ui.dpi(this, 18); col.addView(it)
        }

        val spacer = View(this)
        col.addView(spacer, LinearLayout.LayoutParams(1, 0, 1f))

        pinPanel = Ui.card(this).apply { visibility = View.GONE }
        val pin = Ui.pinInput(this)
        pinMsg = Ui.text(this, "Wie lange soll ${Guard.deviceWord()} frei sein?", 15f, 0xDDFFFFFF.toInt())
        pinPanel.addView(Ui.text(this, "Für Mama und Papa", 18f, bold = true))
        pinPanel.addView(pin)
        pinPanel.addView(Ui.keyboardToggle(this, pin))
        pinPanel.addView(pinMsg)
        val row = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        for ((label, mins) in listOf("30 Min" to 30, "1 Std" to 60, "2 Std" to 120)) {
            row.addView(Ui.button(this, label) { submit(pin.text.toString(), mins); pin.setText("") }.apply {
                layoutParams = LinearLayout.LayoutParams(0, -2, 1f).apply { marginStart = Ui.dpi(context, 3); marginEnd = Ui.dpi(context, 3) }
            })
        }
        pinPanel.addView(row)
        col.addView(pinPanel)

        col.addView(Ui.button(this, "🔑  Eltern-PIN", primary = false) {
            pinPanel.visibility = if (pinPanel.visibility == View.VISIBLE) View.GONE else View.VISIBLE
        })
        if (DeviceOwner.hasTelephony(this)) {
            col.addView(Ui.button(this, "📞  Anrufen (Notfall, Mama, Papa)", primary = false) { openDialer() })
        }
        scroll.addView(col)
        root.addView(scroll, FrameLayout.LayoutParams(-1, -1))
        setContentView(root)
    }

    private fun submit(pin: String, minutes: Int) {
        if (pin.isBlank()) { pinMsg.text = "Bitte zuerst die PIN eingeben."; return }
        val r = Guard.enforcer.submitPin(pin, minutes)
        if (r.ok) {
            pinMsg.text = "Entsperrt ✓"
            GuardService.poke(this)
            refresh()
        } else {
            pinMsg.text = if (r.remaining == 0) "Zu viele falsche Versuche heute – bitte die Master-PIN oder das Admin-Web nutzen."
                          else "Das war nicht die richtige PIN. Noch ${r.remaining} Versuche heute."
            pinMsg.setTextColor(Ui.BAD)
        }
    }

    private fun openDialer() {
        callGraceUntilUptime = SystemClock.uptimeMillis() + 10 * 60_000
        try { startActivity(Intent(Intent.ACTION_DIAL, Uri.parse("tel:")).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) } catch (_: Exception) { }
    }

    override fun onResume() {
        super.onResume()
        isResumed = true
        lastVisibleUptime = SystemClock.uptimeMillis()
        callGraceUntilUptime = 0
        hideSystemBars()
        if (Guard.enforcer.decide().locked && DeviceOwner.isDeviceOwner(this) && !DeviceOwner.lockTaskActive(this)) {
            try { startLockTask() } catch (e: Exception) { Guard.audit.write("UMGEHUNG", "⚠ Sperr-Modus ließ sich nicht starten: ${e.javaClass.simpleName}") }
        }
        ui.post(refresher)
    }

    override fun onPause() {
        isResumed = false
        lastVisibleUptime = SystemClock.uptimeMillis()
        ui.removeCallbacks(refresher)
        super.onPause()
    }

    override fun onDestroy() {
        instances.removeAll { it.get() == null || it.get() === this }
        super.onDestroy()
    }

    @Deprecated("Zurück bleibt auf dem Sperrbildschirm")
    override fun onBackPressed() { /* nichts */ }

    private fun hideSystemBars() {
        if (Build.VERSION.SDK_INT >= 30) {
            window.insetsController?.let {
                it.hide(WindowInsets.Type.navigationBars())
                it.systemBarsBehavior = WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            }
        } else {
            @Suppress("DEPRECATION")
            window.decorView.systemUiVisibility = View.SYSTEM_UI_FLAG_HIDE_NAVIGATION or View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
        }
    }

    private fun refresh() {
        if (isResumed) lastVisibleUptime = SystemClock.uptimeMillis()
        val e = Guard.enforcer
        val d = e.decide()
        if (!d.locked) { leave(); return }
        val word = Guard.deviceWord()
        val used = e.todayUsageMinutes()
        val segs = e.todaySegments()
        big.visibility = View.GONE; timeline.visibility = View.GONE; calc.visibility = View.GONE
        when {
            PenaltyPolicy.isPenaltyReason(d.reason) -> {
                emoji.text = "⏳"
                title.text = "Kurze Auszeit"
                val total = e.penaltyTotalMinutes()
                body.text = "Mama oder Papa waren gerade beschäftigt, und es war zum Glück kein Notfall … " +
                    "Darum macht ${word} jetzt ${if (total > 0) "$total" else "15"} Minuten Pause. 💛"
                big.text = PenaltyPolicy.formatCountdown(e.penaltySeconds()); big.visibility = View.VISIBLE
            }
            d.reason.startsWith("Sperrfenster") -> {
                emoji.text = "🌙"
                title.text = "Schlafenszeit"
                val end = e.lockWindowEndLabel()
                body.text = "Jetzt ruht sich ${word} aus – und du auch. " +
                    (if (end != null) "Ab $end Uhr geht's weiter. " else "") + "Schlaf schön! ✨"
            }
            d.reason.startsWith("Tageslimit") -> {
                emoji.text = "🌈"
                title.text = "Für heute ist die Bildschirmzeit um"
                body.text = "Du hattest heute ${UsageTimeline.fmtMinutes(used)}. Morgen gibt es neue Zeit. 💛"
                timeline.segments = segs
                timeline.nowMinute = LocalTime.now().let { it.hour * 60 + it.minute }
                timeline.visibility = View.VISIBLE
                calc.text = calcLine(segs, used); calc.visibility = if (calc.text.isNotEmpty()) View.VISIBLE else View.GONE
            }
            else -> {
                emoji.text = "💛"
                title.text = "Kleine Pause"
                body.text = d.reason
            }
        }
        if (SystemClock.uptimeMillis() / 8000 % SWEET.size != sweetIdx.toLong()) {
            sweetIdx = (SystemClock.uptimeMillis() / 8000 % SWEET.size).toInt()
            sweet.text = SWEET[sweetIdx]
        }
    }

    private fun leave() {
        if (leaving) return
        leaving = true
        try { if (DeviceOwner.lockTaskActive(this)) stopLockTask() } catch (_: Exception) { }
        finish()
    }

    companion object {
        @Volatile var isResumed = false; private set
        @Volatile var lastVisibleUptime = 0L; private set
        /** Nach „Anrufen" darf die Telefon-App 10 Min vorne bleiben. */
        @Volatile var callGraceUntilUptime = 0L
        private val instances = mutableListOf<WeakReference<LockActivity>>()

        fun finishAll() {
            Handler(Looper.getMainLooper()).post { instances.mapNotNull { it.get() }.forEach { it.leave() } }
        }

        private val SWEET = listOf(
            "Die Sterne passen heute auf dich auf. ⭐",
            "Der Mond sagt: Gute Nacht, kleiner Schatz. 🌙",
            "Morgen wartet ein neuer, schöner Tag auf dich. ☀️",
            "Auch Handys und Tablets brauchen mal eine Pause. 😴",
            "Mama und Papa haben dich ganz doll lieb. 💛",
            "Lies doch ein Buch oder spiel etwas Schönes. 📚",
        )
    }
}
