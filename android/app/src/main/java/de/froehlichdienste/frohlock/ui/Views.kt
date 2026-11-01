package de.froehlichdienste.frohlock.ui

import android.animation.ValueAnimator
import android.app.Activity
import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.RectF
import android.graphics.Shader
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.text.InputType
import android.util.TypedValue
import android.view.Gravity
import android.view.View
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.TextView
import de.froehlichdienste.frohlock.core.UsageSegmentView
import de.froehlichdienste.frohlock.core.UsageTimeline
import kotlin.math.sin
import kotlin.random.Random

/** Kleine Bausteine, damit alle Bildschirme gleich aussehen (ohne Fremdbibliotheken). */
object Ui {
    const val NIGHT_TOP = 0xFF0B1033.toInt()
    const val NIGHT_BOTTOM = 0xFF2A1B5C.toInt()
    const val CARD = 0x26FFFFFF
    const val ACCENT = 0xFFFFD54F.toInt()
    const val GOOD = 0xFF69F0AE.toInt()
    const val WARN = 0xFFFFAB40.toInt()
    const val BAD = 0xFFFF8A80.toInt()
    val BLOCK_COLORS = intArrayOf(0xFFFFB74D.toInt(), 0xFF4DD0E1.toInt(), 0xFFF06292.toInt(),
        0xFF9CCC65.toInt(), 0xFFBA68C8.toInt(), 0xFFFFD54F.toInt())

    fun dp(ctx: Context, v: Float) = TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, v, ctx.resources.displayMetrics)
    fun dpi(ctx: Context, v: Int) = dp(ctx, v.toFloat()).toInt()

    fun column(ctx: Context, padDp: Int = 20) = LinearLayout(ctx).apply {
        orientation = LinearLayout.VERTICAL
        val p = dpi(ctx, padDp); setPadding(p, p, p, p)
    }

    fun text(ctx: Context, s: CharSequence, sizeSp: Float = 16f, color: Int = Color.WHITE, bold: Boolean = false,
             center: Boolean = false) = TextView(ctx).apply {
        text = s; textSize = sizeSp; setTextColor(color)
        if (bold) typeface = Typeface.DEFAULT_BOLD
        if (center) gravity = Gravity.CENTER_HORIZONTAL
        setLineSpacing(0f, 1.15f)
        layoutParams = LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT)
            .apply { bottomMargin = dpi(ctx, 8) }
    }

    fun card(ctx: Context) = column(ctx, 16).apply {
        background = GradientDrawable().apply { cornerRadius = dp(ctx, 18f); setColor(CARD) }
        layoutParams = LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT)
            .apply { bottomMargin = dpi(ctx, 14) }
    }

    fun button(ctx: Context, label: String, primary: Boolean = true, onClick: () -> Unit) = Button(ctx).apply {
        text = label; isAllCaps = false; textSize = 17f
        setTextColor(if (primary) 0xFF1A1440.toInt() else Color.WHITE)
        background = GradientDrawable().apply {
            cornerRadius = dp(ctx, 28f)
            setColor(if (primary) ACCENT else 0x33FFFFFF)
        }
        val h = dpi(ctx, 14); setPadding(h, h, h, h)
        layoutParams = LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT)
            .apply { topMargin = dpi(ctx, 6); bottomMargin = dpi(ctx, 6) }
        setOnClickListener { onClick() }
    }

    fun input(ctx: Context, hint: String, type: Int = InputType.TYPE_CLASS_TEXT) = EditText(ctx).apply {
        this.hint = hint; inputType = type; textSize = 18f
        setTextColor(Color.WHITE); setHintTextColor(0x99FFFFFF.toInt())
        background = GradientDrawable().apply { cornerRadius = dp(ctx, 12f); setColor(0x22FFFFFF) }
        val p = dpi(ctx, 14); setPadding(p, p, p, p)
        layoutParams = LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT)
            .apply { bottomMargin = dpi(ctx, 10) }
    }

    fun pinInput(ctx: Context, hint: String = "Eltern-PIN") =
        input(ctx, hint, InputType.TYPE_CLASS_NUMBER or InputType.TYPE_NUMBER_VARIATION_PASSWORD)

    /** Ziffern ↔ Buchstaben umschalten (die Master-PIN enthält Buchstaben). */
    fun keyboardToggle(ctx: Context, field: EditText) = text(ctx, "ABC – Buchstaben eingeben", 14f, 0xCCFFFFFF.toInt(), center = true).apply {
        setPadding(0, dpi(ctx, 4), 0, dpi(ctx, 8))
        setOnClickListener {
            val numeric = field.inputType and InputType.TYPE_CLASS_NUMBER != 0
            field.inputType = if (numeric) InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD
                              else InputType.TYPE_CLASS_NUMBER or InputType.TYPE_NUMBER_VARIATION_PASSWORD
            text = if (numeric) "123 – Ziffern eingeben" else "ABC – Buchstaben eingeben"
            field.setSelection(field.text.length)
        }
    }

    fun nightBackground(a: Activity) {
        a.window.decorView.background = GradientDrawable(GradientDrawable.Orientation.TOP_BOTTOM, intArrayOf(NIGHT_TOP, NIGHT_BOTTOM))
        a.window.statusBarColor = NIGHT_TOP
        a.window.navigationBarColor = NIGHT_BOTTOM
    }
}

/** Sternenhimmel mit Mond – derselbe liebe Stil wie der Windows-Sperrbildschirm. */
class StarSkyView(ctx: Context) : View(ctx) {
    private data class Star(val x: Float, val y: Float, val r: Float, val phase: Float, val speed: Float)
    private val stars = List(90) { Star(Random.nextFloat(), Random.nextFloat() * 0.75f, 1f + Random.nextFloat() * 2.4f,
        Random.nextFloat() * 6.28f, 0.6f + Random.nextFloat() * 1.6f) }
    private val starPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.WHITE }
    private val moonPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = 0xFFFFF3C4.toInt() }
    private val glowPaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private var t = 0f
    private val anim = ValueAnimator.ofFloat(0f, 1000f).apply {
        duration = 1_000_000; repeatCount = ValueAnimator.INFINITE
        addUpdateListener { t = it.animatedValue as Float; invalidate() }
    }

    override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
        val mr = minOf(w, h) * 0.08f
        glowPaint.shader = android.graphics.RadialGradient(w * 0.8f, h * 0.12f, maxOf(1f, mr * 2.6f), 0x55FFF3C4, 0x00FFF3C4, Shader.TileMode.CLAMP)
    }

    override fun onAttachedToWindow() { super.onAttachedToWindow(); anim.start() }
    override fun onDetachedFromWindow() { anim.cancel(); super.onDetachedFromWindow() }

    override fun onDraw(c: Canvas) {
        val w = width.toFloat(); val h = height.toFloat()
        for (s in stars) {
            val a = (0.35f + 0.65f * ((sin(s.phase + t * s.speed) + 1f) / 2f))
            starPaint.alpha = (a * 255).toInt()
            c.drawCircle(s.x * w, s.y * h, s.r * resources.displayMetrics.density * 0.6f, starPaint)
        }
        val mx = w * 0.8f; val my = h * 0.12f; val mr = minOf(w, h) * 0.08f
        c.drawCircle(mx, my, mr * 2.6f, glowPaint)
        c.drawCircle(mx, my, mr, moonPaint)
        moonPaint.color = Ui.NIGHT_TOP
        c.drawCircle(mx + mr * 0.45f, my - mr * 0.25f, mr * 0.85f, moonPaint)   // Sichel
        moonPaint.color = 0xFFFFF3C4.toInt()
    }
}

/** Zeitleiste „von wann bis wann" – gleiche Farben wie Sperrbildschirm (Windows) und Admin. */
class TimelineView(ctx: Context) : View(ctx) {
    var segments: List<UsageSegmentView> = emptyList(); set(v) { field = v; invalidate() }
    var nowMinute: Int = -1; set(v) { field = v; invalidate() }
    private val track = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = 0x33FFFFFF }
    private val block = Paint(Paint.ANTI_ALIAS_FLAG)
    private val label = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = 0xBBFFFFFF.toInt(); textAlign = Paint.Align.CENTER }
    private val now = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.WHITE; strokeWidth = 3f }
    private val rect = RectF()

    override fun onMeasure(wSpec: Int, hSpec: Int) {
        setMeasuredDimension(MeasureSpec.getSize(wSpec), Ui.dpi(context, 54))
    }

    override fun onDraw(c: Canvas) {
        val d = resources.displayMetrics.density
        label.textSize = 11 * d
        val first = (segments.map { it.startMinute } + listOfNotNull(nowMinute.takeIf { it >= 0 })).minOrNull() ?: 480
        val last = (segments.map { it.endMinute } + listOfNotNull(nowMinute.takeIf { it >= 0 })).maxOrNull() ?: 1200
        val axisStart = minOf(first, 8 * 60) / 60 * 60
        val axisEnd = minOf(1440, (maxOf(last, 20 * 60) + 59) / 60 * 60)
        val span = maxOf(60, axisEnd - axisStart).toFloat()
        val w = width.toFloat()
        fun x(m: Int) = (m - axisStart) / span * w
        val top = 4 * d; val bottom = 30 * d
        rect.set(0f, top, w, bottom)
        c.drawRoundRect(rect, 10 * d, 10 * d, track)
        segments.forEachIndexed { i, s ->
            val color = Ui.BLOCK_COLORS[i % Ui.BLOCK_COLORS.size]
            block.color = color
            rect.set(x(s.startMinute), top, maxOf(x(s.endMinute), x(s.startMinute) + 4 * d), bottom)
            c.drawRoundRect(rect, 8 * d, 8 * d, block)
        }
        if (nowMinute in axisStart..axisEnd) c.drawLine(x(nowMinute), top - 2 * d, x(nowMinute), bottom + 2 * d, now)
        val step = if (span > 600) 120 else 60
        var m = axisStart
        while (m <= axisEnd) {
            c.drawText("${m / 60} Uhr", x(m).coerceIn(18 * d, w - 18 * d), bottom + 18 * d, label)
            m += step
        }
    }
}

/** „70 Min + 50 Min = 2 Std" – damit Kinder sehen, dass die Zeit wirklich um ist. */
fun calcLine(segs: List<UsageSegmentView>, total: Int): String {
    val parts = segs.map { it.minutes }.filter { it > 0 }
    return if (parts.size in 2..8) parts.joinToString(" + ") { UsageTimeline.fmtMinutes(it) } + " = " + UsageTimeline.fmtMinutes(total)
           else ""
}
