# BUILD – Bauen, Testen, Installer erzeugen

## Voraussetzungen
- .NET SDK 8.0
- Python 3.11+ (für den Server / dessen Tests)
- Zum Installer-Bau: **Windows** + Inno Setup 6 (die CI macht das automatisch)

## Tests lokal

**Client (Core-Logik, inkl. Cross-Language-Signaturprüfung):**
```bash
dotnet test client/FrohLock.Tests/FrohLock.Tests.csproj -c Release
# erwartet: 14/14 grün
```

**Server:**
```bash
cd server
python3 -m venv .venv && . .venv/bin/activate
pip install -r requirements.txt
PYTHONPATH=. python -m pytest tests/ -q
# erwartet: 3/3 grün (erzeugt bei Bedarf ein Dev-Schlüsselpaar)
```

## Client bauen (Entwicklung)
```bash
dotnet build FrohLock.sln -c Release
```
> Hinweis: Die WPF-Projekte (Agent/Setup) bauen auf macOS/Linux nur mit
> `EnableWindowsTargeting=true` (bereits gesetzt); **ausführen** lassen sie sich nur unter Windows.

## Installer bauen (Windows)
```powershell
# 1) (optional) produktiven Public Key einbetten
pwsh tools/inject-public-key.ps1 -PemPath prod_public.pem

# 2) die drei Programme als Single-File publishen
pwsh tools/publish-client.ps1

# 3) Installer kompilieren
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" /DAppVersion=0.1.0 installer/frohlock.iss
# Ergebnis: installer/Output/FrohLockSetup-0.1.0.exe
```

## CI (GitHub Actions)
`.github/workflows/build.yml`:
1. **test-dotnet** (Ubuntu) + **test-server** (Ubuntu)
2. **build-installer** (Windows, nur bei Tag `v*` oder manuell): publisht den Client,
   kompiliert den Installer, berechnet SHA-256, lädt das Artefakt hoch und hängt es
   bei Tags an ein Release.

Produktiven Public Key als Repo-Secret `SIGNING_PUBLIC_KEY` hinterlegen → wird automatisch
in den Client injiziert.

## Vor dem echten Einsatz (offene Punkte)

Diese Dinge brauchen einen **Windows-Rechner bzw. produktive Entscheidungen** und wurden
bewusst nicht auf dem Build-Host (macOS) simuliert:

1. **End-to-End-Test auf echtem Windows 10 LTSC** (VM oder Gerät): Installation, Sperre zur
   Zeit, PIN-Entsperrung, Uhr-Verstellen wirkungslos, Watchdog, Fernbefehle, Deinstall-PIN.
2. **Code-Signing-Zertifikat** für Installer + EXEs (sonst SmartScreen-Warnung). Danach den
   Signier-Schritt in die CI aufnehmen.
3. **Produktives Schlüsselpaar** erzeugen und einbetten (nicht den Dev-Key verwenden).
4. **Server produktiv** hinter HTTPS + TLS-Pins (siehe ADMIN.md).
5. Optional: Task-Manager-Sperre per Gruppenrichtlinie für das Kinderkonto ausrollen.
