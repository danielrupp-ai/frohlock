"""Geräte-Endpunkte der Brücke. Sicherheitsrelevante Antworten sind server-signiert."""
from __future__ import annotations

import json
import time

from fastapi import APIRouter, Depends, HTTPException, Request, Response

from . import configbuilder, db
from .config import settings
from .schemas import ConfigDraft, HeartbeatIn, RegisterRequest, RegisterResponse
from .security import authed_device, hash_token, new_token

router = APIRouter()


def _require_self(request: Request, device_id: str) -> str:
    authed = authed_device(request)
    if authed != device_id:
        raise HTTPException(status_code=403, detail="Fremdes Gerät")
    return authed


@router.post("/devices/register", response_model=RegisterResponse)
def register(req: RegisterRequest):
    now = int(time.time())
    row = db.query_one("SELECT * FROM pairing_codes WHERE code = ?", (req.pairingCode.strip(),))
    if not row:
        raise HTTPException(status_code=400, detail="Kopplungscode ungültig")
    if row["used_device_id"]:
        raise HTTPException(status_code=400, detail="Kopplungscode bereits benutzt")
    if row["expires_at"] < now:
        raise HTTPException(status_code=400, detail="Kopplungscode abgelaufen")

    device_id = _new_id()
    token = new_token()
    with db.tx() as c:
        c.execute(
            "INSERT INTO devices(id, name, token_hash, created_at) VALUES (?,?,?,?)",
            (device_id, req.deviceName or row["device_name"], hash_token(token), now),
        )
        c.execute("UPDATE pairing_codes SET used_device_id = ? WHERE code = ?",
                  (device_id, req.pairingCode.strip()))
    db.audit("device", "register", device_id, req.deviceName)
    return RegisterResponse(deviceId=device_id, token=token)


@router.get("/devices/{device_id}/config")
def get_config(device_id: str, request: Request, response: Response, have: int = 0):
    _require_self(request, device_id)
    version = configbuilder.current_version(device_id)
    if version == 0:
        raise HTTPException(status_code=404, detail="Keine Konfiguration hinterlegt")
    if have >= version:
        return Response(status_code=304)
    env = configbuilder.signed_config_envelope(device_id)
    return env


@router.post("/devices/{device_id}/config-draft")
def set_config_draft(device_id: str, draft: ConfigDraft, request: Request):
    """Setup (Eltern) legt Zeitplan + PIN-Hash fest. PIN kommt nie im Klartext."""
    _require_self(request, device_id)
    version = configbuilder.save_draft(device_id, draft.model_dump())
    if draft.resetEmail:
        with db.tx() as c:
            c.execute("UPDATE devices SET reset_email=? WHERE id=?", (draft.resetEmail.strip(), device_id))
    db.audit("device", "config-draft", device_id, f"v{version}")
    return {"configVersion": version}


@router.post("/devices/{device_id}/heartbeat")
def heartbeat(device_id: str, hb: HeartbeatIn, request: Request):
    _require_self(request, device_id)
    with db.tx() as c:
        c.execute(
            """UPDATE devices SET last_seen=?, app_version=?, reported_config_version=?,
                   locked=?, lock_reason=?, trusted_time_age=?, pin_failures=?,
                   usage_today=?, budget_minutes=?, agent_age=?, recent_audit=? WHERE id=?""",
            (int(time.time()), hb.appVersion, hb.configVersion, 1 if hb.currentlyLocked else 0,
             hb.lockReason, hb.trustedTimeAgeSeconds, hb.pinFailuresToday,
             hb.usageMinutesToday, hb.dailyBudgetMinutes, hb.agentAliveAgeSeconds, hb.recentAudit, device_id),
        )
        # Tages-Historie: höchsten gemeldeten Wert je Tag behalten.
        day = hb.usageDay or time.strftime("%Y-%m-%d")
        c.execute(
            """INSERT INTO usage_daily(device_id, day, minutes) VALUES (?,?,?)
               ON CONFLICT(device_id, day) DO UPDATE SET minutes=MAX(minutes, excluded.minutes)""",
            (device_id, day, hb.usageMinutesToday),
        )
    return {"ok": True}


def _version_tuple(v: str) -> tuple:
    parts = []
    for p in (v or "0").split("."):
        try:
            parts.append(int(p))
        except ValueError:
            parts.append(0)
    return tuple(parts)


def _version_older(current: str, target: str) -> bool:
    a, b = _version_tuple(current), _version_tuple(target)
    n = max(len(a), len(b))
    a = a + (0,) * (n - len(a))
    b = b + (0,) * (n - len(b))
    return a < b


@router.get("/devices/{device_id}/commands")
def get_commands(device_id: str, request: Request):
    _require_self(request, device_id)
    now = int(time.time())
    rows = db.query(
        "SELECT id, payload_json FROM commands WHERE device_id=? AND acked=0 ORDER BY created_at ASC",
        (device_id,),
    )
    out = []
    for r in rows:
        payload = json.loads(r["payload_json"])
        out.append(payload["envelope"])

    # Selbst-nachlieferndes Update: solange das Gerät eine ÄLTERE Version meldet, bei JEDEM
    # Poll erneut ein Update-Kommando anbieten (robust gegen abgebrochene Downloads bei wackligem Netz).
    try:
        if settings.update_version and settings.update_url and settings.update_sha256:
            dev = db.query_one("SELECT app_version FROM devices WHERE id=?", (device_id,))
            if dev and _version_older(dev["app_version"] or "0", settings.update_version):
                from .signing import sign_command
                out.append(sign_command(device_id, "update", expires_in=3600)["envelope"])
    except Exception:
        pass
    return out


@router.post("/devices/{device_id}/commands/{command_id}/ack")
def ack_command(device_id: str, command_id: str, request: Request):
    _require_self(request, device_id)
    with db.tx() as c:
        c.execute("UPDATE commands SET acked=1 WHERE id=? AND device_id=?", (command_id, device_id))
    return {"ok": True}


@router.get("/updates/manifest")
def update_manifest(channel: str = "stable"):
    if not settings.update_version or not settings.update_url or not settings.update_sha256:
        raise HTTPException(status_code=404, detail="Kein Update konfiguriert")
    from .signing import sign_update_manifest
    return {
        "version": settings.update_version,
        "url": settings.update_url,
        "sha256": settings.update_sha256,
        "signatureBase64": sign_update_manifest(settings.update_version, settings.update_sha256),
    }


def _new_id() -> str:
    import uuid
    return uuid.uuid4().hex
