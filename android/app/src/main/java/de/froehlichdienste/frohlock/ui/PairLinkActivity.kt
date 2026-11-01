package de.froehlichdienste.frohlock.ui

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import de.froehlichdienste.frohlock.android.Guard
import de.froehlichdienste.frohlock.android.Pairing

/**
 * Kopplungs-Link von der Installations-Seite (frohlock://koppeln?code=123456): Code übernehmen und
 * die Einrichtung öffnen. Ist das Gerät schon eingerichtet, passiert nichts (ein Kind kann damit
 * nichts umkoppeln – Neu-Koppeln geht nur im Eltern-Bereich mit PIN).
 */
class PairLinkActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        Guard.init(this)
        val code = intent?.data?.getQueryParameter("code")?.filter { it.isDigit() }?.take(6).orEmpty()
        val p = Guard.pairing()
        val configured = p?.registered == true && Guard.enforcer.isConfigured
        if (!configured && code.length == 6 && p?.registered != true) {
            Guard.savePairing(Pairing(p?.serverUrl ?: Pairing.DEFAULT_SERVER, pendingCode = code))
            Guard.audit.write("SETUP", "Kopplungs-Link geöffnet – Code übernommen")
        }
        startActivity(Intent(this, if (configured) MainActivity::class.java else SetupActivity::class.java))
        finish()
    }
}
