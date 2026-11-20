#!/usr/bin/env python3
"""FrohLock iOS bauen und über den Link bereitstellen (läuft auf Daniels Mac).

  python3 ios/tools/release.py            # neue iPhones vom Server holen, bei Apple eintragen, bauen, hochladen
  python3 ios/tools/release.py --no-upload
  python3 ios/tools/release.py --version 1.0.1

Ablauf: angemeldete UDIDs (Tabelle ios_enrollments) per SSH lesen → fehlende bei Apple registrieren →
Entwickler-Profile für App + 2 Erweiterungen neu erzeugen (enthalten ALLE Geräte) → Archiv + Export (Development) →
FrohLock.ipa, version.txt, udids.txt, icon.png nach data/download/ios auf dem Server.
Voraussetzung (einmalig): Family Controls (Development) + App Group sind an den drei App-IDs aktiv – siehe README.
"""
import argparse
import base64
import json
import os
import plistlib
import re
import shutil
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(__file__))
import asc  # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
HOST = "deploy@46.224.7.46"
REMOTE_DIR = "~/apps/frohlock/data/download/ios"
TEAM = "33D4MF58H5"
DEV = "/Applications/Xcode.app/Contents/Developer"
TARGETS = {  # Bundle-ID -> Profilname (muss zu project.yml passen)
    "de.froehlichdienste.frohlock": "FrohLock Dev App",
    "de.froehlichdienste.frohlock.monitor": "FrohLock Dev Monitor",
    "de.froehlichdienste.frohlock.shield": "FrohLock Dev Shield",
}
PROFILE_DIR = os.path.expanduser("~/Library/MobileDevice/Provisioning Profiles")


def sh(cmd, **kw):
    print("$", cmd if isinstance(cmd, str) else " ".join(cmd))
    env = dict(os.environ, DEVELOPER_DIR=DEV)
    return subprocess.run(cmd, shell=isinstance(cmd, str), check=True, env=env, **kw)


def server_enrollments():
    py = ("import sqlite3,json;c=sqlite3.connect('/data/app.db');c.row_factory=sqlite3.Row;"
          "print(json.dumps([dict(r) for r in c.execute('SELECT * FROM ios_enrollments')]))")
    out = subprocess.run(["ssh", "-o", "BatchMode=yes", HOST, f'docker exec frohlock python -c "{py}"'],
                         check=True, capture_output=True, text=True).stdout
    return json.loads(out.strip() or "[]")


def apple_devices():
    return [d for d in asc.call("GET", "/v1/devices?limit=200&filter[platform]=IOS")["data"]]


def register_devices(enrolled):
    have = {d["attributes"]["udid"].upper(): d for d in apple_devices()}
    for e in enrolled:
        u = e["udid"].upper()
        if u in have:
            continue
        name = f"FrohLock {e.get('product') or 'iPhone'} {e.get('code') or ''}".strip()[:50]
        print(f"→ Registriere {u} ({name})")
        asc.call("POST", "/v1/devices", {"data": {"type": "devices", "attributes": {
            "name": name, "udid": e["udid"], "platform": "IOS"}}})
    return [d for d in apple_devices() if d["attributes"]["status"] == "ENABLED"
            and d["attributes"]["deviceClass"] in ("IPHONE", "IPAD", "IPOD")]


def local_dev_cert_id():
    pem = subprocess.run(["security", "find-certificate", "-a", "-c", "Apple Development", "-p"],
                         capture_output=True, text=True).stdout
    serials = set()
    for block in re.findall(r"-----BEGIN CERTIFICATE-----.*?-----END CERTIFICATE-----", pem, re.S):
        s = subprocess.run(["openssl", "x509", "-noout", "-serial"], input=block, capture_output=True, text=True).stdout
        serials.add(s.strip().split("=")[-1].lstrip("0").upper())
    idents = subprocess.run(["security", "find-identity", "-v", "-p", "codesigning"], capture_output=True, text=True).stdout
    for c in asc.call("GET", "/v1/certificates?limit=50")["data"]:
        a = c["attributes"]
        if a["certificateType"] in ("DEVELOPMENT", "IOS_DEVELOPMENT") and a["serialNumber"].lstrip("0").upper() in serials:
            if a["name"].split(":")[0] in idents:
                return c["id"]
    raise SystemExit("Kein Apple-Development-Zertifikat mit privatem Schlüssel im Schlüsselbund gefunden.")


def make_profiles(devices, cert_id):
    os.makedirs(PROFILE_DIR, exist_ok=True)
    bundle = {}
    for ident in TARGETS:
        r = asc.call("GET", f"/v1/bundleIds?filter[identifier]={ident}")
        bundle[ident] = next(d["id"] for d in r["data"] if d["attributes"]["identifier"] == ident)
    existing = asc.call("GET", "/v1/profiles?limit=200&filter[profileType]=IOS_APP_DEVELOPMENT")["data"]
    for ident, name in TARGETS.items():
        for p in existing:
            if p["attributes"]["name"] == name:
                asc.call("DELETE", f"/v1/profiles/{p['id']}")
        r = asc.call("POST", "/v1/profiles", {"data": {
            "type": "profiles", "attributes": {"name": name, "profileType": "IOS_APP_DEVELOPMENT"},
            "relationships": {
                "bundleId": {"data": {"type": "bundleIds", "id": bundle[ident]}},
                "certificates": {"data": [{"type": "certificates", "id": cert_id}]},
                "devices": {"data": [{"type": "devices", "id": d["id"]} for d in devices]}}}})
        content = base64.b64decode(r["data"]["attributes"]["profileContent"])
        uuid = r["data"]["attributes"]["uuid"]
        check_entitlements(name, content)
        with open(os.path.join(PROFILE_DIR, f"{uuid}.mobileprovision"), "wb") as f:
            f.write(content)
        print(f"✓ Profil „{name}“ ({len(devices)} Geräte)")


def check_entitlements(name, content):
    start, end = content.find(b"<?xml"), content.rfind(b"</plist>") + 8
    ent = plistlib.loads(content[start:end]).get("Entitlements", {})
    missing = []
    if not ent.get("com.apple.developer.family-controls"):
        missing.append("Family Controls (Development)")
    if not ent.get("com.apple.security.application-groups"):
        missing.append("App Group group.de.froehlichdienste.frohlock")
    if missing:
        raise SystemExit(f"⚠ Profil „{name}“ fehlt: {', '.join(missing)}. Einmalig im Apple-Entwicklerportal an der "
                         f"App-ID einschalten (siehe ios/README.md) und erneut starten.")


def build(version, build_no):
    os.chdir(ROOT)
    sh(["xcodegen", "generate"])
    shutil.rmtree("build/FrohLock.xcarchive", ignore_errors=True)
    shutil.rmtree("build/export", ignore_errors=True)
    sh(["xcodebuild", "-project", "FrohLock.xcodeproj", "-scheme", "FrohLock", "-configuration", "Release",
        "-destination", "generic/platform=iOS", "-archivePath", "build/FrohLock.xcarchive", "archive",
        f"MARKETING_VERSION={version}", f"CURRENT_PROJECT_VERSION={build_no}", "-quiet"])
    opts = {"method": "debugging", "teamID": TEAM, "signingStyle": "manual", "compileBitcode": False,
            "thinning": "<none>", "stripSwiftSymbols": True,
            "signingCertificate": "Apple Development", "provisioningProfiles": TARGETS}
    with open("build/ExportOptions.plist", "wb") as f:
        plistlib.dump(opts, f)
    sh(["xcodebuild", "-exportArchive", "-archivePath", "build/FrohLock.xcarchive", "-exportPath", "build/export",
        "-exportOptionsPlist", "build/ExportOptions.plist", "-quiet"])
    ipa = os.path.join(ROOT, "build/export/FrohLock.ipa")
    if not os.path.exists(ipa):
        raise SystemExit("Export ohne IPA")
    return ipa


def upload(ipa, version, udids):
    stage = os.path.join(ROOT, "build/upload")
    shutil.rmtree(stage, ignore_errors=True)
    os.makedirs(stage)
    shutil.copy(ipa, os.path.join(stage, "FrohLock.ipa"))
    open(os.path.join(stage, "udids.txt"), "w").write("\n".join(sorted(udids)) + "\n")
    subprocess.run(["sips", "-z", "512", "512", os.path.join(ROOT, "App/Assets.xcassets/AppIcon.appiconset/icon-1024.png"),
                    "--out", os.path.join(stage, "icon.png")], check=True, capture_output=True)
    sh(["ssh", HOST, f"mkdir -p {REMOTE_DIR}"])
    sh(["scp", "-q", os.path.join(stage, "FrohLock.ipa"), f"{HOST}:{REMOTE_DIR}/FrohLock.ipa.tmp"])
    sh(["scp", "-q", os.path.join(stage, "udids.txt"), os.path.join(stage, "icon.png"), f"{HOST}:{REMOTE_DIR}/"])
    # Version zuletzt: erst dann zeigen Seite/App die neue Version an.
    sh(["ssh", HOST, f"cd {REMOTE_DIR} && mv FrohLock.ipa.tmp FrohLock.ipa && printf %s {version} > version.txt && ls -l"])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--version", default="")
    ap.add_argument("--no-upload", action="store_true")
    a = ap.parse_args()
    version = a.version or re.search(r'MARKETING_VERSION: "([^"]+)"', open(os.path.join(ROOT, "project.yml")).read()).group(1)
    enrolled = server_enrollments()
    print(f"Angemeldete iPhones auf dem Server: {len(enrolled)}")
    devices = register_devices(enrolled)
    make_profiles(devices, local_dev_cert_id())
    ipa = build(version, time.strftime("%Y%m%d%H%M"))
    print(f"✓ {ipa} ({os.path.getsize(ipa) // 1024} KB)")
    if not a.no_upload:
        upload(ipa, version, [d["attributes"]["udid"].upper() for d in devices])
        print(f"✓ Bereit: https://frohlock.froehlichdienste.de/ios (Version {version})")


if __name__ == "__main__":
    main()
