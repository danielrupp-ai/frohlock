# INSTALL – Auf dem Kinder-Laptop installieren

## Voraussetzungen
- Windows 10 (64-bit), inkl. Enterprise LTSC. Keine Runtime nötig (self-contained).
- Für die Installation werden **einmalig Administratorrechte** gebraucht (UAC).

## Schritte

1. **Härtung zuerst** – bitte [SICHERHEIT.md](SICHERHEIT.md) → Härtungs-Checkliste umsetzen.
   Wichtig: Das Kind bekommt ein **Standardkonto**, ein separates Admin-Konto bleibt bei dir.

2. **Kopplungscode holen:** in der Admin-Brücke unter „Neues Gerät koppeln“ einen Code erzeugen.

3. **Setup herunterladen:** die per Link bereitgestellte `FrohLockSetup-x.y.z.exe` laden.
   (Ohne Code-Signing-Zertifikat zeigt Windows SmartScreen evtl. eine Warnung → „Weitere
   Informationen“ → „Trotzdem ausführen“. Für den Dauerbetrieb ein Zertifikat besorgen,
   siehe BUILD.md.)

4. **Installieren:** Setup starten, UAC bestätigen. Es werden installiert:
   - der Schutzdienst (Autostart, Auto-Neustart),
   - der Overlay-Agent (startet bei jeder Anmeldung),
   - die Eltern-Einrichtung.

5. **Einrichten:** Am Ende öffnet sich „FrohLock einrichten“:
   - Server-Adresse + Kopplungscode → **Koppeln**,
   - **Eltern-PIN** vergeben (mind. 4 Zeichen, PIN-Code erlaubt),
   - **Sperrzeiten** anlegen (z. B. 21:00–07:00), **Speichern & aktivieren**.

6. **Prüfen:** Innerhalb ~1 Minute zieht der Dienst die signierte Konfiguration. Zur
   aktiven Sperrzeit erscheint das Vollbild-Overlay; mit dem PIN lässt es sich entsperren.

## Entsperren im Alltag
- Am Sperrbildschirm den **PIN** eingeben → temporär entsperrt (Dauer einstellbar, Standard 60 min).
- Alternativ aus der Ferne über die Admin-Brücke entsperren.

## Deinstallieren
- Nur mit **Eltern-PIN** möglich (Setup fragt ihn beim Deinstallieren ab), oder per
  **Admin-Wipe** aus der Brücke.
- Ein Standardkonto kann die Deinstallation gar nicht erst starten (Adminrechte nötig).

## Notfall: Ich komme nicht mehr in den PC (gesperrt, kein PIN)
Ab v0.5.0 sperrt FrohLock **erst nach** der Einrichtung (mit gesetztem PIN) – vorher bleibt
das Gerät offen. Falls du dennoch mal ausgesperrt bist (z. B. alte Version):
1. **Strg + Alt + Entf** (das lässt sich nicht blockieren).
2. Ein/Aus-Symbol → **Shift halten + Neu starten**.
3. **Problembehandlung → Erweiterte Optionen → Starteinstellungen → Neu starten → Taste 4**
   (Abgesicherter Modus). Dort läuft der Schutzdienst nicht → kein Sperrbildschirm.
4. **Einstellungen → Apps → FrohLock → Deinstallieren** (ohne gesetzten PIN erlaubt), oder
   PowerShell (Admin): `sc stop FrohLockService && sc config FrohLockService start= disabled`.
5. Neu starten – frei.

## Reihenfolge (wichtig)
1. Zuerst in der **Verwaltung** (`/admin`) einen **Kopplungscode** erzeugen.
2. Dann installieren und im Setup Code + **PIN** + Sperrzeiten setzen. Erst wenn ein PIN
   gesetzt ist, beginnt die Sperre.

## Fehlerbehebung
- **Overlay kommt nicht:** Läuft der Dienst `FrohLockService`? (`services.msc`, Admin nötig).
  Ist ein Nutzer angemeldet? Der Watchdog startet den Agent sonst binnen ~45 s neu.
- **„Keine Konfiguration“ / bleibt gesperrt:** Server erreichbar? Kopplung erfolgt?
  Zeit-Sync möglich (Internet)? Bei zu alter Vertrauenszeit sperrt FrohLock bewusst.
- **Zeitplan greift nicht wie erwartet:** Zeiten sind **lokale** Uhrzeit, gegen die
  Internet-Vertrauenszeit verankert. Fenster über Mitternacht (Start > Ende) sind erlaubt.
