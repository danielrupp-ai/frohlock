# FrohLock – Bildschirmzeit-Schutz für Kinder (Windows)

FrohLock sperrt einen Windows-Laptop in frei definierten Zeitfenstern **vollständig**
(Vollbild-Overlay, keine Bedienung möglich). Entsperrt wird nur mit einem **PIN**, den
die Eltern selbst vergeben. Über eine **Server-Brücke** können Administratoren aus der
Ferne entsperren, umkonfigurieren, updaten und die App wieder entfernen.

> **Ehrlich gesagt:** Gegen Kinder ohne Administratorrechte ist der Schutz sehr robust.
> Eine reine Software kann jedoch nicht *jeden* Umgehungsweg eines technisch versierten
> Erwachsenen ausschließen (Admin-Rechte, USB-/Safe-Mode-Boot, Festplattenausbau).
> Lies dazu unbedingt **[docs/SICHERHEIT.md](docs/SICHERHEIT.md)** inkl. Härtungs-Checkliste.

## Zielplattform
- Windows 10 Enterprise LTSC 64-bit (und neuer), auch ältere Builds ab 1809
- Läuft sparsam auf schwacher Hardware (~2,2 GHz), keine Runtime-Nachinstallation nötig
- Installation per einer herunterladbaren Setup-.exe

## Architektur

```
        ┌─────────────────────────── Kinder-Laptop ───────────────────────────┐
        │                                                                       │
        │  FrohLockService (Windows-Dienst, LocalSystem)   ← das "Gehirn"       │
        │   • Zeitplan-Enforcement (Kernfunktion)                               │
        │   • Vertrauenszeit aus dem Internet (manipulationssicher)             │
        │   • PIN-Prüfung, Named-Pipe-IPC                                       │
        │   • Watchdog startet Overlay nach                                     │
        │   • pollt die Brücke, führt signierte Fernbefehle aus, Self-Update    │
        │            ▲ Named Pipe ▼                                             │
        │  FrohLockAgent (Nutzerkontext)                                        │
        │   • Vollbild-Sperr-Overlay über alle Monitore + PIN-Eingabe           │
        │   • schluckt Umgehungstasten (Alt+Tab, Win, …)                        │
        │                                                                       │
        │  FrohLockSetup (Eltern, elevated)                                     │
        │   • Kopplung, PIN vergeben, Sperrzeiten                               │
        └───────────────────────────────┬───────────────────────────────────────┘
                                         │ HTTPS (TLS-Pinning), Antworten RSA-signiert
                                         ▼
                       FrohLock-Brücke (FastAPI-Server + Admin-Web)
                        • Geräteverwaltung, Kopplungscodes
                        • Zeitplan/PIN, Fernbefehle (entsperren/sperren/Wipe/Update)
                        • signiert Config & Befehle mit dem privaten Schlüssel
```

Sicherheitskern: **Alle** sicherheitsrelevanten Daten (Config, Fernbefehle) sind
server-signiert (RSA-PSS/SHA-256). Der Client vertraut nur dem fest eingebauten
Public Key – lokal gefälschte Config/Befehle sind wirkungslos. Der private Schlüssel
liegt ausschließlich auf dem Server.

## Projektstruktur
```
client/FrohLock.Core     gemeinsame Logik (Zeit, Krypto, Config, Server-Client)  – getestet
client/FrohLock.Service  Windows-Dienst (Enforcement)
client/FrohLock.Agent    WPF-Overlay im Nutzerkontext
client/FrohLock.Setup    WPF-Eltern-Einrichtung (+ --verify-pin für Deinstall-Schutz)
client/FrohLock.Tests    xUnit-Tests (14/14)
server/                  FastAPI-Brücke + Admin-Web (Tests 3/3)
installer/frohlock.iss   Inno-Setup-Installer
tools/                   Build-/Key-Skripte
docs/                    SICHERHEIT / INSTALL / ADMIN / BUILD
```

## Schnellstart
- **Bauen & Installer:** [docs/BUILD.md](docs/BUILD.md)
- **Server betreiben:** [docs/ADMIN.md](docs/ADMIN.md)
- **Auf dem Kinder-Laptop installieren:** [docs/INSTALL.md](docs/INSTALL.md)

## Status
Vollständig implementiert; Kernlogik und Server lokal getestet (17 Tests grün).
Der Windows-Client wird per GitHub-Actions-CI (Windows-Runner) zur fertigen
Setup-.exe gebaut. Offene Punkte siehe [docs/BUILD.md](docs/BUILD.md) → „Vor dem echten Einsatz".
