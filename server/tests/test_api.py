"""End-to-End-Tests der Brücke inkl. Signaturprüfung mit dem Public Key."""
import base64
import os
import tempfile

os.environ["FROHLOCK_DB"] = os.path.join(tempfile.gettempdir(), f"frohlock_test_{os.getpid()}.db")
os.environ["FROHLOCK_ADMIN_PASSWORD"] = "test-pass"

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
