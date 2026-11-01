package de.froehlichdienste.frohlock.android

import android.content.Context
import android.os.Build
import android.provider.Settings
import de.froehlichdienste.frohlock.core.CommandType
import de.froehlichdienste.frohlock.core.DeviceCommand
import org.json.JSONObject

/** Netz-Teil: Kopplung, Config, Fernbefehle, Heartbeat. Läuft auf dem Netz-Thread des Dienstes. */
object Sync {
    @Volatile var lastContactMs = 0L; private set
    @Volatile var lastError: String = ""; private set

    fun deviceInfo(ctx: Context): JSONObject = JSONObject()
        .put("model", "${Build.MANUFACTURER} ${Build.MODEL}".trim())
        .put("android", "Android ${Build.VERSION.RELEASE} (API ${Build.VERSION.SDK_INT})")
        .put("deviceOwner", DeviceOwner.isDeviceOwner(ctx))

    fun defaultDeviceName(ctx: Context): String {
        val n = try { Settings.Global.getString(ctx.contentResolver, Settings.Global.DEVICE_NAME) } catch (_: Exception) { null }
        return n?.takeIf { it.isNotBlank() } ?: "${Guard.deviceNoun()} ${Build.MODEL}"
    }

    private fun api(p: Pairing): ServerApi =
        ServerApi(p.serverUrl, p.deviceId, p.token, Guard.enforcer.currentConfig?.tlsSpkiPins ?: emptyList())

    /** Kopplungscode einlösen (QR-Code oder Eingabe). Wirft bei Fehler – Text ist für Eltern lesbar. */
    fun register(ctx: Context, serverUrl: String, code: String, deviceName: String): Pairing {
        val api = ServerApi(serverUrl)
        val r = try {
            api.register(code.trim(), deviceName, deviceInfo(ctx))
        } catch (e: ServerApi.HttpError) {
            val msg = try { JSONObject(e.body).optString("detail") } catch (_: Exception) { "" }
            throw IllegalStateException(msg.ifBlank { "Server antwortet mit Fehler ${e.code}" })
        }
        val p = Pairing(serverUrl.trimEnd('/'), r.deviceId, r.token, deviceName, "")
        Guard.savePairing(p)
        Guard.audit.write("SETUP", "Gekoppelt als „$deviceName“ (${r.deviceId.take(8)}…)")
        api.lastServerDateMs?.let { Guard.clock.acceptServerTime(it) }
        // Gleich die Einstellungen holen (können aus einem anderen Gerät übernommen sein).
        try { fetchConfig(p) } catch (_: Exception) { }
        return p
    }

    /** Ausstehende Kopplung aus dem QR-Code nachholen (falls bei der Einrichtung kein Netz war). */
    fun tryPendingRegistration(ctx: Context) {
        val p = Guard.pairing() ?: return
        if (p.registered || p.pendingCode.isBlank()) return
        try {
            register(ctx, p.serverUrl, p.pendingCode, p.deviceName.ifBlank { defaultDeviceName(ctx) })
        } catch (e: Exception) {
            lastError = e.message ?: e.javaClass.simpleName
            if (e is IllegalStateException) {
                // Code ungültig/abgelaufen: nicht endlos wiederholen, Eltern müssen neu koppeln.
                Guard.savePairing(p.copy(pendingCode = ""))
                Guard.audit.write("SETUP", "⚠ Kopplung fehlgeschlagen: ${e.message} – bitte im Eltern-Bereich neu koppeln")
            }
        }
    }

    fun fetchConfig(p: Pairing = Guard.pairing()!!) {
        val api = api(p)
        val have = Guard.enforcer.currentConfig?.configVersion ?: 0
        api.getConfig(have)?.let { Guard.applyConfig(it) }
        api.lastServerDateMs?.let { Guard.clock.acceptServerTime(it) }
    }

    /** Volle Runde: Config, Befehle (inkl. Update), Heartbeat. */
    fun fullSync(ctx: Context) {
        val p = Guard.pairing()?.takeIf { it.registered } ?: return
        val api = api(p)
        try {
            val have = Guard.enforcer.currentConfig?.configVersion ?: 0
            api.getConfig(have)?.let { Guard.applyConfig(it) }
            runCommands(ctx, api, includeUpdate = true)
            api.heartbeat(GuardService.buildStatus(ctx))
            api.lastServerDateMs?.let { Guard.clock.acceptServerTime(it) }
            lastContactMs = System.currentTimeMillis()
            lastError = ""
        } catch (e: ServerApi.HttpError) {
            lastError = "HTTP ${e.code}"
            if (e.code == 401) Guard.audit.write("NETZ", "⚠ Server kennt dieses Gerät nicht mehr (401) – im Admin gelöscht? Bitte neu koppeln")
        } catch (e: Exception) {
            lastError = e.javaClass.simpleName
        }
    }

    /** Schnelle Runde (nur Befehle) – damit „Sofort sperren"/„Entsperren" in Sekunden wirkt. */
    fun fastCommands(ctx: Context) {
        val p = Guard.pairing()?.takeIf { it.registered } ?: return
        try {
            val api = api(p)
            runCommands(ctx, api, includeUpdate = false)
            api.lastServerDateMs?.let { Guard.clock.acceptServerTime(it) }
            lastContactMs = System.currentTimeMillis()
        } catch (_: Exception) { }
    }

    private fun runCommands(ctx: Context, api: ServerApi, includeUpdate: Boolean) {
        for (env in api.commands()) {
            val json = Guard.verifier.openJson(env)
            if (json == null) { Guard.audit.write("CMD", "Ungültige Signatur – verworfen"); continue }
            val cmd = DeviceCommand.fromJson(json)
            if (cmd.type == CommandType.UPDATE && !includeUpdate) continue
            val myId = Guard.pairing()?.deviceId
            if (cmd.deviceId.isNotEmpty() && cmd.deviceId != myId) { Guard.audit.write("CMD", "⚠ Befehl für fremdes Gerät – verworfen"); continue }
            if (cmd.expiresAtUnix > 0 && cmd.expiresAtUnix < System.currentTimeMillis() / 1000) {
                Guard.audit.write("CMD", "${CommandType.name(cmd.type)} abgelaufen – verworfen")
                try { api.ack(cmd.commandId) } catch (_: Exception) { }
                continue
            }
            execute(ctx, api, cmd)
            try { api.ack(cmd.commandId) } catch (_: Exception) { }
        }
    }

    private fun execute(ctx: Context, api: ServerApi, cmd: DeviceCommand) {
        val e = Guard.enforcer
        // Doppelt zugestellte Befehle (Ack verloren) nur einmal – sonst addiert sich eine Auszeit doppelt.
        if (cmd.type != CommandType.UPDATE && cmd.type != CommandType.PING && !e.markCommandExecuted(cmd.commandId)) return
        if (cmd.type != CommandType.UPDATE) Guard.audit.write("CMD", "Ausführen: ${CommandType.name(cmd.type)}")
        when (cmd.type) {
            CommandType.TIMEOUT -> e.addPenalty(cmd.unlockMinutes ?: 15, "gestört ohne Notfall")
            CommandType.CLEAR_TIMEOUT -> e.clearPenalty("Eltern")
            CommandType.UNLOCK -> e.remoteUnlock(cmd.unlockMinutes ?: (e.currentConfig?.unlockGraceMinutes ?: 60), "Fern-Entsperrung (Admin)")
            CommandType.LOCK -> e.forceLock("Fern-Befehl")
            CommandType.UPDATE_CONFIG, CommandType.RESET_PIN -> try {
                api.getConfig(e.currentConfig?.configVersion ?: 0)?.let { Guard.applyConfig(it) }
            } catch (_: Exception) { }
            CommandType.UPDATE -> Updater.tryUpdate(ctx, api)
            CommandType.UNINSTALL -> GuardService.releaseDevice(ctx, "Admin-Befehl „Entfernen“")
            CommandType.PING -> Unit
        }
        GuardService.poke(ctx)
    }
}
