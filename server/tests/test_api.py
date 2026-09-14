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
    r = client.get(f"/admin/devices/{device_id}")
    assert r.status_code == 200
    assert "47 min" in r.text
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
