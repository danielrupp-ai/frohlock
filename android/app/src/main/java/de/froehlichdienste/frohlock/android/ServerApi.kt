package de.froehlichdienste.frohlock.android

import de.froehlichdienste.frohlock.core.SignedEnvelope
import org.json.JSONArray
import org.json.JSONObject
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest
import java.util.Base64
import javax.net.ssl.HttpsURLConnection

/**
 * Gleiche Endpunkte wie der Windows-Client (ServerClient.cs). Sicherheitsrelevantes (Config, Befehle,
 * Update) kommt server-signiert und wird getrennt geprüft – TLS ist nur die äußere Hülle.
 */
class ServerApi(baseUrl: String, private val deviceId: String = "", private val token: String = "",
                private val tlsPins: List<String> = emptyList()) {
    private val base = baseUrl.trimEnd('/')

    /** Date-Header der letzten Antwort (Netz-Zeitquelle). */
    var lastServerDateMs: Long? = null; private set

    class HttpError(val code: Int, val body: String) : IOException("HTTP $code: $body")

    private fun request(method: String, path: String, body: JSONObject? = null, timeoutMs: Int = 15_000): Pair<Int, String> {
        val conn = URL(base + path).openConnection() as HttpURLConnection
        try {
            conn.requestMethod = method
            conn.connectTimeout = timeoutMs
            conn.readTimeout = timeoutMs
            conn.useCaches = false
            conn.setRequestProperty("Accept", "application/json")
            conn.setRequestProperty("User-Agent", "FrohLock-Android/${Guard.appVersion}")
            if (token.isNotEmpty()) conn.setRequestProperty("Authorization", "Bearer $token")
            if (body != null) {
                conn.doOutput = true
                conn.setRequestProperty("Content-Type", "application/json; charset=utf-8")
                conn.outputStream.use { it.write(body.toString().toByteArray(Charsets.UTF_8)) }
            }
            val code = conn.responseCode
            checkPins(conn)
            conn.getHeaderFieldDate("Date", 0L).takeIf { it > 0 }?.let { lastServerDateMs = it }
            val stream = if (code in 200..399) conn.inputStream else conn.errorStream
            val text = stream?.use { it.readBytes().toString(Charsets.UTF_8) } ?: ""
            return code to text
        } finally {
            conn.disconnect()
        }
    }

    /** Optionales TLS-Pinning (SPKI-SHA256, Base64) – nur wenn der Server Pins mitgibt. */
    private fun checkPins(conn: HttpURLConnection) {
        if (tlsPins.isEmpty() || conn !is HttpsURLConnection) return
        val chain = try { conn.serverCertificates } catch (_: Exception) { emptyArray() }
        val ok = chain.any { c ->
            val pin = Base64.getEncoder().encodeToString(MessageDigest.getInstance("SHA-256").digest(c.publicKey.encoded))
            pin in tlsPins
        }
        if (!ok) throw IOException("TLS-Pin passt nicht")
    }

    private fun ok(r: Pair<Int, String>): String {
        if (r.first !in 200..299) throw HttpError(r.first, r.second.take(200))
        return r.second
    }

    data class Registered(val deviceId: String, val token: String)

    fun register(pairingCode: String, deviceName: String, info: JSONObject): Registered {
        val body = JSONObject().put("pairingCode", pairingCode).put("deviceName", deviceName)
            .put("platform", "android").put("deviceInfo", info)
        val o = JSONObject(ok(request("POST", "/devices/register", body)))
        return Registered(o.getString("deviceId"), o.getString("token"))
    }

    /** null = unverändert (304) oder (noch) keine Config auf dem Server. */
    fun getConfig(have: Long): SignedEnvelope? {
        val r = request("GET", "/devices/$deviceId/config?have=$have")
        if (r.first == 304 || r.first == 404) return null
        return SignedEnvelope.fromJson(JSONObject(ok(r)))
    }

    fun setConfigDraft(draft: JSONObject) { ok(request("POST", "/devices/$deviceId/config-draft", draft)) }

    fun heartbeat(status: JSONObject) { ok(request("POST", "/devices/$deviceId/heartbeat", status)) }

    fun commands(): List<SignedEnvelope> {
        val arr = JSONArray(ok(request("GET", "/devices/$deviceId/commands")))
        return (0 until arr.length()).map { SignedEnvelope.fromJson(arr.getJSONObject(it)) }
    }

    fun ack(commandId: String) { request("POST", "/devices/$deviceId/commands/$commandId/ack", JSONObject()) }

    data class Manifest(val version: String, val url: String, val sha256: String, val signatureBase64: String)

    fun androidManifest(): Manifest? {
        val r = request("GET", "/updates/manifest?channel=stable&platform=android")
        if (r.first == 404) return null
        val o = JSONObject(ok(r))
        return Manifest(o.getString("version"), o.getString("url"), o.getString("sha256").lowercase(), o.getString("signatureBase64"))
    }
}
