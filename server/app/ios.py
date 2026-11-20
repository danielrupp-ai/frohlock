"""iPhone ohne App Store: Gerät per Profil anmelden (UDID), Entwickler-Build über einen Link installieren.

Ablauf: Admin erstellt Code → Link /ios/<code> am iPhone öffnen → Profil „iPhone anmelden“ laden und installieren
(iOS schickt die UDID an /ios/<code>/udid) → Daniels Mac trägt die UDID bei Apple ein und baut die App neu
(ios/tools/release.py) → dieselbe Seite zeigt dann „App installieren“ → koppeln über frohlock://koppeln?code=.
Dateien in download/ios: FrohLock.ipa, version.txt, udids.txt (UDIDs, die im aktuellen Build stecken).
"""
from __future__ import annotations

import os
import re
import time
import uuid
from xml.sax.saxutils import escape

from . import db
from .config import settings

UDID_RE = re.compile(r"^[0-9A-Fa-f]{8}-?[0-9A-Fa-f]{16}$|^[0-9A-Fa-f]{40}$")
BUNDLE_ID = "de.froehlichdienste.frohlock"


def base_dir() -> str:
    return os.path.join(settings.download_dir, "ios")


def ipa_path() -> str:
    return os.path.join(base_dir(), "FrohLock.ipa")


def _read(name: str) -> str:
    try:
        with open(os.path.join(base_dir(), name), encoding="utf-8") as f:
            return f.read().strip()
    except OSError:
        return ""


def version() -> str:
    return _read("version.txt") if os.path.exists(ipa_path()) else ""


def built_udids() -> set[str]:
    return {u.strip().upper() for u in _read("udids.txt").splitlines() if u.strip()}


def is_built_for(udid: str) -> bool:
    return bool(udid) and udid.upper() in built_udids()


def public(path: str) -> str:
    return settings.public_base_url.rstrip("/") + path


def page_link(code: str) -> str:
    return public(f"/ios/{code}")


def install_url() -> str:
    from urllib.parse import quote
    return "itms-services://?action=download-manifest&url=" + quote(public("/ios/manifest.plist"), safe="")


def enroll_profile(code: str) -> bytes:
    """Apple „Profile Service“: iOS schickt nach dem Installieren UDID + Modell an die URL (nichts wird eingerichtet)."""
    return f"""<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>PayloadContent</key><dict>
    <key>URL</key><string>{escape(public(f"/ios/{code}/udid"))}</string>
    <key>DeviceAttributes</key><array><string>UDID</string><string>PRODUCT</string><string>VERSION</string></array>
  </dict>
  <key>PayloadOrganization</key><string>FrohLock · Fröhlich Dienste</string>
  <key>PayloadDisplayName</key><string>FrohLock – iPhone anmelden</string>
  <key>PayloadDescription</key><string>Meldet nur die Gerätenummer an FrohLock, damit die App für dieses iPhone gebaut werden kann. Es wird nichts eingerichtet.</string>
  <key>PayloadVersion</key><integer>1</integer>
  <key>PayloadUUID</key><string>{uuid.uuid4()}</string>
  <key>PayloadIdentifier</key><string>{BUNDLE_ID}.anmelden</string>
  <key>PayloadType</key><string>Profile Service</string>
</dict></plist>""".encode()


def parse_device_attributes(body: bytes) -> dict[str, str]:
    """Die Antwort ist ein (signierter) PKCS#7-Umschlag um eine Plist – die Plist steht im Klartext darin."""
    text = body.decode("latin-1", errors="ignore")
    out: dict[str, str] = {}
    for key in ("UDID", "PRODUCT", "VERSION", "SERIAL", "IMEI"):
        m = re.search(rf"<key>{key}</key>\s*<string>([^<]{{1,80}})</string>", text)
        if m:
            out[key] = m.group(1).strip()
    return out


def remember(code: str, attrs: dict[str, str]) -> str | None:
    udid = attrs.get("UDID", "").strip()
    if not UDID_RE.match(udid):
        return None
    now = int(time.time())
    with db.tx() as c:
        c.execute("""INSERT INTO ios_enrollments(udid, code, product, os_version, created_at)
                     VALUES (?,?,?,?,?)
                     ON CONFLICT(udid) DO UPDATE SET code=excluded.code, product=excluded.product,
                       os_version=excluded.os_version, created_at=excluded.created_at""",
                  (udid.upper(), code, attrs.get("PRODUCT", ""), attrs.get("VERSION", ""), now))
    db.audit("ios", "enroll", None, f"{code} / {attrs.get('PRODUCT', '?')} iOS-Build {attrs.get('VERSION', '?')}")
    return udid.upper()


def enrollments(code: str | None = None) -> list[dict]:
    rows = db.query("SELECT * FROM ios_enrollments WHERE code=? ORDER BY created_at DESC", (code,)) if code else \
        db.query("SELECT * FROM ios_enrollments ORDER BY created_at DESC")
    built = built_udids()
    return [dict(r) | {"built": r["udid"] in built} for r in rows]


def waiting() -> list[dict]:
    """Angemeldete iPhones, die im aktuellen Build noch fehlen (Daniels Mac muss neu bauen)."""
    return [e for e in enrollments() if not e["built"]]


def manifest_plist() -> bytes:
    v = escape(version() or "1.0.0")
    return f"""<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict><key>items</key><array><dict>
  <key>assets</key><array>
    <dict><key>kind</key><string>software-package</string><key>url</key><string>{escape(public("/ios/FrohLock.ipa"))}?v={v}</string></dict>
    <dict><key>kind</key><string>display-image</string><key>url</key><string>{escape(public("/ios/icon.png"))}</string></dict>
    <dict><key>kind</key><string>full-size-image</string><key>url</key><string>{escape(public("/ios/icon.png"))}</string></dict>
  </array>
  <key>metadata</key><dict>
    <key>bundle-identifier</key><string>{BUNDLE_ID}</string>
    <key>bundle-version</key><string>{v}</string>
    <key>kind</key><string>software</string>
    <key>title</key><string>FrohLock</string>
  </dict>
</dict></array></dict></plist>""".encode()
