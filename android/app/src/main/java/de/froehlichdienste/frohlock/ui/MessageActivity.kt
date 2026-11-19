package de.froehlichdienste.frohlock.ui

import android.app.Activity
import android.graphics.Color
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.view.Gravity
import android.view.View
import android.view.WindowManager
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.VideoView
import android.window.OnBackInvokedDispatcher
import de.froehlichdienste.frohlock.android.Guard
import de.froehlichdienste.frohlock.android.GuardService
import de.froehlichdienste.frohlock.android.MessageMedia
import java.io.File

/**
 * Nachricht der Eltern ans Kind (Geburtstagsgruß 20.11.2026). Vollbild über allem – auch während einer Sperre.
 * Mit Video: erst das Video (mit Ton), danach Text + „Nochmal" + Danke-Knopf; beim ersten Mal in die Galerie.
 * EXTRA_REPLAY = gespeichertes Video nochmal ansehen (aus der FrohLock-Übersicht).
 */
class MessageActivity : Activity() {
    private var messageId = ""
    private lateinit var video: VideoView
    private lateinit var after: View

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        Guard.init(this)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        val replay = intent.getStringExtra(EXTRA_REPLAY)?.let { File(it) }?.takeIf { it.exists() }
        val m = Guard.enforcer.pendingMessage
        if (replay == null && m == null) { finish(); return }
        messageId = m?.id ?: ""
        if (Build.VERSION.SDK_INT >= 33 && replay == null) {
            onBackInvokedDispatcher.registerOnBackInvokedCallback(OnBackInvokedDispatcher.PRIORITY_DEFAULT) { /* erst lesen */ }
        }
        val file = replay ?: m?.takeIf { MessageMedia.hasVideo(this, it) }?.let { MessageMedia.videoFile(this, it) }

        val root = FrameLayout(this).apply { setBackgroundColor(Color.BLACK) }
        // Text-Teil (nach dem Video bzw. ohne Video sofort)
        val textRoot = FrameLayout(this)
        Ui.nightBackground(this)
        textRoot.setBackgroundColor(Ui.NIGHT_TOP)
        textRoot.addView(StarSkyView(this), FrameLayout.LayoutParams(-1, -1))
        val scroll = ScrollView(this).apply { isFillViewport = true }
        val col = Ui.column(this, 28).apply { gravity = Gravity.CENTER_HORIZONTAL }
        col.addView(View(this), LinearLayout.LayoutParams(1, Ui.dpi(this, 40)))
        col.addView(Ui.text(this, "🎂🎁🎉", 56f, center = true))
        if (m != null) {
            if (m.title.isNotBlank()) col.addView(Ui.text(this, m.title, 28f, Ui.ACCENT, bold = true, center = true))
            val card = Ui.card(this)
            card.addView(Ui.text(this, m.text, 19f, 0xF2FFFFFF.toInt(), center = true))
            col.addView(card)
        }
        col.addView(View(this), LinearLayout.LayoutParams(1, 0, 1f))
        if (file != null) col.addView(Ui.button(this, "▶  Ще раз", primary = false) { playAgain() })
        col.addView(Ui.button(this, if (m != null && replay == null) m.button else "Закрити") {
            if (replay == null) {
                Guard.enforcer.messageRead(messageId)
                GuardService.poke(this)
            }
            finish()
        })
        scroll.addView(col)
        textRoot.addView(scroll, FrameLayout.LayoutParams(-1, -1))
        Ui.fitSystemBars(scroll)
        after = textRoot

        video = VideoView(this)
        root.addView(video, FrameLayout.LayoutParams(-1, -1, Gravity.CENTER))
        root.addView(textRoot, FrameLayout.LayoutParams(-1, -1))
        setContentView(root)

        if (file != null) {
            textRoot.visibility = View.GONE
            video.setVideoURI(Uri.fromFile(file))
            video.setOnCompletionListener { showAfter() }
            video.setOnErrorListener { _, _, _ -> showAfter(); true }
            video.setOnPreparedListener { it.isLooping = false; video.start() }
            // Beim ersten Ansehen auch in der Galerie ablegen (Filme/FrohLock) – zum späteren Nochmal-Ansehen.
            if (m != null && replay == null) {
                val marker = File(file.path + ".gallery")
                if (!marker.exists()) Thread {
                    if (MessageMedia.saveToGallery(this, m)) marker.writeText("ok")
                }.start()
            }
        } else {
            video.visibility = View.GONE
        }
    }

    private fun showAfter() {
        after.visibility = View.VISIBLE
        video.visibility = View.INVISIBLE
    }

    private fun playAgain() {
        after.visibility = View.GONE
        video.visibility = View.VISIBLE
        video.seekTo(0)
        video.start()
    }

    @Deprecated("Zurück erst nach dem Lesen")
    override fun onBackPressed() {
        if (intent.getStringExtra(EXTRA_REPLAY) != null) finish()
    }

    override fun onResume() { super.onResume(); isResumed = true }
    override fun onPause() { isResumed = false; super.onPause() }

    companion object {
        const val EXTRA_REPLAY = "replay"
        @Volatile var isResumed = false; private set
    }
}
