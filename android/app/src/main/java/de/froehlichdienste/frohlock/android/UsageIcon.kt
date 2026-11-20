package de.froehlichdienste.frohlock.android

import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Typeface
import android.graphics.drawable.Icon

/**
 * Statusleisten-Symbol mit den Restminuten als Zahl (z. B. „75“), damit das Kind oben am Handy
 * jederzeit sieht, wie viel Zeit noch übrig ist. Android färbt Statusleisten-Symbole selbst ein – nur Weiß + Alpha zählt.
 */
object UsageIcon {
    private val cache = HashMap<Int, Icon>()

    fun number(minutes: Int): Icon? = try {
        val m = minutes.coerceIn(0, 999)
        synchronized(cache) { cache.getOrPut(m) { draw(m) } }
    } catch (_: Exception) { null }

    private fun draw(m: Int): Icon {
        val size = 96
        val bmp = Bitmap.createBitmap(size, size, Bitmap.Config.ARGB_8888)
        val c = Canvas(bmp)
        val p = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            color = Color.WHITE
            typeface = Typeface.create("sans-serif-condensed", Typeface.BOLD)
            textAlign = Paint.Align.CENTER
        }
        val text = m.toString()
        p.textSize = when (text.length) { 1 -> 90f; 2 -> 80f; else -> 62f }
        // auf die Breite einpassen
        val w = p.measureText(text)
        if (w > size - 2) p.textSize *= (size - 2) / w
        val fm = p.fontMetrics
        val y = size / 2f - (fm.ascent + fm.descent) / 2f
        c.drawText(text, size / 2f, y, p)
        return Icon.createWithBitmap(bmp)
    }
}
