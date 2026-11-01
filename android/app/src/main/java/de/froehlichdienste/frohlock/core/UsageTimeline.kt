package de.froehlichdienste.frohlock.core

import org.json.JSONObject
import java.time.Instant
import java.time.LocalDateTime
import java.time.ZoneId

/** Zusammenhängender Nutzungsabschnitt (Unix-Sekunden). */
data class UsageSegment(var startUnix: Long, var endUnix: Long, var seconds: Int) {
    fun toJson(): JSONObject = JSONObject().put("startUnix", startUnix).put("endUnix", endUnix).put("seconds", seconds)

    companion object {
        fun fromJson(o: JSONObject) = UsageSegment(o.optLong("startUnix"), o.optLong("endUnix"), o.optInt("seconds"))
    }
}

/** Anzeigefertiger Abschnitt – gleiche Feldnamen wie der Windows-Heartbeat (Admin-Zeitleiste). */
data class UsageSegmentView(val start: String, val end: String, val startMinute: Int, val endMinute: Int, val minutes: Int) {
    fun toJson(): JSONObject = JSONObject()
        .put("start", start).put("end", end)
        .put("startMinute", startMinute).put("endMinute", endMinute).put("minutes", minutes)
}

/** „Von wann bis wann" – Port von FrohLock.Core.Schedule.UsageTimeline. */
object UsageTimeline {
    const val MERGE_GAP_SECONDS = 90
    const val DISPLAY_MERGE_GAP_SECONDS = 5 * 60

    fun accrue(segments: MutableList<UsageSegment>, nowUnix: Long, seconds: Int) {
        if (seconds <= 0) return
        val last = segments.lastOrNull()
        if (last != null && nowUnix >= last.endUnix && nowUnix - last.endUnix <= MERGE_GAP_SECONDS) {
            last.endUnix = nowUnix
            last.seconds += seconds
            return
        }
        segments.add(UsageSegment(nowUnix - seconds, nowUnix, seconds))
        if (segments.size > 300) {
            val merged = merge(segments, DISPLAY_MERGE_GAP_SECONDS)
            segments.clear(); segments.addAll(merged)
        }
    }

    fun merge(segments: List<UsageSegment>, maxGapSeconds: Int): List<UsageSegment> {
        val result = mutableListOf<UsageSegment>()
        for (s in segments.sortedBy { it.startUnix }) {
            val last = result.lastOrNull()
            if (last != null && s.startUnix - last.endUnix <= maxGapSeconds) {
                last.endUnix = maxOf(last.endUnix, s.endUnix)
                last.seconds += s.seconds
            } else result.add(s.copy())
        }
        return result
    }

    fun toViews(segments: List<UsageSegment>, zone: ZoneId): List<UsageSegmentView> =
        merge(segments, DISPLAY_MERGE_GAP_SECONDS).map { s ->
            val a = LocalDateTime.ofInstant(Instant.ofEpochSecond(s.startUnix), zone)
            val b = LocalDateTime.ofInstant(Instant.ofEpochSecond(s.endUnix), zone)
            val nextDay = b.toLocalDate().isAfter(a.toLocalDate())
            val startMin = a.hour * 60 + a.minute
            var endMin = if (nextDay) 1440 else b.hour * 60 + b.minute
            if (endMin <= startMin) endMin = minOf(1440, startMin + 1)
            UsageSegmentView(
                start = a.format(ScheduleEngine.HM),
                end = if (nextDay) "24:00" else b.format(ScheduleEngine.HM),
                startMinute = startMin, endMinute = endMin,
                minutes = Math.round(s.seconds / 60.0).toInt(),
            )
        }

    /** 120 -> "2 Std", 75 -> "1 Std 15 Min", 40 -> "40 Min" (wie Windows/Admin). */
    fun fmtMinutes(m: Int): String {
        if (m < 60) return "$m Min"
        val h = m / 60; val r = m % 60
        return if (r == 0) "$h Std" else "$h Std $r Min"
    }
}
