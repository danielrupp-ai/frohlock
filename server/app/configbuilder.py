"""Baut die vollständige, signierte LockConfig aus gespeichertem Entwurf + Server-Feldern."""
from __future__ import annotations

import json
import time

from . import db
from .config import settings
from .signing import make_envelope


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
    }


def signed_config_envelope(device_id: str) -> dict | None:
    full = build_full_config(device_id)
    if full is None:
        return None
    return make_envelope(full)


def current_version(device_id: str) -> int:
    stored = get_stored(device_id)
    return stored[0] if stored else 0
