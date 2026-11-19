package de.froehlichdienste.frohlock.ui

import android.app.Activity
import android.os.Build
import android.os.Bundle
import android.view.Gravity
import android.view.View
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.ScrollView
import android.window.OnBackInvokedDispatcher
import de.froehlichdienste.frohlock.android.Guard
import de.froehlichdienste.frohlock.android.GuardService

/**
 * Nachricht der Eltern ans Kind (z. B. Geburtstagsgruß, 19.11.2026). Vollbild über allem – auch während einer
 * Sperre –, bis das Kind den Knopf tippt. Danach geht es normal weiter (ggf. zurück zum Sperrbildschirm).
 */
class MessageActivity : Activity() {
    private var messageId = ""

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        Guard.init(this)
        val m = Guard.enforcer.pendingMessage
        if (m == null) { finish(); return }
        messageId = m.id
        Ui.nightBackground(this)
        if (Build.VERSION.SDK_INT >= 33) {
            onBackInvokedDispatcher.registerOnBackInvokedCallback(OnBackInvokedDispatcher.PRIORITY_DEFAULT) { /* erst lesen */ }
        }
        val root = FrameLayout(this)
        root.addView(StarSkyView(this), FrameLayout.LayoutParams(-1, -1))
        val scroll = ScrollView(this).apply { isFillViewport = true }
        val col = Ui.column(this, 28).apply { gravity = Gravity.CENTER_HORIZONTAL }
        col.addView(View(this), LinearLayout.LayoutParams(1, Ui.dpi(this, 48)))
        col.addView(Ui.text(this, "🎂🎁🎉", 60f, center = true))
        if (m.title.isNotBlank()) col.addView(Ui.text(this, m.title, 28f, Ui.ACCENT, bold = true, center = true))
        val card = Ui.card(this)
        card.addView(Ui.text(this, m.text, 19f, 0xF2FFFFFF.toInt(), center = true))
        col.addView(card)
        col.addView(View(this), LinearLayout.LayoutParams(1, 0, 1f))
        col.addView(Ui.button(this, m.button) {
            Guard.enforcer.messageRead(messageId)
            GuardService.poke(this)
            finish()
        })
        scroll.addView(col)
        root.addView(scroll, FrameLayout.LayoutParams(-1, -1))
        Ui.fitSystemBars(scroll)
        setContentView(root)
    }

    @Deprecated("Zurück erst nach dem Lesen")
    override fun onBackPressed() { /* erst lesen, dann „Danke" */ }

    override fun onResume() { super.onResume(); isResumed = true }
    override fun onPause() { isResumed = false; super.onPause() }

    companion object {
        @Volatile var isResumed = false; private set
    }
}
