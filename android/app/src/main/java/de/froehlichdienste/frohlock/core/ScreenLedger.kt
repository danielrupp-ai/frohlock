package de.froehlichdienste.frohlock.core

/**
 * Gegenprobe aus Androids eigenen Bildschirm-Ereignissen (UsageEvents: Bildschirm an/aus, Sperrbildschirm
 * gezeigt/versteckt). Android schreibt diese Ereignisse unabhängig von FrohLock – lief der Schutzdienst
 * zeitweise nicht (vom System beendet) oder sah er den Bildschirm fälschlich als „aus", fällt das hier auf.
 * Reine Logik ohne Android-Klassen, damit sie testbar ist.
 */
object ScreenLedger {
    const val SCREEN_ON = 1
    const val SCREEN_OFF = 2
    const val KEYGUARD_SHOWN = 3
    const val KEYGUARD_HIDDEN = 4

    data class Ev(val timeMs: Long, val type: Int)

    /**
     * Sekunden „Bildschirm an UND Sperrbildschirm weg" zwischen [fromMs] und [toMs].
     * Ereignisse vor [fromMs] bestimmen nur den Anfangszustand. Unbekannt gilt als „aus" (lieber zu wenig
     * als falscher Alarm). Gibt es im ganzen Zeitraum kein Sperrbildschirm-Ereignis, hat das Gerät keinen
     * Sperrbildschirm (Android meldet dann keine) – dann zählt „Bildschirm an" allein.
     */
    fun interactiveUnlockedSeconds(events: List<Ev>, fromMs: Long, toMs: Long): Long {
        if (toMs <= fromMs) return 0
        val sorted = events.sortedBy { it.timeMs }
        val anyKeyguard = sorted.any { it.type == KEYGUARD_SHOWN || it.type == KEYGUARD_HIDDEN }
        var on = false
        var hidden = !anyKeyguard
        var cursor = fromMs
        var total = 0L
        fun advance(to: Long) {
            val t = to.coerceIn(fromMs, toMs)
            if (on && hidden && t > cursor) total += t - cursor
            if (t > cursor) cursor = t
        }
        for (e in sorted) {
            if (e.timeMs > fromMs) advance(e.timeMs)
            when (e.type) {
                SCREEN_ON -> on = true
                SCREEN_OFF -> on = false
                KEYGUARD_SHOWN -> hidden = false
                KEYGUARD_HIDDEN -> hidden = true
            }
            if (e.timeMs >= toMs) break
        }
        advance(toMs)
        return total / 1000
    }

    /** Ab wie viel Differenz nachgezählt wird: 5 Minuten – Takt-Ungenauigkeiten bleiben darunter. */
    const val MIN_GAP_SECONDS = 300L

    /**
     * Vergleich seit dem letzten Abgleich (nicht absolut, damit ein Update mitten am Tag keinen Sprung erzeugt):
     * Android-Zuwachs minus FrohLock-Zuwachs. Liefert die nachzuzählenden Sekunden (0 = alles gut).
     */
    fun missedSince(androidNow: Long, androidBase: Long, ownNow: Long, ownBase: Long): Long {
        if (androidBase < 0) return 0
        val diff = (androidNow - androidBase) - (ownNow - ownBase)
        return if (diff >= MIN_GAP_SECONDS) diff else 0
    }
}
