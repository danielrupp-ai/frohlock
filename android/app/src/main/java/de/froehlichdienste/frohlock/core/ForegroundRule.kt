package de.froehlichdienste.frohlock.core

/**
 * Welche App „zählt" als vorne, wenn Android gerade einen System-Dialog meldet?
 *
 * System-Dialoge (Berechtigungsabfrage, Teilen-Dialog, Fotoauswahl, Dateiauswahl) liegen nur
 * halb über der App darunter – die läuft sichtbar weiter (Video, Spiel). Würde FrohLock den Dialog
 * selbst als „erlaubt" werten und das Overlay wegnehmen, könnte das Kind in der Sperre ein Video starten,
 * einen Dialog aufklappen und einfach weiterschauen. Darum gilt: Ein System-Dialog erbt die Einordnung
 * der zuletzt echten App darunter. Telegram + Fotoauswahl bleibt erlaubt (wenn Telegram erlaubt ist),
 * Spiel + Berechtigungsdialog bleibt gesperrt.
 */
object ForegroundRule {
    /**
     * @param foreground  was Android gerade als vorderste App meldet (null = unbekannt)
     * @param lastReal    die letzte vorderste App, die KEIN System-Dialog war (null = noch keine gesehen)
     * @param system      Pakete, die als System-Dialog gelten
     * @return das Paket, nach dem sich die Sperre richtet (null = unbekannt → nicht erlaubt)
     */
    fun effective(foreground: String?, lastReal: String?, system: Set<String>): String? =
        if (foreground != null && foreground in system) lastReal else foreground

    /**
     * Zählt die Zeit gerade NICHT aufs Tageslimit? Nur wenn die (echte) vorderste App eine Ausnahme
     * „zählt nicht“ ist. Unbekannt (keine Nutzungsdaten) oder FrohLock selbst → zählt (lieber zu viel als Schlupfloch).
     */
    fun isUncounted(foreground: String?, lastReal: String?, system: Set<String>, uncounted: Collection<String>, own: String): Boolean {
        if (uncounted.isEmpty()) return false
        val real = effective(foreground, lastReal, system) ?: return false
        return real != own && real in uncounted
    }
}
