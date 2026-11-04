# FrohLock für Android

FrohLock läuft auf Kinder-Handys und -Tablets **ohne Play Store**. Die App wird beim Einrichten
des Geräts per QR-Code geladen und wird dabei **Geräte-Verwalter (Device Owner)**. Erst das macht
die Sperre dicht: Das Kind kann FrohLock weder löschen noch beenden.

## Einrichten (Eltern)

1. Admin → **„Android-Gerät einrichten“** → Name eintragen, optional „Einstellungen wie …“
   (übernimmt Sperrzeiten, Tageslimit und Eltern-PIN eines anderen Geräts) und WLAN.
2. Kinder-Handy **neu oder auf Werkseinstellungen zurückgesetzt** einschalten (Daten vorher sichern).
3. Auf dem Willkommensbildschirm **6× tippen** → Kamera öffnet sich → QR-Code scannen.
4. Android lädt FrohLock, FrohLock koppelt sich selbst (Code steckt im QR-Code). Ohne Vorlage fragt
   FrohLock nach Eltern-PIN, Schlafenszeit und Tageslimit.
5. Handy-Einrichtung normal fertigstellen (Google-Konto usw.). Play Store funktioniert normal.

Die QR-Seite meldet von selbst „✅ Das Gerät hat sich gemeldet“.

**Ohne Zurücksetzen** (nur ohne angemeldete Konten): APK von `/download/android` installieren,
`adb shell dpm set-device-owner de.froehlichdienste.frohlock/de.froehlichdienste.frohlock.android.AdminReceiver`,
dann in FrohLock „Einrichtung abschließen“ mit Kopplungscode. Ohne Device Owner läuft FrohLock nur
im schwachen Modus (Admin zeigt „⚠ ohne Geräte-Verwaltung“).

## Google Family Link

Ist das Kinder-Handy mit Family Link beaufsichtigt, blockiert Family Link die Installation der APK
(„Frag deine Eltern“). Freigabe auf dem Eltern-Handy: Family Link → Kind → **Angemeldete Geräte** →
Gerät → **Apps aus unbekannten Quellen** einschalten (oder im Browser über g.co/yourfamily).
Danach Weg 2 (Link) normal. Weg 1 (QR, Device Owner) und Family-Link-Aufsicht schließen sich aus.

## Was gesperrt wird

| Wann | Was passiert |
|---|---|
| Sperrzeit / Tageslimit / Auszeit / „Sofort sperren“ | Sperrbildschirm im **Sperr-Modus (Lock Task)**: Home, Übersicht, Benachrichtigungen aus. Erreichbar bleiben nur FrohLock, **Telefon** (Notruf, Eltern anrufen, eingehende Anrufe) und der **Wecker**. Zusätzlich werden alle Apps mit Startsymbol pausiert (greift auch direkt nach einem Neustart). |
| Immer | FrohLock nicht deinstallierbar, nicht „Beenden erzwingen“/„Daten löschen“; Uhrzeit automatisch und nicht verstellbar; kein abgesicherter Modus; kein Zurücksetzen über die Einstellungen; keine Entwickleroptionen/kein USB-Debugging; keine Apps aus Dateien (Play Store geht); keine weiteren Nutzer/Arbeitsprofile. |
| Eltern-Bereich (PIN) → „Einstellungen 15 Min freigeben“ | Hebt die „Immer“-Sperren außer dem Löschschutz für 15 Minuten auf, danach automatisch wieder an. |

**Immer erlaubt** (auch in jeder Sperre): Telegram und Signal (Standard, im Admin je Gerät unter
„Immer erlaubt“ änderbar: WhatsApp, Threema, eigene Paketnamen), dazu Telefon und Wecker. Auf dem
Sperrbildschirm gibt es dafür Knöpfe („Telegram öffnen“).

**Einfacher Schutz (Weg 2, ohne Zurücksetzen)** braucht: Über anderen Apps einblenden,
Geräteadministrator und **Nutzungsdaten** (erkennt die App im Vordergrund). Ablauf in der Sperre:
erlaubte App vorne → in Ruhe lassen; alles andere → Vollbild-Overlay + Sperrbildschirm sofort zurück
(Takt 1,5 s); bleibt das Kind > 12 s woanders → Bildschirm aus (`lockNow`). Wegwischen aus der
Übersicht: Dienst läuft weiter, Wächter-Wecker startet ihn jede Minute neu, falls das System ihn
beendet. Die Auszeit läuft nur, solange der Sperrbildschirm sichtbar ist.

Bildschirmzeit zählt nur bei **Bildschirm an und entsperrt** (wie „Bildschirm an“ am Laptop).

**Gegenprobe (ab 1.0.5):** Android schreibt selbst mit, wann der Bildschirm an und entsperrt war
(Nutzungsdaten-Ereignisse). FrohLock vergleicht alle 2 Minuten und beim Start seinen eigenen Stand damit.
Fehlen FrohLock ≥ 5 Minuten gegenüber Android (Dienst vom System beendet, ausgebremst, Bildschirm falsch als
„aus“ gesehen), wird die Zeit **nachgezählt** (zählt aufs Limit, Protokoll `LUECKE ⚠`, Admin → „Auffälligkeiten“
und Android-Karte „Gegenprobe heute“). Zeit in Telegram/Signal/Telefon *während einer Sperre* zählt nicht aufs
Limit, wird aber im Admin ausgewiesen. Die Zeitzone ist fest (beim ersten Start gemerkt): stellt das Kind die
Zone um, springt „heute“ nicht; ein zurückgedrehtes Datum setzt die Bildschirmzeit nicht zurück. Eltern übernehmen
eine neue Zone über „Einstellungen 15 Min freigeben“.

## Freigeben

- Am Sperrbildschirm: **Eltern-PIN** → 30 Min / 1 Std / 2 Std. Die **Master-PINs** gelten auch hier.
- Admin: Entsperren, Sofort sperren, 15-Min-Auszeit (wirken in Sekunden, wenn das Handy online ist).
- Eltern-Bereich in der App: freigeben, Freigabe beenden, neu koppeln, Protokoll, **FrohLock entfernen**
  (hebt alle Sperren auf, gibt die Geräte-Verwaltung ab; danach normal deinstallierbar).
- Admin „App entfernen“ macht dasselbe aus der Ferne.

## Updates

Release bauen: Tag `android-vX.Y.Z` pushen → GitHub Actions (`.github/workflows/android.yml`) testet,
signiert und hängt `FrohLock-X.Y.Z.apk`, `android-cert.txt`, `SHA256SUMS.txt` an das Release.

Bereitstellen auf dem Server (kein Container-Neustart nötig):

```bash
bash ~/apps/frohlock/repo/deploy/update-download.sh android X.Y.Z
```

Danach bekommen alle Android-Geräte das Update beim nächsten Kontakt **still**:
- Weg 1 (Geräte-Verwalter): immer ohne Rückfrage.
- Weg 2 (Link): ab Android 12 ebenfalls ohne Rückfrage, sobald FrohLock selbst die Installationsquelle
  ist. Nach der Installation über den Browser ist das noch nicht so → das **erste** Update einmal
  bestätigen (Benachrichtigung „FrohLock-Update“ bzw. Knopf in der App), danach automatisch.
  Voraussetzung: FrohLock darf Apps installieren („Aus dieser Quelle zulassen“). Admin zeigt
  „Updates: ✓ automatisch“. Ab Android 14 beansprucht FrohLock die Update-Hoheit. Manifest-Signatur: `android|Version|SHA-256` mit dem Server-Schlüssel.

## ⚠ Signierschlüssel (nie verlieren)

Jedes Update muss mit **demselben** Schlüssel signiert sein, sonst lehnt Android es ab und der
QR-Code passt nicht mehr (Prüfsumme des Zertifikats). Ablage:

- GitHub-Secrets `FROHLOCK_ANDROID_KEYSTORE_B64`, `FROHLOCK_ANDROID_KEYSTORE_PASSWORD` (für die CI)
- Server `~/apps/frohlock/secrets/android-release.jks` + `android-keystore-password.txt` (Sicherung)
- Mac `~/.frohlock-android/` (Sicherung)

`update-download.sh` bricht ab, wenn sich das Zertifikat ändert.

## Ehrliche Grenzen

- Zurücksetzen über den **Recovery-Modus** (Tastenkombination beim Einschalten) lässt sich von keiner
  App verhindern. Danach ist FrohLock weg – das Admin-Web zeigt dann „offline“, und alle Daten auf dem
  Handy sind gelöscht (das merkt man).
- Ohne Netz gelten Zeitplan, Limit und PIN weiter; Fernbefehle kommen beim nächsten Kontakt an.
- Bildschirmzeit zählt je Gerät; ein gemeinsames Limit über Laptop und Handy gibt es (noch) nicht.

## Entwicklung

```bash
cd android
export JAVA_HOME=/opt/homebrew/opt/openjdk@17 ANDROID_HOME=$HOME/android-sdk
./gradlew :app:testReleaseUnitTest :app:assembleRelease     # ohne Schlüssel: Debug-signiert
```

Der Kern (`core/`: Zeitplan, Limit, Auszeit, PIN-PBKDF2, RSA-PSS-Prüfung) ist eine Portierung von
`FrohLock.Core` und wird gegen echte Server-Signaturen getestet (`app/src/test/resources/xlang.json`,
erzeugt mit `server/app/signing.py` und dem Dev-Schlüssel).
