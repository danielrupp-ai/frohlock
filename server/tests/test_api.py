"""End-to-End-Tests der Brücke inkl. Signaturprüfung mit dem Public Key."""
import base64
import os
import tempfile

os.environ["FROHLOCK_DB"] = os.path.join(tempfile.gettempdir(), f"frohlock_test_{os.getpid()}.db")
os.environ["FROHLOCK_ADMIN_PASSWORD"] = "test-pass"
os.environ["FROHLOCK_WATCHDOG"] = "0"

import pytest
from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.asymmetric import padding
from cryptography.hazmat.primitives.serialization import load_pem_public_key
from fastapi.testclient import TestClient

from app.config import settings
from app.main import create_app

PUB_PATH = os.path.join(os.path.dirname(__file__), "..", "..", "tools", "dev-keys", "signing_public.pem")


@pytest.fixture()
def client():
    if os.path.exists(settings.db_path):
        os.remove(settings.db_path)
    # DB-Modul hält eine offene Verbindung -> für sauberen Test neu importieren.
    import importlib
    import app.db as dbmod
    importlib.reload(dbmod)
    app = create_app()
    return TestClient(app)


def _verify(pub, payload_b64, sig_b64):
    payload = base64.b64decode(payload_b64)
    sig = base64.b64decode(sig_b64)
    pub.verify(sig, payload, padding.PSS(mgf=padding.MGF1(hashes.SHA256()), salt_length=32), hashes.SHA256())
    return payload


def _admin_login(client):
    r = client.post("/login", data={"username": "admin", "password": "test-pass"}, follow_redirects=False)
    assert r.status_code == 303


def test_full_flow(client):
    with open(PUB_PATH, "rb") as fh:
        pub = load_pem_public_key(fh.read())

    _admin_login(client)

    # Kopplungscode erzeugen
    client.post("/admin/pairing", data={"device_name": "Test-Laptop"}, follow_redirects=False)
    import app.db as dbmod
    code = dbmod.query("SELECT code FROM pairing_codes")[0]["code"]

    # Gerät registriert sich
    r = client.post("/devices/register", json={"pairingCode": code, "deviceName": "Test-Laptop"})
    assert r.status_code == 200
    dev = r.json()
    device_id, token = dev["deviceId"], dev["token"]
    auth = {"Authorization": f"Bearer {token}"}

    # Ohne Config -> 404
    assert client.get(f"/devices/{device_id}/config?have=0", headers=auth).status_code == 404

    # Setup (Eltern) legt Entwurf mit Fenster + PIN-Hash an
    draft = {
        "windows": [{"days": [1, 2, 3], "startMinute": 1260, "endMinute": 420, "enabled": True}],
        "pinHash": "aGFzaA==", "pinSalt": "c2FsdA==", "pinIterations": 210000,
        "unlockGraceMinutes": 45,
    }
    r = client.post(f"/devices/{device_id}/config-draft", json=draft, headers=auth)
    assert r.status_code == 200 and r.json()["configVersion"] == 1

    # Signierte Config abrufen und Signatur prüfen
    import json
    r = client.get(f"/devices/{device_id}/config?have=0", headers=auth)
    assert r.status_code == 200
    env = r.json()
    payload = _verify(pub, env["payloadBase64"], env["signatureBase64"])
    cfg = json.loads(payload)
    assert cfg["deviceId"] == device_id
    assert cfg["configVersion"] == 1
    assert cfg["windows"][0]["startMinute"] == 1260
    assert cfg["unlockGraceMinutes"] == 45

    # have>=version -> 304
    assert client.get(f"/devices/{device_id}/config?have=1", headers=auth).status_code == 304

    # Heartbeat
    hb = {"deviceId": device_id, "appVersion": "0.1.0", "configVersion": 1, "currentlyLocked": True,
          "lockReason": "Sperrfenster", "trustedTimeAgeSeconds": 12}
    assert client.post(f"/devices/{device_id}/heartbeat", json=hb, headers=auth).status_code == 200

    # Admin stellt Fern-Entsperrbefehl in die Warteschlange
    client.post(f"/admin/devices/{device_id}/command", data={"kind": "unlock", "minutes": 30},
                follow_redirects=False)
    r = client.get(f"/devices/{device_id}/commands", headers=auth)
    assert r.status_code == 200
    cmds = r.json()
    assert len(cmds) == 1
    cmd_payload = _verify(pub, cmds[0]["payloadBase64"], cmds[0]["signatureBase64"])
    cmd = json.loads(cmd_payload)
    assert cmd["type"] == 0  # unlock
    assert cmd["unlockMinutes"] == 30
    assert cmd["deviceId"] == device_id


def test_landing_public_and_admin_moved(client):
    # Öffentliche Landingpage ohne Login
    r = client.get("/")
    assert r.status_code == 200
    assert "herunterladen" in r.text.lower() or "vorbereitet" in r.text.lower()
    # Admin liegt jetzt unter /admin und verlangt Login (Redirect)
    r2 = client.get("/admin", follow_redirects=False)
    assert r2.status_code == 303
    # Download ohne bereitgestellte Datei -> 404 (sauber, kein 500)
    assert client.get("/download").status_code == 404


def test_foreign_device_rejected(client):
    _admin_login(client)
    client.post("/admin/pairing", data={"device_name": "A"}, follow_redirects=False)
    import app.db as dbmod
    code = dbmod.query("SELECT code FROM pairing_codes")[0]["code"]
    dev = client.post("/devices/register", json={"pairingCode": code, "deviceName": "A"}).json()
    auth = {"Authorization": f"Bearer {dev['token']}"}
    # Zugriff auf fremde device_id
    assert client.get("/devices/some-other-id/config", headers=auth).status_code == 403
    # Ohne Token
    assert client.get(f"/devices/{dev['deviceId']}/config").status_code == 401


def test_pin_reset_by_email_flow(client):
    _admin_login(client)
    client.post("/admin/pairing", data={"device_name": "Reset-Laptop"}, follow_redirects=False)
    import app.db as dbmod
    code = dbmod.query("SELECT code FROM pairing_codes")[0]["code"]
    dev = client.post("/devices/register", json={"pairingCode": code, "deviceName": "Reset-Laptop"}).json()
    device_id, token = dev["deviceId"], dev["token"]
    auth = {"Authorization": f"Bearer {token}"}

    # Setup legt Config + Reset-E-Mail an.
    draft = {
        "windows": [{"days": [], "startMinute": 1260, "endMinute": 420, "enabled": True}],
        "pinHash": "YWx0", "pinSalt": "c2FsdA==", "pinIterations": 210000,
        "resetEmail": "Eltern@example.de",
    }
    assert client.post(f"/devices/{device_id}/config-draft", json=draft, headers=auth).status_code == 200
    v1 = configbuilder_version(device_id)

    # /forgot -> Token wird angelegt (case-insensitive)
    r = client.post("/forgot", data={"email": "eltern@example.de"}, follow_redirects=False)
    assert r.status_code == 200
    trow = dbmod.query("SELECT token FROM reset_tokens WHERE device_id=?", (device_id,))
    assert len(trow) == 1
    tok = trow[0]["token"]

    # Reset-Seite erreichbar, dann neuen PIN setzen
    assert client.get(f"/reset/{tok}").status_code == 200
    r = client.post(f"/reset/{tok}", data={"pin": "4321"}, follow_redirects=False)
    assert r.status_code == 200

    # Config-Version wurde erhöht, PIN-Hash hat sich geändert
    v2 = configbuilder_version(device_id)
    assert v2 > v1
    new_cfg = client.get(f"/devices/{device_id}/config?have=0", headers=auth).json()
    import base64, json
    cfg = json.loads(base64.b64decode(new_cfg["payloadBase64"]))
    assert cfg["pinHash"] != "YWx0"

    # Token ist verbraucht -> zweiter Versuch ungültig (kein 500)
    assert client.get(f"/reset/{tok}").status_code == 200  # zeigt "ungültig"-Seite


def test_usage_tracking_and_device_page(client):
    _admin_login(client)
    client.post("/admin/pairing", data={"device_name": "Nutzungs-Laptop"}, follow_redirects=False)
    import app.db as dbmod
    code = dbmod.query("SELECT code FROM pairing_codes")[0]["code"]
    dev = client.post("/devices/register", json={"pairingCode": code, "deviceName": "Nutzungs-Laptop"}).json()
    device_id, token = dev["deviceId"], dev["token"]
    auth = {"Authorization": f"Bearer {token}"}

    # Config mit Tageslimit anlegen (über Admin-Einstellungen).
    client.post(f"/devices/{device_id}/config-draft",
                json={"windows": [], "pinHash": "eA==", "pinSalt": "eA==", "dailyBudgetMinutes": 120},
                headers=auth)

    # Heartbeat meldet Nutzung.
    hb = {"deviceId": device_id, "appVersion": "0.4.0", "configVersion": 1,
          "currentlyLocked": False, "usageMinutesToday": 47, "dailyBudgetMinutes": 120,
          "usageDay": "2026-09-14"}
    assert client.post(f"/devices/{device_id}/heartbeat", json=hb, headers=auth).status_code == 200

    # Historie gespeichert (max je Tag)
    row = dbmod.query_one("SELECT minutes FROM usage_daily WHERE device_id=? AND day=?", (device_id, "2026-09-14"))
    assert row and row["minutes"] == 47

    # Admin-Detailseite rendert (kein Template-Fehler) und zeigt die Nutzung
    r = client.get(f"/admin/devices/{device_id}?day=2026-09-14")
    assert r.status_code == 200
    assert "47 Min" in r.text
    assert "nur die Gesamtzeit" in r.text  # Alt-Tag ohne Uhrzeiten
    assert "Tageslimit" in r.text


def configbuilder_version(device_id):
    from app.configbuilder import current_version
    return current_version(device_id)


def test_used_pairing_code_rejected(client):
    _admin_login(client)
    client.post("/admin/pairing", data={"device_name": "A"}, follow_redirects=False)
    import app.db as dbmod
    code = dbmod.query("SELECT code FROM pairing_codes")[0]["code"]
    assert client.post("/devices/register", json={"pairingCode": code}).status_code == 200
    # zweite Registrierung mit gleichem Code -> abgelehnt
    assert client.post("/devices/register", json={"pairingCode": code}).status_code == 400


def test_usage_timeline_in_admin(client):
    """Client meldet „von wann bis wann" -> Admin-Geräteseite zeigt die Zeitleiste."""
    _admin_login(client)
    client.post("/admin/pairing", data={"device_name": "Zeit-Laptop"}, follow_redirects=False)
    import app.db as dbmod
    code = dbmod.query("SELECT code FROM pairing_codes WHERE device_name='Zeit-Laptop'")[0]["code"]
    dev = client.post("/devices/register", json={"pairingCode": code, "deviceName": "Zeit-Laptop"}).json()
    device_id = dev["deviceId"]
    auth = {"Authorization": f"Bearer {dev['token']}"}

    hb = {"deviceId": device_id, "appVersion": "0.19.0", "usageMinutesToday": 120,
          "dailyBudgetMinutes": 120, "usageDay": "2026-10-10", "screenActive": False,
          "usageSegments": [
              {"start": "09:10", "end": "10:20", "startMinute": 550, "endMinute": 620, "minutes": 70},
              {"start": "16:00", "end": "16:50", "startMinute": 960, "endMinute": 1010, "minutes": 50},
          ]}
    assert client.post(f"/devices/{device_id}/heartbeat", json=hb, headers=auth).status_code == 200

    page = client.get(f"/admin/devices/{device_id}?day=2026-10-10").text
    assert "09:10 – 10:20" in page and "1 Std 10 Min" in page
    assert "16:00 – 16:50" in page
    assert "Zusammengerechnet: 1 Std 10 Min + 50 Min = 2 Std" in page
    assert "von 2 Std" in page
    assert "keine Zeit gezählt" in page

    # Tagesverlauf bleibt gespeichert: ein späterer Heartbeat ohne Abschnitte löscht sie nicht.
    hb2 = dict(hb, usageSegments=[])
    assert client.post(f"/devices/{device_id}/heartbeat", json=hb2, headers=auth).status_code == 200
    assert "16:00 – 16:50" in client.get(f"/admin/devices/{device_id}?day=2026-10-10").text


def test_timeout_command_stacks_and_waits_for_device(client):
    """15-Min-Auszeit: signierter Befehl, lange gültig (Laptop evtl. zu), mehrfach = mehrere Befehle, aufhebbar."""
    import base64
    import json as _json
    import time as _time
    _admin_login(client)
    client.post("/admin/pairing", data={"device_name": "Auszeit-Laptop"}, follow_redirects=False)
    import app.db as dbmod
    code = dbmod.query("SELECT code FROM pairing_codes WHERE device_name='Auszeit-Laptop'")[0]["code"]
    dev = client.post("/devices/register", json={"pairingCode": code, "deviceName": "Auszeit-Laptop"}).json()
    device_id = dev["deviceId"]
    auth = {"Authorization": f"Bearer {dev['token']}"}

    # Ein Tipp in der Geräteliste -> zurück zur Liste mit Bestätigung
    r = client.post(f"/admin/devices/{device_id}/command", data={"kind": "timeout", "back": "dashboard"},
                    follow_redirects=False)
    assert r.status_code == 303 and r.headers["location"] == "/admin?ok=auszeit"
    client.post(f"/admin/devices/{device_id}/command", data={"kind": "timeout"}, follow_redirects=False)

    page = client.get("/admin?ok=auszeit").text
    assert "15 Min Auszeit" in page and "Auszeit geschickt" in page
    assert "2× wartet auf den Laptop" in page

    # Alter Client (vor 0.20.0) bekommt die Auszeit noch NICHT (würde sie verschlucken)
    client.post(f"/devices/{device_id}/heartbeat", json={"appVersion": "0.19.0.0"}, headers=auth)
    assert client.get(f"/devices/{device_id}/commands", headers=auth).json() == [] or all(
        _json.loads(base64.b64decode(e["payloadBase64"]))["type"] != 7
        for e in client.get(f"/devices/{device_id}/commands", headers=auth).json())
    client.post(f"/devices/{device_id}/heartbeat", json={"appVersion": "0.20.0.0"}, headers=auth)

    cmds = client.get(f"/devices/{device_id}/commands", headers=auth).json()
    bodies = [_json.loads(base64.b64decode(e["payloadBase64"])) for e in cmds]
    timeouts = [b for b in bodies if b["type"] == 7]
    assert len(timeouts) == 2
    assert all(b["unlockMinutes"] == 15 for b in timeouts)
    assert all(b["expiresAtUnix"] - _time.time() > 6 * 86400 for b in timeouts)  # wartet tagelang

    # Gerät meldet laufende Auszeit -> Admin zeigt Countdown
    hb = {"deviceId": device_id, "appVersion": "0.20.0", "penaltySecondsRemaining": 1712, "penaltyCount": 2}
    client.post(f"/devices/{device_id}/heartbeat", json=hb, headers=auth)
    assert "noch 28:32" in client.get(f"/admin/devices/{device_id}").text

    # Aufheben nimmt auch noch nicht zugestellte Auszeiten zurück
    client.post(f"/admin/devices/{device_id}/command", data={"kind": "cleartimeout"}, follow_redirects=False)
    cmds = client.get(f"/devices/{device_id}/commands", headers=auth).json()
    types = [_json.loads(base64.b64decode(e["payloadBase64"]))["type"] for e in cmds]
    assert 7 not in types and 8 in types


def test_android_qr_pairing_copies_settings_and_updates_separately(client, tmp_path, monkeypatch):
    import json as _json
    import app.db as dbmod
    with open(PUB_PATH, "rb") as fh:
        pub = load_pem_public_key(fh.read())
    _admin_login(client)

    # Laptop mit Einstellungen (PIN + Sperrzeit) als Vorlage.
    client.post("/admin/pairing", data={"device_name": "Laptop Kind"})
    code = dbmod.query("SELECT code FROM pairing_codes")[0]["code"]
    lap = client.post("/devices/register", json={"pairingCode": code, "deviceName": "Laptop Kind"}).json()
    hdr_l = {"Authorization": f"Bearer {lap['token']}"}
    client.post(f"/devices/{lap['deviceId']}/config-draft", headers=hdr_l, json={
        "windows": [{"id": "n", "days": [], "startMinute": 1260, "endMinute": 420}],
        "pinHash": "aGFzaA==", "pinSalt": "c2FsdA==", "dailyBudgetMinutes": 90})

    # Ohne APK auf dem Server: Seite erklärt, was fehlt, aber kein QR-Code.
    monkeypatch.setattr(settings, "download_dir", str(tmp_path))
    r = client.post("/admin/android/new", data={"device_name": "Handy Kind", "template_device_id": lap["deviceId"]})
    assert r.status_code == 200 and "noch nicht auf dem Server" in r.text and "<svg" not in r.text

    # APK + Zertifikat bereitstellen -> QR-Code mit Kopplungscode + WLAN.
    (tmp_path / "FrohLock.apk").write_bytes(b"PK-fake-apk")
    (tmp_path / "android-version.txt").write_text("1.0.0")
    (tmp_path / "android-cert.txt").write_text("abc_-DEF")
    r = client.post("/admin/android/new", data={"device_name": "Handy Kind", "template_device_id": lap["deviceId"],
                                               "wifi_ssid": "Zuhause", "wifi_password": "geheim"})
    assert r.status_code == 200 and "<svg" in r.text and "Einstellungen wie" in r.text
    row = dbmod.query_one("SELECT * FROM pairing_codes WHERE platform='android' ORDER BY created_at DESC")
    qr_code = row["code"]
    from app import android
    payload = android.provisioning_payload(qr_code, "Handy Kind", "Zuhause", "geheim")
    assert payload["android.app.extra.PROVISIONING_DEVICE_ADMIN_SIGNATURE_CHECKSUM"] == "abc_-DEF"
    assert payload["android.app.extra.PROVISIONING_ADMIN_EXTRAS_BUNDLE"]["pairingCode"] == qr_code
    assert payload["android.app.extra.PROVISIONING_WIFI_SSID"] == "Zuhause"
    # WLAN-Passwort wird nirgends gespeichert.
    assert "geheim" not in _json.dumps([dict(x) for x in dbmod.query("SELECT * FROM pairing_codes")])
    assert client.get(f"/admin/android/qr/{qr_code}/status").json()["used"] is False

    # Handy koppelt sich (wie nach dem QR-Code) -> Admin-Name, Plattform, Einstellungen übernommen.
    r = client.post("/devices/register", json={"pairingCode": qr_code, "deviceName": "SM-A156B",
                                              "platform": "android", "deviceInfo": {"model": "Samsung SM-A156B"}})
    ph = r.json()
    hdr = {"Authorization": f"Bearer {ph['token']}"}
    dev = dbmod.query_one("SELECT * FROM devices WHERE id=?", (ph["deviceId"],))
    assert dev["name"] == "Handy Kind" and dev["platform"] == "android"
    assert client.get(f"/admin/android/qr/{qr_code}/status").json()["used"] is True
    env = client.get(f"/devices/{ph['deviceId']}/config", headers=hdr).json()
    cfg = _json.loads(_verify(pub, env["payloadBase64"], env["signatureBase64"]))
    assert cfg["pinHash"] == "aGFzaA==" and cfg["windows"][0]["startMinute"] == 1260 and cfg["dailyBudgetMinutes"] == 90
    assert cfg["deviceId"] == ph["deviceId"]

    # Heartbeat mit Android-Feldern -> Geräteseite zeigt Geräte-Verwaltung.
    client.post(f"/devices/{ph['deviceId']}/heartbeat", headers=hdr, json={
        "appVersion": "0.9.0", "platform": "android", "deviceOwner": True, "lockTaskActive": False,
        "deviceInfo": {"model": "Samsung SM-A156B", "android": "Android 15 (API 35)"}})
    page = client.get(f"/admin/devices/{ph['deviceId']}").text
    assert "Geräte-Verwaltung" in page and "✓ aktiv" in page and "Samsung SM-A156B" in page

    # Update: Android bekommt das Android-Ziel (1.0.0), nicht das Windows-Ziel.
    monkeypatch.setattr(settings, "update_version", "0.28.0")
    monkeypatch.setattr(settings, "update_url", "https://x/download")
    monkeypatch.setattr(settings, "update_sha256", "ff" * 32)
    cmds = client.get(f"/devices/{ph['deviceId']}/commands", headers=hdr).json()
    types = [_json.loads(base64.b64decode(c["payloadBase64"]))["type"] for c in cmds]
    assert 5 in types
    m = client.get("/updates/manifest?platform=android").json()
    assert m["version"] == "1.0.0" and m["url"].endswith("/download/android")
    _verify(pub, base64.b64encode(f"android|{m['version']}|{m['sha256']}".encode()).decode(), m["signatureBase64"])
    assert client.get("/updates/manifest").json()["version"] == "0.28.0"   # Windows unverändert

    # Aktuelle Android-Version -> kein Update-Befehl mehr.
    client.post(f"/devices/{ph['deviceId']}/heartbeat", headers=hdr, json={"appVersion": "1.0.0", "platform": "android"})
    cmds = client.get(f"/devices/{ph['deviceId']}/commands", headers=hdr).json()
    assert 5 not in [_json.loads(base64.b64decode(c["payloadBase64"]))["type"] for c in cmds]

    # Öffentlicher APK-Download.
    r = client.get("/download/android")
    assert r.status_code == 200 and r.content == b"PK-fake-apk"
    assert r.headers["content-type"] == "application/vnd.android.package-archive"
    assert client.head("/download/android").status_code == 200

    # Einstellungen nachträglich übernehmen (Geräteseite).
    r = client.post(f"/admin/devices/{lap['deviceId']}/copy-from", data={"source_id": ph["deviceId"]}, follow_redirects=False)
    assert r.status_code == 303 and "ok=kopiert" in r.headers["location"]
    assert "Android-Gerät einrichten" in client.get("/admin").text


def test_android_qr_is_compact_and_install_link_page(client, tmp_path, monkeypatch):
    import app.db as dbmod
    from app import android
    _admin_login(client)
    monkeypatch.setattr(settings, "download_dir", str(tmp_path))
    monkeypatch.setattr(settings, "public_base_url", "https://frohlock.froehlichdienste.de")
    (tmp_path / "FrohLock.apk").write_bytes(b"PK-apk")
    (tmp_path / "android-version.txt").write_text("1.0.1")
    (tmp_path / "android-cert.txt").write_text("9wdtJbgqKD97VcjuD_nUvmB91n0lX3yoFk4oPR3soYI")

    # QR muss grob bleiben, sonst scannt der Einrichtungsassistent ihn nicht vom Bildschirm.
    assert android.qr_version(android.provisioning_payload("123456", "Handy")) <= 15
    p = android.provisioning_payload("123456", "Handy")
    assert p["android.app.extra.PROVISIONING_DEVICE_ADMIN_PACKAGE_DOWNLOAD_LOCATION"].endswith("/apk")
    assert p["android.app.extra.PROVISIONING_ADMIN_EXTRAS_BUNDLE"] == {"pairingCode": "123456"}

    r = client.post("/admin/android/new", data={"device_name": "Handy Kind"})
    assert "Weg 2" in r.text and "/i/" in r.text and r.text.count("<svg") >= 2
    code = dbmod.query_one("SELECT code FROM pairing_codes WHERE platform='android'")["code"]

    page = client.get(f"/i/{code}").text          # öffentlich, ohne Login
    assert "FrohLock herunterladen" in page and f"frohlock://koppeln?code={code}" in page
    assert client.get("/apk").content == b"PK-apk"
    assert "gibt es nicht" in client.get("/i/000000").text or code == "000000"
    client.post("/devices/register", json={"pairingCode": code, "deviceName": "x", "platform": "android"})
    assert "schon benutzt" in client.get(f"/i/{code}").text


def test_android_simple_qr_has_no_code(client, tmp_path, monkeypatch):
    import app.db as dbmod
    from app import android
    _admin_login(client)
    monkeypatch.setattr(settings, "download_dir", str(tmp_path))
    (tmp_path / "FrohLock.apk").write_bytes(b"PK")
    (tmp_path / "android-cert.txt").write_text("abc")
    p = android.provisioning_payload("123456", "x", simple=True)
    assert "android.app.extra.PROVISIONING_ADMIN_EXTRAS_BUNDLE" not in p
    client.post("/admin/android/new", data={"device_name": "Handy"})
    code = dbmod.query_one("SELECT code FROM pairing_codes")["code"]
    r = client.get(f"/admin/android/qr/{code}?einfach=1")
    assert "Einfacher QR-Code" in r.text and "Zurück zum normalen Code" in r.text


def test_android_allowed_apps_default_and_admin(client):
    import json as _json
    import app.db as dbmod
    with open(PUB_PATH, "rb") as fh:
        pub = load_pem_public_key(fh.read())
    _admin_login(client)
    client.post("/admin/android/new", data={"device_name": "Handy"})
    code = dbmod.query_one("SELECT code FROM pairing_codes")["code"]
    ph = client.post("/devices/register", json={"pairingCode": code, "platform": "android"}).json()
    hdr = {"Authorization": f"Bearer {ph['token']}"}
    client.post(f"/devices/{ph['deviceId']}/config-draft", headers=hdr, json={"pinHash": "aA==", "pinSalt": "aA=="})
    env = client.get(f"/devices/{ph['deviceId']}/config", headers=hdr).json()
    cfg = _json.loads(_verify(pub, env["payloadBase64"], env["signatureBase64"]))
    assert "org.thoughtcrime.securesms" in cfg["alwaysAllowedApps"] and "org.telegram.messenger" in cfg["alwaysAllowedApps"]

    client.post(f"/devices/{ph['deviceId']}/heartbeat", headers=hdr, json={"platform": "android", "deviceOwner": False})
    page = client.get(f"/admin/devices/{ph['deviceId']}").text
    assert "Immer erlaubt" in page and 'name="m_org.telegram.messenger" checked' in page

    r = client.post(f"/admin/devices/{ph['deviceId']}/allowed-apps",
                    data={"m_org.thoughtcrime.securesms": "on", "extra": "com.whatsapp ungültig! de.schule.app"}, follow_redirects=False)
    assert r.status_code == 303
    env = client.get(f"/devices/{ph['deviceId']}/config?have=0", headers=hdr).json()
    cfg = _json.loads(_verify(pub, env["payloadBase64"], env["signatureBase64"]))
    assert cfg["alwaysAllowedApps"] == ["org.thoughtcrime.securesms", "com.whatsapp", "de.schule.app"]


def test_android_locked_but_in_use_is_shown_and_remembered(client):
    """07.11.2026: Renat war bei „gesperrt" in Telegram (immer erlaubt) – das Web zeigte nur „gesperrt".
    Jetzt: Status „gesperrt – aber in Benutzung" + nach 2 Min eine Auffälligkeit."""
    import json as _json
    import app.db as dbmod
    _admin_login(client)
    client.post("/admin/android/new", data={"device_name": "Handy"})
    code = dbmod.query_one("SELECT code FROM pairing_codes")["code"]
    ph = client.post("/devices/register", json={"pairingCode": code, "platform": "android"}).json()
    hdr = {"Authorization": f"Bearer {ph['token']}"}
    client.post(f"/devices/{ph['deviceId']}/config-draft", headers=hdr, json={"pinHash": "aA==", "pinSalt": "aA=="})
    hb = {"platform": "android", "deviceOwner": False, "currentlyLocked": True, "screenActive": True,
          "lockVisible": False, "lockReason": "Tageslimit erreicht (74/60 min)",
          "deviceInfo": {"allowedApps": ["Telegram"]}}
    assert client.post(f"/devices/{ph['deviceId']}/heartbeat", headers=hdr, json=hb).status_code == 200
    page = client.get(f"/admin/devices/{ph['deviceId']}").text
    assert "gesperrt – aber in Benutzung" in page and "Telegram ist unter „Immer erlaubt“" in page
    assert "gesperrt – aber in Benutzung" in client.get("/admin").text
    # Noch keine Auffälligkeit (erst nach 2 Min) …
    assert not dbmod.query_one("SELECT gap_alerts FROM devices WHERE id=?", (ph["deviceId"],))["gap_alerts"]
    with dbmod.tx() as c:
        c.execute("UPDATE devices SET lock_hidden_since=lock_hidden_since-200 WHERE id=?", (ph["deviceId"],))
    client.post(f"/devices/{ph['deviceId']}/heartbeat", headers=hdr, json=hb)
    alerts = _json.loads(dbmod.query_one("SELECT gap_alerts FROM devices WHERE id=?", (ph["deviceId"],))["gap_alerts"])
    assert len(alerts) == 1 and "in Benutzung" in alerts[0] and "Telegram" in alerts[0]
    # … und nicht bei jedem Heartbeat eine neue Zeile.
    client.post(f"/devices/{ph['deviceId']}/heartbeat", headers=hdr, json=hb)
    assert len(_json.loads(dbmod.query_one("SELECT gap_alerts FROM devices WHERE id=?", (ph["deviceId"],))["gap_alerts"])) == 1
    assert "Auffälligkeiten" in client.get(f"/admin/devices/{ph['deviceId']}").text
    # Sperrbildschirm wieder sichtbar → normales „gesperrt".
    hb["lockVisible"] = True
    client.post(f"/devices/{ph['deviceId']}/heartbeat", headers=hdr, json=hb)
    page = client.get(f"/admin/devices/{ph['deviceId']}").text
    assert 'class="badge inuse"' not in page and 'class="badge locked">gesperrt<' in page
    # Ohne erlaubte Apps steht es auch dabei.
    assert "keine App erlaubt" in page or "erlaubt sind" in page


def _android_device(client, name):
    _admin_login(client)
    client.post("/admin/pairing", data={"device_name": name}, follow_redirects=False)
    import app.db as dbmod
    code = dbmod.query("SELECT code FROM pairing_codes WHERE device_name=?", (name,))[0]["code"]
    dev = client.post("/devices/register", json={"pairingCode": code, "deviceName": name}).json()
    return dev["deviceId"], {"Authorization": f"Bearer {dev['token']}"}


def test_tamper_line_sends_one_email(client, monkeypatch):
    """Löschversuch im Heartbeat → sofort eine Warn-E-Mail (nicht bei jedem Heartbeat erneut)."""
    import time as _time
    from app import alerts, emailer
    from app.config import settings
    sent = []
    monkeypatch.setattr(emailer, "send", lambda to, subj, body: sent.append((to, subj, body)) or True)
    monkeypatch.setattr(alerts.threading, "Thread", lambda target, daemon: type("T", (), {"start": lambda self: target()})())
    monkeypatch.setattr(settings, "alert_email", "eltern@example.org")
    device_id, auth = _android_device(client, "Kind Handy")
    ts = _time.strftime("%Y-%m-%dT%H:%M:%S", _time.gmtime()) + ".1Z"
    line = f"{ts}\tUMGEHUNG\t⚠ LÖSCHVERSUCH: Geräteadministrator sollte abgeschaltet werden – Handy gesperrt bis Eltern-PIN"
    hb = {"deviceId": device_id, "appVersion": "1.0.7", "platform": "android", "recentAudit": line,
          "deviceInfo": {"deviceAdmin": True}}
    assert client.post(f"/devices/{device_id}/heartbeat", json=hb, headers=auth).status_code == 200
    assert client.post(f"/devices/{device_id}/heartbeat", json=hb, headers=auth).status_code == 200
    assert len(sent) == 1
    assert sent[0][0] == "eltern@example.org" and "Löschversuch" in sent[0][1] and "Kind Handy" in sent[0][1]
    # harmlose ⚠-Zeile → keine Mail
    hb2 = dict(hb, recentAudit=f"{ts}\tUMGEHUNG\t⚠ Während der Sperre war „Google“ vorne")
    client.post(f"/devices/{device_id}/heartbeat", json=hb2, headers=auth)
    assert len(sent) == 1


def test_silent_device_after_admin_off_warns_once_then_all_clear(client, monkeypatch):
    import time as _time
    from app import alerts, emailer
    from app.config import settings
    import app.db as dbmod
    sent = []
    monkeypatch.setattr(emailer, "send", lambda to, subj, body: sent.append(subj) or True)
    monkeypatch.setattr(alerts.threading, "Thread", lambda target, daemon: type("T", (), {"start": lambda self: target()})())
    monkeypatch.setattr(settings, "alert_email", "eltern@example.org")
    device_id, auth = _android_device(client, "Stilles Handy")
    hb = {"deviceId": device_id, "appVersion": "1.0.7", "platform": "android", "recentAudit": "",
          "deviceInfo": {"deviceAdmin": False}}
    client.post(f"/devices/{device_id}/heartbeat", json=hb, headers=auth)
    now = int(_time.time())
    assert alerts.check_silence(now + 300) == 0            # 5 Min still: noch nichts
    assert alerts.check_silence(now + 700) == 1            # > 10 Min nach „Schutz aus“ → wahrscheinlich gelöscht
    assert alerts.check_silence(now + 1400) == 0           # nur einmal je Funkstille
    assert any("wahrscheinlich gelöscht" in s for s in sent)
    gap = dbmod.query_one("SELECT gap_alerts FROM devices WHERE id=?", (device_id,))["gap_alerts"]
    import json as _json
    assert any("wahrscheinlich gelöscht" in g for g in _json.loads(gap))
    client.post(f"/devices/{device_id}/heartbeat", json=hb, headers=auth)   # wieder da → Entwarnung
    assert any("meldet sich wieder" in s for s in sent)


def test_silent_normal_device_waits_three_hours(client, monkeypatch):
    import time as _time
    from app import alerts, emailer
    monkeypatch.setattr(emailer, "send", lambda *a: True)
    monkeypatch.setattr(alerts.threading, "Thread", lambda target, daemon: type("T", (), {"start": lambda self: target()})())
    device_id, auth = _android_device(client, "Normales Handy")
    hb = {"deviceId": device_id, "appVersion": "1.0.7", "platform": "android", "deviceInfo": {"deviceAdmin": True}}
    client.post(f"/devices/{device_id}/heartbeat", json=hb, headers=auth)
    now = int(_time.time())
    assert alerts.check_silence(now + 3600) == 0
    assert alerts.check_silence(now + 3 * 3600 + 60) == 1


def _pair(client, name):
    import app.db as dbmod
    client.post("/admin/pairing", data={"device_name": name}, follow_redirects=False)
    code = dbmod.query_one("SELECT code FROM pairing_codes WHERE device_name=? AND used_device_id IS NULL", (name,))["code"]
    dev = client.post("/devices/register", json={"pairingCode": code, "deviceName": name}).json()
    auth = {"Authorization": f"Bearer {dev['token']}"}
    client.post(f"/devices/{dev['deviceId']}/config-draft",
                json={"windows": [], "pinHash": "eA==", "pinSalt": "eA==", "dailyBudgetMinutes": 60,
                      "dailyBudgetByWeekday": [60] * 7}, headers=auth)
    return dev["deviceId"], auth


def _shared_cmds(client, pub, device_id, auth):
    import json
    out = []
    for env in client.get(f"/devices/{device_id}/commands", headers=auth).json():
        c = json.loads(_verify(pub, env["payloadBase64"], env["signatureBase64"]))
        if c["type"] == 9:
            out.append(c)
    return out


def test_shared_daily_account_across_devices(client):
    """Daniel 19.11.2026: Renat hat 2 Std am Tag – egal ob Laptop oder Handy."""
    import json
    with open(PUB_PATH, "rb") as fh:
        pub = load_pem_public_key(fh.read())
    _admin_login(client)
    laptop, la = _pair(client, "Laptop renat")
    handy, ha = _pair(client, "renat handy")

    # Beide Geräte demselben Kind zuordnen (Schreibweise wird vereinheitlicht).
    r = client.post(f"/admin/devices/{laptop}/child", data={"child": "Renat"}, follow_redirects=False)
    assert r.status_code == 303 and "ok=konto" in r.headers["location"]
    client.post(f"/admin/devices/{handy}/child", data={"child": " renat "}, follow_redirects=False)
    import app.db as dbmod
    assert dbmod.query_one("SELECT child FROM devices WHERE id=?", (handy,))["child"] == "Renat"

    # Gemeinsames Limit 120, ohne Wochentag-Werte -> gilt für beide Geräte (neue Config-Version).
    v_before = configbuilder_version(handy)
    client.post(f"/admin/devices/{laptop}/settings",
                data={"daily_budget": "120", "unlock_grace": "60", "max_stale": "720"}, follow_redirects=False)
    assert configbuilder_version(handy) > v_before
    for did, auth in ((laptop, la), (handy, ha)):
        env = client.get(f"/devices/{did}/config?have=0", headers=auth).json()
        cfg = json.loads(_verify(pub, env["payloadBase64"], env["signatureBase64"]))
        assert cfg["dailyBudgetMinutes"] == 120 and cfg["dailyBudgetByWeekday"] == []

    # Laptop meldet 45 Min, Handy 30 Min (heute in Berlin – der Admin zeigt den heutigen Tag).
    import datetime
    from zoneinfo import ZoneInfo
    day = datetime.datetime.now(ZoneInfo("Europe/Berlin")).strftime("%Y-%m-%d")
    for did, auth, mins, ver in ((laptop, la, 45, "0.29.0.0"), (handy, ha, 30, "1.0.8")):
        hb = {"deviceId": did, "appVersion": ver, "configVersion": 3, "usageMinutesToday": mins,
              "dailyBudgetMinutes": 120, "usageDay": day}
        if did == handy:
            hb["platform"] = "android"
        assert client.post(f"/devices/{did}/heartbeat", json=hb, headers=auth).status_code == 200

    # Jedes Gerät bekommt signiert die Minuten der ANDEREN.
    lc = _shared_cmds(client, pub, laptop, la)
    hc = _shared_cmds(client, pub, handy, ha)
    assert len(lc) == 1 and lc[0]["payload"]["minutes"] == "30" and lc[0]["payload"]["day"] == day
    assert "renat handy" in lc[0]["payload"]["detail"] and lc[0]["deviceId"] == laptop
    assert len(hc) == 1 and hc[0]["payload"]["minutes"] == "45"

    # Alte Version bekommt den Befehl nicht (würde ihn nicht kennen).
    client.post(f"/devices/{laptop}/heartbeat", headers=la,
                json={"deviceId": laptop, "appVersion": "0.28.0.0", "usageMinutesToday": 46, "usageDay": day})
    assert _shared_cmds(client, pub, laptop, la) == []
    assert _shared_cmds(client, pub, handy, ha)[0]["payload"]["minutes"] == "46"

    # Admin zeigt die Summe.
    page = client.get(f"/admin/devices/{handy}").text
    assert "Gemeinsames Tageskonto" in page and "Laptop renat: 46 Min" in page
    assert "Renat zusammen" in client.get("/admin").text

    # Herauslösen: Gerät zählt wieder für sich, keine Fremd-Minuten mehr.
    client.post(f"/admin/devices/{handy}/child", data={"child": ""}, follow_redirects=False)
    assert _shared_cmds(client, pub, handy, ha) == []
    env = client.get(f"/devices/{handy}/config?have=0", headers=ha).json()
    cfg = json.loads(_verify(pub, env["payloadBase64"], env["signatureBase64"]))
    assert cfg["dailyBudgetMinutes"] == 60   # wieder das eigene Limit


def test_parent_message_waits_until_time_and_version(client):
    import json, time
    with open(PUB_PATH, "rb") as fh:
        pub = load_pem_public_key(fh.read())
    _admin_login(client)
    handy, ha = _pair(client, "renat handy")
    hb = {"deviceId": handy, "appVersion": "1.0.8", "platform": "android", "usageDay": "2026-11-19"}
    client.post(f"/devices/{handy}/heartbeat", json=hb, headers=ha)

    from app.api import enqueue_message
    enqueue_message(handy, "З днем народження!", "Текст", "Дякую! 💛", int(time.time()) + 3600)
    enqueue_message(handy, "Jetzt", "sofort", "OK", 0)

    def msgs():
        out = []
        for env in client.get(f"/devices/{handy}/commands", headers=ha).json():
            c = json.loads(_verify(pub, env["payloadBase64"], env["signatureBase64"]))
            if c["type"] == 10:
                out.append(c["payload"]["title"])
        return out

    assert msgs() == []                     # 1.0.8 kennt die Nachricht nicht -> wartet
    hb["appVersion"] = "1.0.10"
    client.post(f"/devices/{handy}/heartbeat", json=hb, headers=ha)
    assert msgs() == ["Jetzt"]              # die Geburtstagsnachricht erst ab ihrem Zeitpunkt
    page = client.get(f"/admin/devices/{handy}").text
    assert "Nachricht ans Kind" in page and "З днем народження!" in page


def test_media_route_only_safe_names(client):
    assert client.get("/media/..%2Fapp.db").status_code == 404
    assert client.get("/media/short.mp4").status_code == 404
    assert client.get("/media/abcdefghijklmnopqrstuvwx.mp4").status_code == 404


def test_iphone_enroll_install_and_pair(client, monkeypatch, tmp_path):
    """iPhone ohne App Store: Link → Profil → UDID → (Build) → Installieren → Koppeln."""
    import app.db as dbmod
    from app.config import settings
    monkeypatch.setattr(settings, "download_dir", str(tmp_path))
    _admin_login(client)
    r = client.post("/admin/ios/new", data={"device_name": "iPhone Renat"}, follow_redirects=False)
    assert r.status_code == 303 and r.headers["location"].startswith("/admin/ios/")
    code = r.headers["location"].rsplit("/", 1)[1]
    assert "iPhone Renat" in client.get(f"/admin/ios/{code}").text
    assert "Link zeigen" in client.get("/admin").text

    page = client.get(f"/ios/{code}").text
    assert "iPhone anmelden" in page and f"/ios/{code}/anmelden.mobileconfig" in page
    prof = client.get(f"/ios/{code}/anmelden.mobileconfig")
    assert prof.headers["content-type"].startswith("application/x-apple-aspen-config")
    assert b"Profile Service" in prof.content and f"/ios/{code}/udid".encode() in prof.content
    assert client.get("/ios/999999/anmelden.mobileconfig").status_code == 404

    # iOS schickt PKCS#7 mit Plist darin
    udid = "00008110-001A2B3C4D5E801E"
    body = (b"\x30\x82junk<?xml version=\"1.0\"?><plist><dict><key>PRODUCT</key><string>iPhone14,7</string>"
            b"<key>UDID</key><string>" + udid.encode() + b"</string><key>VERSION</key><string>22A3354</string></dict></plist>junk")
    r = client.post(f"/ios/{code}/udid", content=body, follow_redirects=False)
    assert r.status_code == 301 and f"u={udid}" in r.headers["location"]
    row = dbmod.query_one("SELECT * FROM ios_enrollments WHERE udid=?", (udid,))
    assert row["product"] == "iPhone14,7" and row["code"] == code
    page = client.get(f"/ios/{code}?u={udid}").text
    assert "wird gerade für dieses iPhone gebaut" in page
    assert "wartet auf neuen Build" in client.get(f"/admin/ios/{code}").text

    # Build mit dieser UDID liegt bereit
    (tmp_path / "ios").mkdir()
    (tmp_path / "ios" / "FrohLock.ipa").write_bytes(b"PK")
    (tmp_path / "ios" / "version.txt").write_text("1.0.0")
    (tmp_path / "ios" / "udids.txt").write_text(udid.lower() + "\n")
    page = client.get(f"/ios/{code}?u={udid}").text   # (in echt per Cookie, nur über HTTPS)
    assert "itms-services://?action=download-manifest" in page and "FrohLock 1.0.0 installieren" in page
    man = client.get("/ios/manifest.plist").text
    assert "de.froehlichdienste.frohlock" in man and "/ios/FrohLock.ipa" in man
    assert client.get("/ios/FrohLock.ipa").content == b"PK"
    assert client.get("/updates/manifest?platform=ios").json()["version"] == "1.0.0"

    # Koppeln: Name aus dem Admin, Plattform ios, keine Update-Befehle, Nachrichten erlaubt
    dev = client.post("/devices/register", json={"pairingCode": code, "deviceName": "iPhone", "platform": "ios"}).json()
    did, auth = dev["deviceId"], {"Authorization": f"Bearer {dev['token']}"}
    d = dbmod.query_one("SELECT name, platform FROM devices WHERE id=?", (did,))
    assert d["name"] == "iPhone Renat" and d["platform"] == "ios"
    client.post(f"/devices/{did}/heartbeat", headers=auth, json={
        "deviceId": did, "appVersion": "0.0.1", "platform": "ios",
        "deviceInfo": {"model": "iPhone14,7", "screenTimeAuthorized": True, "countedCategories": 12}})
    monkeypatch.setattr(settings, "update_version", "9.9.9")
    monkeypatch.setattr(settings, "update_url", "https://x/y.exe")
    monkeypatch.setattr(settings, "update_sha256", "ab" * 32)
    assert client.get(f"/devices/{did}/commands", headers=auth).json() == []
    client.post(f"/devices/{did}/message", headers=auth)  # (kein Geräte-Endpunkt – ignoriert)
    r = client.post(f"/admin/devices/{did}/message", data={"title": "Hi", "text": "Hallo", "button": "OK"},
                    follow_redirects=False)
    assert r.status_code == 303
    assert len(client.get(f"/devices/{did}/commands", headers=auth).json()) == 1
    page = client.get(f"/admin/devices/{did}").text
    assert "Bildschirmzeit-Erlaubnis" in page and "12 Kategorien" in page and "Nachricht ans Kind" in page
    assert "gekoppelt" in client.get(f"/ios/{code}").text


def test_android_app_exception_open_then_defined(client):
    """21.11.2026: Ausnahme für eine App erst vormerken („offen" = wirkt nicht), Art später festlegen."""
    import json as _json
    import app.db as dbmod
    with open(PUB_PATH, "rb") as fh:
        pub = load_pem_public_key(fh.read())
    _admin_login(client)
    client.post("/admin/android/new", data={"device_name": "Handy"})
    code = dbmod.query_one("SELECT code FROM pairing_codes")["code"]
    ph = client.post("/devices/register", json={"pairingCode": code, "platform": "android"}).json()
    did = ph["deviceId"]
    hdr = {"Authorization": f"Bearer {ph['token']}"}
    client.post(f"/devices/{did}/config-draft", headers=hdr, json={"pinHash": "aA==", "pinSalt": "aA=="})

    def cfg():
        env = client.get(f"/devices/{did}/config?have=0", headers=hdr).json()
        return _json.loads(_verify(pub, env["payloadBase64"], env["signatureBase64"]))

    # Handy meldet installierte Apps einmal; der nächste Heartbeat ohne Liste behält sie.
    client.post(f"/devices/{did}/heartbeat", headers=hdr, json={"platform": "android", "appVersion": "1.0.12",
                "deviceInfo": {"installedApps": [{"p": "com.duolingo", "n": "Duolingo"}, {"p": "com.mojang.minecraftpe", "n": "Minecraft"}]}})
    client.post(f"/devices/{did}/heartbeat", headers=hdr, json={"platform": "android", "appVersion": "1.0.12",
                "deviceInfo": {"exemptMinutes": 7}})
    page = client.get(f"/admin/devices/{did}").text
    assert "Ausnahme für eine App" in page and 'data-label="Duolingo"' in page and "Heute 7 Min" in page

    r = client.post(f"/admin/devices/{did}/app-exceptions", data={"new_package": "com.duolingo", "new_label": "Duolingo",
                    "new_mode": "offen"}, follow_redirects=False)
    assert r.status_code == 303 and "ok=ausnahme" in r.headers["location"]
    c = cfg()
    assert "com.duolingo" not in c["alwaysAllowedApps"] and c["uncountedApps"] == []
    page = client.get(f"/admin/devices/{did}").text
    assert "com.duolingo" in page and 'value="offen" selected' in page

    client.post(f"/admin/devices/{did}/app-exceptions", data={"mode_0": "zeit"})
    c = cfg()
    assert c["uncountedApps"] == ["com.duolingo"] and "com.duolingo" not in c["alwaysAllowedApps"]

    client.post(f"/admin/devices/{did}/app-exceptions", data={"mode_0": "beides"})
    c = cfg()
    assert c["uncountedApps"] == ["com.duolingo"] and "com.duolingo" in c["alwaysAllowedApps"]
    assert "org.telegram.messenger" in c["alwaysAllowedApps"]   # Messenger-Auswahl bleibt unberührt

    r = client.post(f"/admin/devices/{did}/app-exceptions", data={"mode_0": "beides", "new_package": "kaputt!"},
                    follow_redirects=False)
    assert "apppaketfalsch" in r.headers["location"]

    client.post(f"/admin/devices/{did}/app-exceptions", data={"del_0": "on"})
    c = cfg()
    assert c["uncountedApps"] == [] and "com.duolingo" not in c["alwaysAllowedApps"]


EVO_SHA = "d34d36f3be1a6c49c56525ae86469b92e4f417ddf0b43cf00dd80c385c4b0562"


def test_windows_program_exception_evoluzzer(client):
    """21.11.2026, Daniel: „Renat darf, wenn seine Zeit rum ist, nur Evoluzzer spielen – so lange er will."
    Windows-Karte „Ausnahme für ein Programm" -> signierte Config „appExceptions" (nur Windows), Heartbeat-Minuten,
    die NICHT ins (gemeinsame) Tageskonto fließen, Anzeige im Admin, Audit."""
    import json
    import datetime
    from zoneinfo import ZoneInfo
    import app.db as dbmod
    with open(PUB_PATH, "rb") as fh:
        pub = load_pem_public_key(fh.read())
    _admin_login(client)
    laptop, la = _pair(client, "Laptop renat")
    handy, ha = _pair(client, "renat handy")
    client.post(f"/admin/devices/{laptop}/child", data={"child": "Renat"})
    client.post(f"/admin/devices/{handy}/child", data={"child": "Renat"})
    day = datetime.datetime.now(ZoneInfo("Europe/Berlin")).strftime("%Y-%m-%d")
    client.post(f"/devices/{handy}/heartbeat", headers=ha,
                json={"deviceId": handy, "appVersion": "1.0.12", "platform": "android", "usageMinutesToday": 30, "usageDay": day})

    def cfg(did, auth):
        env = client.get(f"/devices/{did}/config?have=0", headers=auth).json()
        return json.loads(_verify(pub, env["payloadBase64"], env["signatureBase64"]))

    # Ohne Ausnahme: Windows bekommt eine leere Liste, Android das Feld gar nicht.
    assert cfg(laptop, la)["appExceptions"] == []
    assert "appExceptions" not in cfg(handy, ha)

    # Karte nur beim Laptop; Hinweis, solange der Laptop noch 0.30.0 hat.
    client.post(f"/devices/{laptop}/heartbeat", headers=la,
                json={"deviceId": laptop, "appVersion": "0.30.0.0", "usageMinutesToday": 50, "usageDay": day})
    page = client.get(f"/admin/devices/{laptop}").text
    assert "Ausnahme für ein Programm" in page and "Evoluzzer eintragen" in page and EVO_SHA in page
    assert "wirkt erst ab FrohLock 0.31.0" in page
    assert "Ausnahme für ein Programm" not in client.get(f"/admin/devices/{handy}").text

    # Falsche Eingaben -> nichts gespeichert.
    v0 = configbuilder_version(laptop)
    r = client.post(f"/admin/devices/{laptop}/program-exceptions", data={"new_exe": "C:\\Spiele\\Evoluzzer.exe"},
                    follow_redirects=False)
    assert "ok=programmfalsch" in r.headers["location"]
    r = client.post(f"/admin/devices/{laptop}/program-exceptions",
                    data={"new_exe": "Evoluzzer.exe", "new_sha": "1234"}, follow_redirects=False)
    assert "ok=pruefsummefalsch" in r.headers["location"]
    assert configbuilder_version(laptop) == v0

    # „Evoluzzer eintragen" + Speichern (so schickt das Formular die vorausgefüllten Felder).
    r = client.post(f"/admin/devices/{laptop}/program-exceptions", follow_redirects=False,
                    data={"new_name": "Evoluzzer", "new_exe": "Evoluzzer.exe", "new_sha": EVO_SHA.upper() + "\n",
                          "new_mode": "beides"})
    assert r.status_code == 303 and "ok=programm#programme" in r.headers["location"]
    c = cfg(laptop, la)
    assert c["appExceptions"] == [{"name": "Evoluzzer", "exe": "Evoluzzer.exe", "sha256": [EVO_SHA], "mode": "beides"}]
    assert "appExceptions" not in cfg(handy, ha)          # Android-Verhalten unverändert
    assert cfg(handy, ha)["uncountedApps"] == []
    audit = dbmod.query("SELECT * FROM audit WHERE device_id=? AND action='program-exceptions'", (laptop,))
    assert audit and "Evoluzzer (Evoluzzer.exe)=beides, 1 Prüfsumme(n)" in audit[-1]["detail"]

    # Gleiche Datei erneut eintragen ersetzt statt doppelt; Art ändern; Prüfsumme leeren.
    client.post(f"/admin/devices/{laptop}/program-exceptions",
                data={"mode_0": "zeit", "sha_0": "", "new_exe": "evoluzzer.exe", "new_mode": "sperre", "new_sha": EVO_SHA})
    c = cfg(laptop, la)
    assert len(c["appExceptions"]) == 1 and c["appExceptions"][0]["mode"] == "sperre"
    client.post(f"/admin/devices/{laptop}/program-exceptions", data={"mode_0": "beides", "sha_0": EVO_SHA})
    assert cfg(laptop, la)["appExceptions"][0]["mode"] == "beides"
    page = client.get(f"/admin/devices/{laptop}").text
    assert 'value="beides" selected' in page

    # Laptop (0.31.0) meldet 45 Min Evoluzzer, die nicht zählen.
    hb = {"deviceId": laptop, "appVersion": "0.31.0.0", "usageMinutesToday": 50, "usageDay": day, "dailyBudgetMinutes": 60,
          "exemptMinutesToday": 45, "exemptPrograms": [{"name": "Evoluzzer", "minutes": 45}, "kaputt", {"name": "x", "minutes": "y"}]}
    assert client.post(f"/devices/{laptop}/heartbeat", headers=la, json=hb).status_code == 200
    page = client.get(f"/admin/devices/{laptop}").text
    assert "wirkt erst ab FrohLock 0.31.0" not in page
    assert "Evoluzzer: 45 Min" in page and "zählt nicht" in page
    assert "Evoluzzer: 45 Min (zählt nicht)" in client.get("/admin").text
    row = dbmod.query_one("SELECT minutes, exempt_minutes FROM usage_daily WHERE device_id=? AND day=?", (laptop, day))
    assert row["minutes"] == 50 and row["exempt_minutes"] == 45
    # Gemeinsames Tageskonto: das Handy sieht nur die 50 gezählten Minuten des Laptops, nicht die 45 Evoluzzer-Minuten.
    hc = _shared_cmds(client, pub, handy, ha)
    assert hc and hc[0]["payload"]["minutes"] == "50"

    # Einstellungen übernehmen auf ein neues Windows-Gerät nimmt die Programm-Ausnahme mit.
    neu, na = _pair(client, "Laptop neu")
    client.post(f"/admin/devices/{neu}/copy-from", data={"source_id": laptop})
    assert cfg(neu, na)["appExceptions"][0]["exe"].lower() == "evoluzzer.exe"   # Groß/Klein egal (Client vergleicht ohne)

    # Entfernen.
    client.post(f"/admin/devices/{laptop}/program-exceptions", data={"mode_0": "beides", "del_0": "on"})
    assert cfg(laptop, la)["appExceptions"] == []


# ---- „Sofort sperren" endet mit dem nächsten Tag (Server entsperrt alte Apps nach dem Tageswechsel) ----

PARENT_REASON = "Mama oder Papa haben das Handy gerade gesperrt. 💛"


def _parent_lock_day(device_id):
    import app.db as dbmod
    return dbmod.query_one("SELECT parent_lock_day FROM devices WHERE id=?", (device_id,))["parent_lock_day"]


def _next_day(day):
    import datetime
    return (datetime.date.fromisoformat(day) + datetime.timedelta(days=1)).isoformat()


def _unlock_rows(device_id):
    import app.db as dbmod
    return dbmod.query("SELECT * FROM commands WHERE device_id=? AND type=0 ORDER BY created_at", (device_id,))


def _heartbeat(client, device_id, auth, day, locked, reason="", **extra):
    hb = {"deviceId": device_id, "appVersion": "0.31.0.0", "usageDay": day,
          "currentlyLocked": locked, "lockReason": reason, **extra}
    r = client.post(f"/devices/{device_id}/heartbeat", headers=auth, json=hb)
    assert r.status_code == 200
    return r


def test_parent_lock_ends_with_new_day_by_one_minute_unlock(client):
    import json as _json
    import time as _time
    with open(PUB_PATH, "rb") as fh:
        pub = load_pem_public_key(fh.read())
    _admin_login(client)
    dev, auth = _pair(client, "Sperr-Laptop")

    # Noch keine Eltern-Sperre -> nichts zu tun, auch bei neuem Tag.
    assert _parent_lock_day(dev) is None
    _heartbeat(client, dev, auth, "2030-01-01", True, PARENT_REASON)
    assert _unlock_rows(dev) == []

    # (a) Admin sperrt -> Tag gemerkt (Berlin, yyyy-MM-dd); Befehl „lock" liegt in der Warteschlange.
    r = client.post(f"/admin/devices/{dev}/command", data={"kind": "lock"}, follow_redirects=False)
    assert r.status_code == 303
    day = _parent_lock_day(dev)
    import app.admin as adminmod
    assert day == adminmod._berlin_now().strftime("%Y-%m-%d")
    import app.db as dbmod
    assert len(dbmod.query("SELECT 1 FROM commands WHERE device_id=? AND type=1", (dev,))) == 1

    # Heartbeat am selben Tag (gesperrt mit Eltern-Text) -> kein neuer Befehl, Merkung bleibt.
    _heartbeat(client, dev, auth, day, True, PARENT_REASON)
    assert _unlock_rows(dev) == [] and _parent_lock_day(dev) == day
    # Auch ein leerer Tag (altes Gerät ohne Angabe) ändert nichts.
    _heartbeat(client, dev, auth, "", True, PARENT_REASON)
    assert _unlock_rows(dev) == [] and _parent_lock_day(dev) == day
    # Ein früherer Tag am Gerät (Uhr/Zeitzone hinkt) ebenfalls nicht.
    _heartbeat(client, dev, auth, "2000-01-01", True, PARENT_REASON)
    assert _unlock_rows(dev) == [] and _parent_lock_day(dev) == day

    # (b) Neuer Tag am Gerät, noch mit Eltern-Grund gesperrt -> genau EIN Unlock mit 1 Minute.
    tomorrow = _next_day(day)
    t0 = int(_time.time())
    _heartbeat(client, dev, auth, tomorrow, True, PARENT_REASON)
    rows = _unlock_rows(dev)
    assert len(rows) == 1 and _parent_lock_day(dev) is None
    body = _json.loads(base64.b64decode(_json.loads(rows[0]["payload_json"])["envelope"]["payloadBase64"]))
    assert body["type"] == 0 and body["unlockMinutes"] == 1 and body["deviceId"] == dev
    assert 3500 < body["expiresAtUnix"] - t0 <= 3600 + 5 and rows[0]["acked"] == 0
    # Das Gerät bekommt ihn mit gültiger Signatur ausgeliefert (kein Plattform-/Versions-Gate für Typ 0).
    env = [e for e in client.get(f"/devices/{dev}/commands", headers=auth).json()
           if _json.loads(base64.b64decode(e["payloadBase64"]))["type"] == 0]
    assert len(env) == 1
    assert _json.loads(_verify(pub, env[0]["payloadBase64"], env[0]["signatureBase64"]))["unlockMinutes"] == 1
    audit = dbmod.query("SELECT * FROM audit WHERE device_id=? AND action='cmd:unlock'", (dev,))
    assert len(audit) == 1 and audit[0]["actor"] == "automatik" and "Sofortsperre endet mit dem neuen Tag" in audit[0]["detail"]

    # Zweiter Heartbeat (Gerät hat noch nicht quittiert) -> kein weiterer Befehl.
    _heartbeat(client, dev, auth, tomorrow, True, PARENT_REASON)
    _heartbeat(client, dev, auth, _next_day(tomorrow), True, PARENT_REASON)
    assert len(_unlock_rows(dev)) == 1

    # Die Geräteseite rendert mit dem neuen Hinweistext.
    page = client.get(f"/admin/devices/{dev}").text
    assert "endet von selbst mit dem nächsten Tag" in page and "startet auch das Tageslimit neu" in page
    assert "bleibt bestehen" not in page


def test_parent_lock_new_day_without_parent_reason_sends_nothing(client):
    _admin_login(client)
    dev, auth = _pair(client, "Sperr-Handy")
    client.post(f"/admin/devices/{dev}/command", data={"kind": "lock"}, follow_redirects=False)
    day = _parent_lock_day(dev)
    assert day

    # (c1) Neuer Tag, Gerät ist gar nicht (mehr) gesperrt -> nichts senden, Merkung leeren.
    _heartbeat(client, dev, auth, _next_day(day), False, "")
    assert _unlock_rows(dev) == [] and _parent_lock_day(dev) is None

    # (c2) Neuer Tag, gesperrt aus anderem Grund (Tageslimit, Zeitplan, Löschversuch) -> nichts senden, Merkung leeren.
    for reason in ("Tageslimit erreicht (74/60 min)", "Schlafenszeit", "Manipulationsversuch erkannt",
                   "Mama oder Papa waren gerade beschäftigt"):
        client.post(f"/admin/devices/{dev}/command", data={"kind": "lock"}, follow_redirects=False)
        assert _parent_lock_day(dev) == day
        _heartbeat(client, dev, auth, _next_day(day), True, reason)
        assert _parent_lock_day(dev) is None
    # Nur der letzte Grund beginnt NICHT mit „Mama oder Papa haben" – also nirgends ein Unlock.
    assert _unlock_rows(dev) == []


def test_parent_lock_cleared_by_admin_unlock_and_works_on_android_and_ios(client):
    _admin_login(client)
    dev, auth = _pair(client, "Sperr-Tablet")
    client.post(f"/admin/devices/{dev}/command", data={"kind": "lock"}, follow_redirects=False)
    assert _parent_lock_day(dev)
    # (d) Admin entsperrt -> Merkung weg; ein späterer neuer Tag erzeugt nichts mehr.
    client.post(f"/admin/devices/{dev}/command", data={"kind": "unlock", "minutes": "30"}, follow_redirects=False)
    assert _parent_lock_day(dev) is None
    assert len(_unlock_rows(dev)) == 1          # nur der Befehl des Admins
    _heartbeat(client, dev, auth, "2099-01-01", True, PARENT_REASON)
    assert len(_unlock_rows(dev)) == 1
    # Andere Befehle lassen die Merkung unverändert.
    client.post(f"/admin/devices/{dev}/command", data={"kind": "lock"}, follow_redirects=False)
    day = _parent_lock_day(dev)
    for kind in ("ping", "timeout", "cleartimeout", "update", "updateconfig"):
        client.post(f"/admin/devices/{dev}/command", data={"kind": kind}, follow_redirects=False)
        assert _parent_lock_day(dev) == day

    # Android und iPhone verhalten sich gleich (Plattform kommt aus dem Heartbeat).
    for platform, word in (("android", "Handy"), ("ios", "iPhone")):
        d2, a2 = _pair(client, f"Gerät {platform}")
        client.post(f"/admin/devices/{d2}/command", data={"kind": "lock"}, follow_redirects=False)
        day2 = _parent_lock_day(d2)
        reason = f"Mama oder Papa haben das {word} für heute gesperrt. Morgen geht es weiter. 💛"
        _heartbeat(client, d2, a2, day2, True, reason, platform=platform, deviceOwner=True)
        assert _unlock_rows(d2) == []
        _heartbeat(client, d2, a2, _next_day(day2), True, reason, platform=platform, deviceOwner=True)
        assert len(_unlock_rows(d2)) == 1 and _parent_lock_day(d2) is None
        assert len(client.get(f"/devices/{d2}/commands", headers=a2).json()) >= 1
        page = client.get(f"/admin/devices/{d2}").text
        assert "endet von selbst mit dem nächsten Tag" in page and f"am {word} den Eltern-PIN" in page


def test_parent_lock_error_does_not_break_heartbeat(client, monkeypatch):
    import app.signing as signingmod
    _admin_login(client)
    dev, auth = _pair(client, "Sperr-Fehler")
    client.post(f"/admin/devices/{dev}/command", data={"kind": "lock"}, follow_redirects=False)
    day = _parent_lock_day(dev)

    def boom(*a, **kw):
        raise RuntimeError("Schlüssel fehlt")
    monkeypatch.setattr(signingmod, "sign_command", boom)
    # Heartbeat klappt trotzdem, der übrige Teil (z. B. Tagesverbrauch) wird gespeichert, Merkung bleibt zum Neuversuch.
    _heartbeat(client, dev, auth, _next_day(day), True, PARENT_REASON, usageMinutesToday=12)
    import app.db as dbmod
    assert dbmod.query_one("SELECT usage_today FROM devices WHERE id=?", (dev,))["usage_today"] == 12
    assert _parent_lock_day(dev) == day and _unlock_rows(dev) == []
    monkeypatch.undo()
    # Auch wenn das Einreihen selbst scheitert, wird das Leeren zurückgerollt (alles oder nichts) – Heartbeat bleibt heil.
    import app.api as apimod

    def boom_insert(*a, **kw):
        raise RuntimeError("Einreihen kaputt")
    monkeypatch.setattr(apimod, "insert_command", boom_insert)
    _heartbeat(client, dev, auth, _next_day(day), True, PARENT_REASON, usageMinutesToday=13)
    assert dbmod.query_one("SELECT usage_today FROM devices WHERE id=?", (dev,))["usage_today"] == 13
    assert _parent_lock_day(dev) == day and _unlock_rows(dev) == []
    monkeypatch.undo()
    _heartbeat(client, dev, auth, _next_day(day), True, PARENT_REASON)
    assert len(_unlock_rows(dev)) == 1 and _parent_lock_day(dev) is None


def test_parent_lock_new_app_versions_get_no_server_unlock(client):
    """Neue Apps beenden die Eltern-Sperre selbst. Melden sie am neuen Tag noch „Mama oder Papa haben …“,
    ist es die strenge Sperre (Manipulation) – der Server hebt sie NICHT auf."""
    _admin_login(client)
    for platform, version, extra in (("", "0.32.0.0", {}), ("", "0.33.1", {}),
                                     ("android", "1.0.13", {"platform": "android", "deviceOwner": False})):
        dev, auth = _pair(client, f"Neu {platform or 'windows'} {version}")
        client.post(f"/admin/devices/{dev}/command", data={"kind": "lock"}, follow_redirects=False)
        day = _parent_lock_day(dev)
        _heartbeat(client, dev, auth, _next_day(day), True, PARENT_REASON, **{"appVersion": version, **extra})
        assert _unlock_rows(dev) == [] and _parent_lock_day(dev) is None
    # Gegenprobe: Android 1.0.12 (alt) bekommt den Unlock.
    dev, auth = _pair(client, "Alt android")
    client.post(f"/admin/devices/{dev}/command", data={"kind": "lock"}, follow_redirects=False)
    day = _parent_lock_day(dev)
    _heartbeat(client, dev, auth, _next_day(day), True, PARENT_REASON,
               appVersion="1.0.12", platform="android", deviceOwner=False)
    assert len(_unlock_rows(dev)) == 1
