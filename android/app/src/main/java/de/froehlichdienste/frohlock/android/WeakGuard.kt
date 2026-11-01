package de.froehlichdienste.frohlock.android

import android.app.admin.DevicePolicyManager
import android.content.Context
import android.content.Intent
import android.graphics.PixelFormat
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.view.Gravity
import android.view.View
import android.view.WindowManager
import android.widget.FrameLayout
import android.widget.LinearLayout
import de.froehlichdienste.frohlock.ui.LockActivity
import de.froehlichdienste.frohlock.ui.StarSkyView
import de.froehlichdienste.frohlock.ui.Ui

/**
 * Schutz OHNE Geräte-Verwaltung (installiert über den Link, Handy nicht zurückgesetzt):
 * Ein Vollbild-Overlay legt sich über jede App, sobald gesperrt ist und der Sperrbildschirm nicht
 * vorne liegt (Home-Taste, App-Wechsel). Dank „Über anderen Apps einblenden" darf FrohLock den
 * Sperrbildschirm auch aus dem Hintergrund wieder nach vorn holen. Der Geräteadministrator
 * verhindert das einfache Deinstallieren; wird er abgeschaltet, meldet FrohLock das dem Admin.
 */
object WeakGuard {
    private val main = Handler(Looper.getMainLooper())
    @Volatile private var overlay: View? = null
    /** Liegt das Overlay gerade sichtbar über allem? */
    val isShowing: Boolean get() = overlay?.isAttachedToWindow == true

    fun canOverlay(ctx: Context) = Settings.canDrawOverlays(ctx)

    fun isAdminActive(ctx: Context) = try {
        ctx.getSystemService(DevicePolicyManager::class.java).isAdminActive(DeviceOwner.admin(ctx))
    } catch (_: Exception) { false }

    fun notificationsAllowed(ctx: Context) =
        ctx.getSystemService(android.app.NotificationManager::class.java).areNotificationsEnabled()

    /** Was im einfachen Modus noch fehlt (für Einrichtung, Übersicht und Admin). */
    fun missing(ctx: Context): List<String> = buildList {
        if (!canOverlay(ctx)) add("Über anderen Apps einblenden")
        if (!isAdminActive(ctx)) add("Geräteadministrator")
        if (!ForegroundApp.hasAccess(ctx)) add("Nutzungsdaten (welche App offen ist)")
        if (!notificationsAllowed(ctx)) add("Benachrichtigungen")
    }

    /** Overlay zeigen/entfernen. Wird nur im einfachen Modus benutzt. */
    fun setOverlay(ctx: Context, show: Boolean) {
        val app = ctx.applicationContext
        main.post {
            val wm = app.getSystemService(WindowManager::class.java)
            if (show && overlay == null && canOverlay(app)) {
                val v = buildView(app)
                val lp = WindowManager.LayoutParams(
                    WindowManager.LayoutParams.MATCH_PARENT, WindowManager.LayoutParams.MATCH_PARENT,
                    WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
                    WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN or WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE,
                    PixelFormat.OPAQUE)
                try { wm.addView(v, lp); overlay = v } catch (e: Exception) {
                    Guard.audit.write("UMGEHUNG", "⚠ Sperr-Overlay ließ sich nicht zeigen: ${e.javaClass.simpleName}")
                }
            } else if (!show && overlay != null) {
                try { wm.removeView(overlay) } catch (_: Exception) { }
                overlay = null
            }
        }
    }

    private fun buildView(ctx: Context): View {
        val root = FrameLayout(ctx).apply { setBackgroundColor(Ui.NIGHT_TOP) }
        root.addView(StarSkyView(ctx), FrameLayout.LayoutParams(-1, -1))
        val col = Ui.column(ctx, 32).apply { gravity = Gravity.CENTER }
        col.addView(Ui.text(ctx, "🌙", 64f, center = true))
        col.addView(Ui.text(ctx, "Gerade ist Pause", 26f, bold = true, center = true))
        col.addView(Ui.text(ctx, "Tippe hier, um den FrohLock-Bildschirm zu öffnen.", 17f, 0xDDFFFFFF.toInt(), center = true))
        col.addView(Ui.button(ctx, "FrohLock öffnen") { openLock(ctx) })
        root.addView(col, FrameLayout.LayoutParams(-1, LinearLayout.LayoutParams.WRAP_CONTENT, Gravity.CENTER))
        root.setOnClickListener { openLock(ctx) }
        return root
    }

    fun openLock(ctx: Context) {
        try {
            ctx.startActivity(Intent(ctx, LockActivity::class.java)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_REORDER_TO_FRONT or Intent.FLAG_ACTIVITY_NO_ANIMATION))
        } catch (_: Exception) { }
    }
}
