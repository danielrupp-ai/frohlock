# ADMIN – Die Brücke betreiben & Geräte steuern

Die Brücke ist ein FastAPI-Server mit Admin-Weboberfläche. Sie signiert Konfiguration und
Fernbefehle und hält den privaten Schlüssel. **Nur der Server kennt den privaten Schlüssel.**

## 1. Schlüsselpaar

Für Entwicklung liegt ein Dev-Schlüssel unter `tools/dev-keys/` (privater Teil ist
**nicht** im Git). Für den Produktivbetrieb ein eigenes Paar erzeugen:

```bash
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 -out signing_private.pem
openssl rsa -in signing_private.pem -pubout -out signing_public.pem
```

- `signing_private.pem` → nur auf dem Server, Pfad via `FROHLOCK_SIGNING_KEY`.
- `signing_public.pem` → beim Client-Build einbetten (CI-Secret `SIGNING_PUBLIC_KEY`
  oder `tools/inject-public-key.ps1`). **Public Key im Client und Private Key im Server
  müssen zusammenpassen.**

## 2. Server starten (lokal/Dev)

```bash
cd server
python3 -m venv .venv && . .venv/bin/activate
pip install -r requirements.txt
cp .env.example .env   # anpassen
set -a; . ./.env; set +a
uvicorn app.main:app --host 0.0.0.0 --port 8080
```

Admin-Web: `http://localhost:8080/`  (Login mit `FROHLOCK_ADMIN_USER` / `FROHLOCK_ADMIN_PASSWORD`).

## 3. Produktivbetrieb (Hetzner o. ä.)

- Hinter **HTTPS** betreiben (Caddy/Nginx als TLS-Terminierung). `FROHLOCK_PUBLIC_URL`
  auf die öffentliche `https://…`-Adresse setzen – dieser Wert landet in der Geräte-Config.
- **TLS-Pinning:** SPKI-SHA256 des Serverzertifikats bestimmen und als `FROHLOCK_TLS_PINS`
  setzen (mehrere kommagetrennt, inkl. Backup-Pin):
  ```bash
  openssl s_client -connect dein-host:443 </dev/null 2>/dev/null \
    | openssl x509 -pubkey -noout \
    | openssl pkey -pubin -outform der \
    | openssl dgst -sha256 -binary | base64
  ```
- Als systemd-Service oder Container starten; DB (`app.db`, SQLite) sichern.
- Starke `FROHLOCK_ADMIN_PASSWORD` und feste `FROHLOCK_SESSION_SECRET` setzen.

## 4. Ein Gerät in Betrieb nehmen

1. Im Admin-Web **„Kopplungscode erzeugen“** (24 h gültig).
2. Auf dem Kinder-Laptop das Setup ausführen, Server-Adresse + Code eingeben, koppeln.
3. Eltern-PIN und Sperrzeiten setzen → „Speichern & aktivieren“.
4. Im Admin-Web erscheint das Gerät mit Live-Status (online/gesperrt/frei).

## 5. Fernsteuerung (Geräte-Detailseite)

| Aktion            | Wirkung |
|-------------------|---------|
| **Entsperren**    | temporär für n Minuten entsperren |
| **Sofort sperren**| Entsperrung aufheben, sofort sperren – gilt bis zum nächsten Tag (dann startet auch das Tageslimit neu) oder bis Entsperren/Eltern-PIN. Die strenge Sperre nach einem Löschversuch bleibt bis zur Freigabe durch Eltern. |
| **PIN setzen**    | neuen PIN vergeben (neue signierte Config) |
| **Sperrzeiten**   | Fenster hinzufügen/entfernen |
| **Update**        | Self-Update auf die konfigurierte Version anstoßen |
| **App entfernen** | Admin-Wipe: stille Deinstallation ohne PIN (nur ihr) |

Befehle sind signiert und laufen nach 1 h ab (Replay-Schutz). Das Gerät pollt ~alle 60 s.

## 6. Updates ausrollen

1. Neue Installer-Version bauen (CI, Tag `vX.Y.Z`) und unter einer HTTPS-URL bereitstellen.
2. `FROHLOCK_UPDATE_VERSION` / `FROHLOCK_UPDATE_URL` / `FROHLOCK_UPDATE_SHA256` am Server setzen.
   Das Manifest wird automatisch signiert (`{version}|{sha256}`).
3. Auf dem Gerät **„Update“** auslösen (oder es wird beim nächsten Poll gezogen).
   Der Client installiert nur bei gültiger Signatur **und** passendem SHA-256.

## 7. Ausnahme für ein Programm (Windows ab 0.31.0)

Geräte-Detailseite eines Laptops → Karte **„🧩 Ausnahme für ein Programm“**. Je Eintrag: Name,
Programmdatei (nur Dateiname, z. B. `Evoluzzer.exe`), optional erlaubte SHA-256-Prüfsummen, Art:

| Art | Wirkung am Laptop |
|-----|-------------------|
| Noch nicht festgelegt | nichts (vorgemerkt) |
| Auch während der Sperre erlaubt | Sperrbildschirm zeigt „🎮 … spielen“; nur dieses Programm ist erreichbar, die Sperre liegt dahinter. Außerhalb der Sperre zählt es normal. |
| Zählt nicht zur Bildschirmzeit | frei nutzbar, Tageslimit/gemeinsames Konto sinken nicht; in der Sperre gesperrt |
| Immer frei – auch in der Sperre, und zählt nie | beides |

- Knopf **„🎮 Evoluzzer eintragen“** füllt Name, Datei, Prüfsumme der Godot-4.7.2-Laufzeit und Art „beides“ aus → „Speichern“.
- Erlaubt nur, wenn das Tageslimit aufgebraucht ist (auch gemeinsames Konto) – **nicht** im Sperrfenster des Zeitplans (Schlafenszeit), **nicht** bei „Sofort sperren“ und **nie** bei Auszeit,
  Manipulation, Fail-Secure (Zeit zu alt). PIN, Fern-Entsperren, Master-PIN wie bisher.
- Erkennung im Dienst: voller Pfad des Vordergrund-Prozesses, Dateiname = Programmdatei, SHA-256 in der Liste,
  Datei nach dem Start nicht ausgetauscht, keine Godot-Startparameter, die ein anderes Spiel laden. Ablehnungen stehen
  als `AUSNAHME`-Zeile im Geräte-Protokoll.
- Neue Spielversion mit anderer Godot-Laufzeit = andere Prüfsumme → in der Karte ergänzen, sonst bleibt alles gesperrt.
- Die nicht gezählte Zeit erscheint im Admin („🎮 Evoluzzer: 45 Min (zählt nicht)“) und fließt nicht ins gemeinsame Tageskonto.
