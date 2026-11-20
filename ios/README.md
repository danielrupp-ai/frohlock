# FrohLock fürs iPhone (ohne App Store)

Apples Bildschirmzeit (FamilyControls `.individual`, ManagedSettings, DeviceActivity). Gleiche signierte Config und Befehle wie
Windows und Android: Sperrzeiten, gemeinsames Tageskonto, Eltern-PIN, Fern-Sperren/Entsperren, Auszeit und Nachrichten.

- `Sources/FrohLockCore`: Logik (PIN, Signatur, Zeitplan, Tageslimit), getestet mit `DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer swift test`
- `Shared`: App-Group-Speicher, Server-API, Engine (Sperre anwenden, Wecker, Abgleich)
- `App`: SwiftUI (Koppeln → Bildschirmzeit erlauben → Apps wählen → PIN), Eltern-Bereich
- `Monitor`: DeviceActivityMonitor (Tagesbeginn, alle 5 Min Nutzung, Sperrzeit-Grenzen, Wecker spätestens alle 15 Min)
- `Shield`: eigener Sperrbildschirm

## Einmalig (Apple-Konto, ~3 Min)
Die App Store Connect API kann **Family Controls nicht einschalten** (`FAMILY_CONTROLS is not a valid value`), und App Groups
auch nicht zuordnen. Eine der zwei Möglichkeiten:

1. **Xcode → Einstellungen → Accounts → „+“ → Apple ID** (Team 33D4MF58H5) anmelden. Danach einmal:
   `xcodebuild -project FrohLock.xcodeproj -scheme FrohLock -configuration Debug -destination generic/platform=iOS -allowProvisioningUpdates build`
   Xcode schaltet Family Controls (Development) und die App Group an allen drei App-IDs ein.
2. Oder im Portal developer.apple.com → Identifiers: App Group `group.de.froehlichdienste.frohlock` anlegen. Dann bei
   `de.froehlichdienste.frohlock`, `.monitor` und `.shield` jeweils **Family Controls (Development)** und **App Groups** (diese Gruppe) anhaken.

## Neues iPhone / neue Version
`python3 tools/release.py` holt die angemeldeten iPhones vom Server und trägt sie bei Apple ein. Danach erzeugt es die Profile neu,
baut die App und lädt sie nach `data/download/ios` hoch. Version ändern: `MARKETING_VERSION` in `project.yml`.

## Grenzen (ehrlich)
- Development-Signatur: das iPhone braucht den **Entwicklermodus**. Pro Team sind höchstens 100 iPhones pro Jahr möglich.
- Die Zeit zählt in 5-Minuten-Schritten. Befehle kommen beim nächsten Wecken an: bei Nutzung alle 5 Min, sonst nach etwa 15 Min.
- Das Kind hat eine eigene Apple-ID, deshalb kann der Gerätebesitzer die Bildschirmzeit-Erlaubnis entziehen. Dagegen hilft ein
  Bildschirmzeit-Code, den nur die Eltern kennen. Wird die Erlaubnis entzogen, meldet der Admin „Erlaubnis fehlt“.
- `denyAppRemoval` verhindert das Löschen **aller** Apps, solange FrohLock eingerichtet ist.
