package de.froehlichdienste.frohlock.core

import org.json.JSONObject
import java.security.KeyFactory
import java.security.MessageDigest
import java.security.PublicKey
import java.security.SecureRandom
import java.security.Signature
import java.security.spec.MGF1ParameterSpec
import java.security.spec.PSSParameterSpec
import java.security.spec.X509EncodedKeySpec
import java.util.Base64
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/**
 * PBKDF2-HMAC-SHA256 – selbst implementiert, damit die Bytes EXAKT wie .NET
 * Rfc2898DeriveBytes.Pbkdf2 und Pythons hashlib.pbkdf2_hmac entstehen (PIN als UTF-8).
 * (Javas PBEKeySpec arbeitet mit char[] und ist je nach Anbieter nicht sicher UTF-8.)
 */
object PinHasher {
    const val DEFAULT_ITERATIONS = 210_000
    private const val SALT_BYTES = 16
    private const val HASH_BYTES = 32

    fun pbkdf2(password: ByteArray, salt: ByteArray, iterations: Int, dkLen: Int): ByteArray {
        val mac = Mac.getInstance("HmacSHA256")
        require(password.isNotEmpty()) { "Leere PIN" }   // SecretKeySpec lehnt leere Schlüssel ab
        mac.init(SecretKeySpec(password, "HmacSHA256"))
        val hLen = mac.macLength
        val blocks = (dkLen + hLen - 1) / hLen
        val out = ByteArray(dkLen)
        var pos = 0
        for (i in 1..blocks) {
            mac.update(salt)
            mac.update(byteArrayOf((i ushr 24).toByte(), (i ushr 16).toByte(), (i ushr 8).toByte(), i.toByte()))
            var u = mac.doFinal()
            val t = u.copyOf()
            for (j in 1 until iterations) {
                u = mac.doFinal(u)
                for (k in t.indices) t[k] = (t[k].toInt() xor u[k].toInt()).toByte()
            }
            val n = minOf(hLen, dkLen - pos)
            System.arraycopy(t, 0, out, pos, n)
            pos += n
        }
        return out
    }

    fun hash(pin: String, iterations: Int = DEFAULT_ITERATIONS): Pair<String, String> {
        val salt = ByteArray(SALT_BYTES).also { SecureRandom().nextBytes(it) }
        val h = pbkdf2(pin.toByteArray(Charsets.UTF_8), salt, iterations, HASH_BYTES)
        return Base64.getEncoder().encodeToString(h) to Base64.getEncoder().encodeToString(salt)
    }

    fun verify(pin: String, hashB64: String, saltB64: String, iterations: Int = DEFAULT_ITERATIONS): Boolean {
        if (hashB64.isEmpty() || saltB64.isEmpty() || pin.isEmpty()) return false
        return try {
            val salt = Base64.getDecoder().decode(saltB64)
            val expected = Base64.getDecoder().decode(hashB64)
            val actual = pbkdf2(pin.toByteArray(Charsets.UTF_8), salt, iterations, expected.size)
            MessageDigest.isEqual(actual, expected)
        } catch (e: Exception) {
            false
        }
    }
}

/**
 * Master-/Notfall-PINs: dieselben Hashes wie Branding.cs (Windows). Entsperren IMMER –
 * auch ohne Einrichtung, ohne Netz und über Fail-Secure hinweg.
 */
object MasterPin {
    private const val HASH1 = "4niEsTR/uwcpdSkYNAPvq9sOYFw2iFQUE1OkN7GEsJM="
    private const val SALT1 = "gh3nKrbedEjnc7EvZx9Xdg=="
    private const val HASH2 = "3W8tJJ+3LLmin6RmCJLzOy9wknJLOrSCkRIFPill2vs="
    private const val SALT2 = "STcnhS506Krp4V2BfGCY7Q=="
    private const val ITER = 210_000

    fun isMaster(pin: String) = PinHasher.verify(pin, HASH1, SALT1, ITER) || PinHasher.verify(pin, HASH2, SALT2, ITER)
}

/** Fest eingebauter Public Key des Signaturservers (PRODUKTIV, identisch zu EmbeddedKeys.cs). */
object EmbeddedKeys {
    const val SIGNING_PUBLIC_KEY_PEM = """-----BEGIN PUBLIC KEY-----
MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAhMBhplCOg5jcy2ORMbR5
cyF08qRgUyJjsxfyYGTqwW6Y61e+hICRk9tczqHmCo9eWMS70BHZrickHsBhl0Ms
31fymtbunk85yxbuif8EwpH84zAk4Dh1ZvkcsMD/J93bBHQ8XAD9gW5YC1pTAdBp
toUSV+wheUoTE3DQoAAji/AOpZcd6RUSpomLdw1YwszpvPyZXvpg3BZNlQFlwdYY
2Exrf33lzqmCOnoaxcNJlv62kpLm4k2Myyyi6pw3v21CAFHuHF/vVWvdCjZjiE/W
KlMEDS3+xhpyh/6lXfuOcv0g08N7IsGz6Vv+qYBS/p++YMzu38Lo5DFX6Q2L96YX
MwIDAQAB
-----END PUBLIC KEY-----"""
}

/** RSASSA-PSS / SHA-256 / MGF1-SHA-256 / Salt 32 – passt zu server/app/signing.py. */
class SignatureVerifier(publicKeyPem: String = EmbeddedKeys.SIGNING_PUBLIC_KEY_PEM) {
    private val key: PublicKey = run {
        val b64 = publicKeyPem.lines().filter { it.isNotBlank() && !it.startsWith("-----") }.joinToString("")
        KeyFactory.getInstance("RSA").generatePublic(X509EncodedKeySpec(Base64.getDecoder().decode(b64)))
    }

    private fun newSignature(): Signature {
        // Android (Conscrypt) kennt „SHA256withRSA/PSS" (Salt = 32, MGF1-SHA256); die JVM (Tests) „RSASSA-PSS".
        return try {
            Signature.getInstance("SHA256withRSA/PSS")
        } catch (e: Exception) {
            Signature.getInstance("RSASSA-PSS").apply {
                setParameter(PSSParameterSpec("SHA-256", "MGF1", MGF1ParameterSpec.SHA256, 32, 1))
            }
        }
    }

    fun verify(data: ByteArray, signature: ByteArray): Boolean = try {
        val s = newSignature()
        s.initVerify(key)
        s.update(data)
        s.verify(signature)
    } catch (e: Exception) {
        false
    }

    /** Verifizierte Nutzlast oder null. */
    fun open(env: SignedEnvelope): ByteArray? = try {
        val payload = Base64.getDecoder().decode(env.payloadBase64)
        val sig = Base64.getDecoder().decode(env.signatureBase64)
        if (verify(payload, sig)) payload else null
    } catch (e: Exception) {
        null
    }

    fun openJson(env: SignedEnvelope): JSONObject? = open(env)?.let {
        try { JSONObject(String(it, Charsets.UTF_8)) } catch (e: Exception) { null }
    }
}

fun sha256Hex(bytes: ByteArray): String =
    MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it) }
