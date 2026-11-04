package de.froehlichdienste.frohlock.android

import android.app.usage.UsageEvents
import android.app.usage.UsageStatsManager
import android.content.Context
import de.froehlichdienste.frohlock.core.ScreenLedger

/**
 * Androids eigene Bildschirm-Ereignisse (über die Nutzungsdaten-Erlaubnis) für die Gegenprobe:
 * „Wie lange war der Bildschirm heute an und entsperrt – laut Android?" Unabhängig davon, ob FrohLock lief.
 */
object ScreenEvents {
    /** Sekunden „an + entsperrt" seit [dayStartWall] (Geräteuhr) bis jetzt; -1 = keine Erlaubnis / nicht lesbar. */
    fun interactiveUnlockedSecondsSince(ctx: Context, dayStartWall: Long): Long {
        if (!ForegroundApp.hasAccess(ctx)) return -1
        val usm = ctx.getSystemService(UsageStatsManager::class.java) ?: return -1
        val now = System.currentTimeMillis()
        if (now <= dayStartWall) return 0
        return try {
            // 6 h Vorlauf, damit der Zustand um Mitternacht (an/aus, Sperrbildschirm) bekannt ist.
            val events = usm.queryEvents(dayStartWall - 6 * 3600_000L, now)
            val e = UsageEvents.Event()
            val list = ArrayList<ScreenLedger.Ev>(512)
            while (events.hasNextEvent()) {
                events.getNextEvent(e)
                val t = when (e.eventType) {
                    UsageEvents.Event.SCREEN_INTERACTIVE -> ScreenLedger.SCREEN_ON
                    UsageEvents.Event.SCREEN_NON_INTERACTIVE -> ScreenLedger.SCREEN_OFF
                    UsageEvents.Event.KEYGUARD_SHOWN -> ScreenLedger.KEYGUARD_SHOWN
                    UsageEvents.Event.KEYGUARD_HIDDEN -> ScreenLedger.KEYGUARD_HIDDEN
                    else -> continue
                }
                list.add(ScreenLedger.Ev(e.timeStamp, t))
            }
            ScreenLedger.interactiveUnlockedSeconds(list, dayStartWall, now)
        } catch (_: Exception) { -1 }
    }
}
