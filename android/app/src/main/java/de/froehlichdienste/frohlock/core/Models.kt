package de.froehlichdienste.frohlock.core

import org.json.JSONArray
import org.json.JSONObject

/**
 * Gleiche Datenformen wie der Windows-Client (FrohLock.Core.Models) – der Server signiert
 * EIN Format für beide. Feldnamen camelCase, Enums als Zahl.
 */
data class ScheduleWindow(
    val id: String,
    val label: String?,
    /** 0 = Sonntag .. 6 = Samstag. Leer = alle Tage. */
    val days: List<Int>,
    val startMinute: Int,
    val endMinute: Int,
    val enabled: Boolean = true,
) {
    fun appliesTo(dow: Int) = days.isEmpty() || dow in days

    companion object {
        private val DAY_NAMES = listOf("sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday")

        fun fromJson(o: JSONObject): ScheduleWindow {
            val days = mutableListOf<Int>()
            val arr = o.optJSONArray("days") ?: JSONArray()
            for (i in 0 until arr.length()) {
                val v = arr.get(i)
                when (v) {
                    is Number -> days.add(v.toInt())
                    is String -> v.toIntOrNull()?.let { days.add(it) }
                        ?: DAY_NAMES.indexOf(v.lowercase()).takeIf { it >= 0 }?.let { days.add(it) }
                }
            }
            return ScheduleWindow(
                id = o.optString("id", ""),
                label = if (o.isNull("label")) null else o.optString("label", null),
                days = days,
                startMinute = o.optInt("startMinute", 0),
                endMinute = o.optInt("endMinute", 0),
                enabled = o.optBoolean("enabled", true),
            )
        }
    }
}

data class LockConfig(
    val configVersion: Long,
    val deviceId: String,
    val windows: List<ScheduleWindow>,
    val dailyBudgetMinutes: Int,
    /** 7 Werte, Index 0 = Sonntag; Wert > 0 überschreibt den Standard. */
    val dailyBudgetByWeekday: List<Int>,
    val unlockGraceMinutes: Int,
    val pinHash: String,
    val pinSalt: String,
    val pinIterations: Int,
    val serverBaseUrl: String,
    val tlsSpkiPins: List<String>,
    val serverPollSeconds: Int,
    val maxTrustedTimeStalenessMinutes: Int,
) {
    fun effectiveDailyBudget(dow: Int): Int {
        if (dailyBudgetByWeekday.size == 7) {
            val v = dailyBudgetByWeekday[dow]
            if (v > 0) return v
        }
        return dailyBudgetMinutes
    }

    val hasPin get() = pinHash.isNotEmpty()

    companion object {
        fun fromJson(o: JSONObject): LockConfig {
            val wins = o.optJSONArray("windows") ?: JSONArray()
            val wb = o.optJSONArray("dailyBudgetByWeekday") ?: JSONArray()
            val pins = o.optJSONArray("tlsSpkiPins") ?: JSONArray()
            return LockConfig(
                configVersion = o.optLong("configVersion", 0),
                deviceId = o.optString("deviceId", ""),
                windows = (0 until wins.length()).map { ScheduleWindow.fromJson(wins.getJSONObject(it)) },
                dailyBudgetMinutes = o.optInt("dailyBudgetMinutes", 0),
                dailyBudgetByWeekday = (0 until wb.length()).map { wb.optInt(it, 0) },
                unlockGraceMinutes = o.optInt("unlockGraceMinutes", 60),
                pinHash = o.optString("pinHash", ""),
                pinSalt = o.optString("pinSalt", ""),
                pinIterations = o.optInt("pinIterations", PinHasher.DEFAULT_ITERATIONS),
                serverBaseUrl = o.optString("serverBaseUrl", ""),
                tlsSpkiPins = (0 until pins.length()).map { pins.optString(it) }.filter { it.isNotBlank() },
                serverPollSeconds = o.optInt("serverPollSeconds", 30),
                maxTrustedTimeStalenessMinutes = o.optInt("maxTrustedTimeStalenessMinutes", 720),
            )
        }
    }
}

/** Werte wie FrohLock.Core.Models.CommandType (Server: signing.CMD). */
object CommandType {
    const val UNLOCK = 0
    const val LOCK = 1
    const val UPDATE_CONFIG = 2
    const val RESET_PIN = 3
    const val UNINSTALL = 4
    const val UPDATE = 5
    const val PING = 6
    const val TIMEOUT = 7
    const val CLEAR_TIMEOUT = 8

    fun name(t: Int) = when (t) {
        UNLOCK -> "Unlock"; LOCK -> "Lock"; UPDATE_CONFIG -> "UpdateConfig"; RESET_PIN -> "ResetPin"
        UNINSTALL -> "Uninstall"; UPDATE -> "Update"; PING -> "Ping"; TIMEOUT -> "Timeout"
        CLEAR_TIMEOUT -> "ClearTimeout"; else -> "Typ$t"
    }
}

data class DeviceCommand(
    val commandId: String,
    val deviceId: String,
    val type: Int,
    val issuedAtUnix: Long,
    val expiresAtUnix: Long,
    val unlockMinutes: Int?,
) {
    companion object {
        fun fromJson(o: JSONObject): DeviceCommand {
            // Typ kann als Zahl oder (ältere Server) als Name kommen.
            val rawType = o.opt("type")
            val type = when (rawType) {
                is Number -> rawType.toInt()
                is String -> rawType.toIntOrNull() ?: when (rawType.lowercase()) {
                    "unlock" -> 0; "lock" -> 1; "updateconfig" -> 2; "resetpin" -> 3; "uninstall" -> 4
                    "update" -> 5; "ping" -> 6; "timeout" -> 7; "cleartimeout" -> 8; else -> -1
                }
                else -> -1
            }
            return DeviceCommand(
                commandId = o.optString("commandId", ""),
                deviceId = o.optString("deviceId", ""),
                type = type,
                issuedAtUnix = o.optLong("issuedAtUnix", 0),
                expiresAtUnix = o.optLong("expiresAtUnix", 0),
                unlockMinutes = if (o.has("unlockMinutes") && !o.isNull("unlockMinutes")) o.optInt("unlockMinutes") else null,
            )
        }
    }
}

/** Server-signierter Umschlag: Signatur über GENAU die Bytes in payloadBase64. */
data class SignedEnvelope(val payloadBase64: String, val signatureBase64: String) {
    fun toJson(): JSONObject = JSONObject()
        .put("payloadBase64", payloadBase64)
        .put("signatureBase64", signatureBase64)
        .put("algorithm", "RSASSA-PSS-SHA256")

    companion object {
        fun fromJson(o: JSONObject) = SignedEnvelope(o.optString("payloadBase64", ""), o.optString("signatureBase64", ""))
    }
}

data class Decision(val locked: Boolean, val reason: String) {
    companion object {
        fun locked(reason: String) = Decision(true, reason)
        fun unlocked(reason: String) = Decision(false, reason)
    }
}
