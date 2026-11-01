"""Baut die vollständige, signierte LockConfig aus gespeichertem Entwurf + Server-Feldern."""
from __future__ import annotations

import json
import time

from . import db
from .config import settings
from .signing import make_envelope

# Android: Apps, die auch während jeder Sperre offen bleiben. Standard = Telegram + Signal (Daniel 01.11.2026).
DEFAULT_ALLOWED_APPS = ["org.telegram.messenger", "org.telegram.messenger.web", "org.thoughtcrime.securesms"]
# Für die Auswahl im Admin (Name -> Paketnamen).
KNOWN_MESSENGERS = [
    ("Telegram", ["org.telegram.messenger", "org.telegram.messenger.web"]),
    ("Signal", ["org.thoughtcrime.securesms"]),
    ("WhatsApp", ["com.whatsapp"]),
    ("Threema", ["ch.threema.app"]),
]


def get_stored(device_id: str) -> tuple[int, dict] | None:
    row = db.query_one(
        "SELECT config_version, config_json FROM device_config WHERE device_id = ?", (device_id,)
    )
    if not row:
        return None
    return row["config_version"], json.loads(row["config_json"])


def save_draft(device_id: str, draft: dict) -> int:
    """Speichert einen neuen Config-Entwurf und erhöht die Version (Anti-Rollback am Client)."""
    existing = get_stored(device_id)
    new_version = (existing[0] + 1) if existing else 1
    with db.tx() as c:
        c.execute(
            """INSERT INTO device_config(device_id, config_version, config_json, updated_at)
               VALUES (?,?,?,?)
               ON CONFLICT(device_id) DO UPDATE SET
                   config_version=excluded.config_version,
                   config_json=excluded.config_json,
                   updated_at=excluded.updated_at""",
            (device_id, new_version, json.dumps(draft), int(time.time())),
        )
    return new_version


def build_full_config(device_id: str) -> dict | None:
    """Setzt LockConfig zusammen (Feldnamen = FrohLock.Core.Models.LockConfig, camelCase)."""
    stored = get_stored(device_id)
    if not stored:
        return None
    version, draft = stored

    return {
        "configVersion": version,
        "deviceId": device_id,
        "windows": draft.get("windows", []),
        "dailyBudgetMinutes": draft.get("dailyBudgetMinutes", 0),
        "dailyBudgetByWeekday": draft.get("dailyBudgetByWeekday", []),
        "unlockGraceMinutes": draft.get("unlockGraceMinutes", 60),
        "pinHash": draft.get("pinHash", ""),
        "pinSalt": draft.get("pinSalt", ""),
        "pinIterations": draft.get("pinIterations", 210_000),
        "serverBaseUrl": settings.public_base_url,
        "tlsSpkiPins": settings.tls_pins,
        "serverPollSeconds": 30,
        "maxTrustedTimeStalenessMinutes": draft.get("maxTrustedTimeStalenessMinutes", 4320),
        "failSecureLocked": True,
        "alwaysAllowedApps": draft.get("alwaysAllowedApps", DEFAULT_ALLOWED_APPS),
    }


def signed_config_envelope(device_id: str) -> dict | None:
    full = build_full_config(device_id)
    if full is None:
        return None
    return make_envelope(full)


def current_version(device_id: str) -> int:
    stored = get_stored(device_id)
    return stored[0] if stored else 0


# Was beim Übernehmen von einem anderen Gerät mitkommt (Zeitplan, Limits, PIN, Entsperrdauer).
_COPY_KEYS = ("windows", "dailyBudgetMinutes", "dailyBudgetByWeekday", "unlockGraceMinutes",
              "pinHash", "pinSalt", "pinIterations", "maxTrustedTimeStalenessMinutes", "alwaysAllowedApps")


def copy_settings(source_id: str, target_id: str) -> str | None:
    """Einstellungen eines Geräts auf ein anderes übernehmen. Gibt den Namen der Quelle zurück (oder None)."""
    src = get_stored(source_id)
    if not src:
        return None
    _, draft = src
    target = get_stored(target_id)
    new = dict(target[1]) if target else {}
    for k in _COPY_KEYS:
        if k in draft:
            new[k] = json.loads(json.dumps(draft[k]))
    save_draft(target_id, new)
    row = db.query_one("SELECT name, reset_email FROM devices WHERE id=?", (source_id,))
    if row and row["reset_email"]:
        with db.tx() as c:
            c.execute("UPDATE devices SET reset_email=COALESCE(reset_email, ?) WHERE id=?", (row["reset_email"], target_id))
    return row["name"] if row else source_id
