# SICHERHEIT – was FrohLock leistet und was nicht

Dieses Dokument ist bewusst ehrlich. Bitte vollständig lesen, bevor du dich auf den
Schutz verlässt.

## Was FrohLock zuverlässig kann (gegen Kinder ohne Admin-Rechte)

- **Zeitgesteuerte Vollsperre** in definierten Fenstern (Kernfunktion), über alle Monitore.
- **PIN-Pflicht** zum Entsperren; PIN wird nur als PBKDF2-Hash gespeichert, nie im Klartext.
- **Uhr-Manipulation läuft ins Leere:** Die Sperre nutzt eine aus dem Internet gezogene
  Vertrauenszeit (NTP + HTTPS) plus monoton gemessene Laufzeit. Wer die Windows-Uhr
  verstellt, ändert die Sperrzeiten **nicht**. Ist die Zeit zu lange nicht verifizierbar,
  wird **fail-secure gesperrt**.
- **Beenden/Löschen erschwert:**
  - Der Dienst läuft als LocalSystem und ist für Standardbenutzer nicht stoppbar.
  - Dienst-Recovery startet ihn nach einem Absturz automatisch neu.
  - Ein Watchdog startet das Overlay in der Sitzung neu, wenn es beendet wird.
  - Programmdateien liegen in `Program Files`, die Konfiguration in einem ACL-gesperrten
    `ProgramData`-Verzeichnis (nur SYSTEM/Administratoren).
  - Die **Deinstallation verlangt den Eltern-PIN** (oder eine Admin-Wipe-Freigabe vom Server).
- **Signierte Steuerung:** Konfiguration und Fernbefehle sind RSA-signiert. Lokal
  gefälschte Dateien werden ignoriert (fail-secure). Anti-Rollback verhindert das
  Zurückspielen alter Konfigurationen.
- **Umgehungstasten** wie Alt+Tab, Windows-Taste, Alt+F4, Strg+Esc werden während der
  Sperre geschluckt.

## Was FrohLock NICHT garantieren kann (bitte ernst nehmen)

Eine reine Anwendung im Windows-Nutzerland kann einen **technisch versierten Erwachsenen
mit vollem Zugriff** nicht vollständig aussperren. Konkret nicht abgesichert sind u. a.:

- **Administratorrechte:** Wer lokaler Administrator ist, kann Dienste stoppen und Software
  entfernen. → Das Kind **muss ein Standardkonto** haben.
- **Booten von USB/Live-Linux oder abgesicherter Modus:** umgeht Windows-Dienste komplett.
- **Festplattenausbau / Zugriff an einem anderen PC.**
- **Strg+Alt+Entf (Secure Attention Sequence):** systemseitig nicht abfangbar. Über den
  dortigen Bildschirm lässt sich theoretisch der Task-Manager öffnen – dagegen wirken
  Watchdog, Fail-Secure und die Gruppenrichtlinie (siehe Härtung), nicht aber eine Garantie.
- **Kein Manipulationsschutz auf Kernel-Ebene** (kein signierter Treiber/PPL). Das wäre ein
  deutlich größeres Projekt mit Zertifizierungsaufwand.

Für den eigentlichen Zweck – **die eigenen Kinder auf ihren Laptops** – ist der Schutz
in Kombination mit der folgenden Härtung sehr wirksam.

## Härtungs-Checkliste (auf jedem Kinder-Laptop durchführen)

Diese Schritte sind **kein Code**, sondern Einrichtung. Ohne sie ist der Schutz schwächer.

1. **Standardkonto fürs Kind** (kein Administrator). Ein separates Admin-Konto mit starkem,
   dem Kind unbekanntem Passwort anlegen. → wichtigster Einzelschritt.
2. **BIOS-/UEFI-Passwort** setzen und **Booten von USB/Netzwerk deaktivieren**
   (Boot-Reihenfolge fixieren).
3. **Abgesicherten Modus einschränken** (für Standardnutzer):
   `bcdedit /set {default} safeboot minimal` NICHT setzen – stattdessen den Zugang zum
   Boot-Menü über das BIOS-Passwort und ein Admin-only-Konto absichern.
4. **BitLocker** aktivieren → schützt vor Festplattenausbau/Offline-Zugriff.
5. **Gruppenrichtlinie / Registry (optional, empfohlen):** Task-Manager für das Kinderkonto
   sperren (`DisableTaskMgr`), Registry-Editor sperren.
6. **Windows-Updates** aktiv lassen (Sicherheitsbasis).
7. **Automatische Anmeldung** des Kinderkontos, damit der Agent per Logon-Task sicher startet.

## Datenschutz / Recht

- FrohLock verarbeitet nur die zur Sperre nötigen Daten (Zeitplan, PIN-Hash, Gerätestatus).
- Es werden **keine** Bildschirminhalte, Tastatureingaben (außer dem PIN zur Prüfung) oder
  Aktivitäten protokolliert. Das Audit-Log enthält nur Ereignisse wie „gesperrt/entsperrt“.
- Der Einsatz auf Geräten der **eigenen minderjährigen Kinder** durch die Sorgeberechtigten
  ist zulässig. Auf Geräten Dritter/Erwachsener wäre eine heimliche Überwachung rechtlich
  heikel – nicht der Zweck dieser Software.
