"""Geräte-Endpunkte der Brücke. Sicherheitsrelevante Antworten sind server-signiert."""
from __future__ import annotations

import json
import time

from fastapi import APIRouter, Depends, HTTPException, Request, Response

from . import alerts, android, configbuilder, db, shared
from .config import settings
from .schemas import ConfigDraft, HeartbeatIn, RegisterRequest, RegisterResponse
from .security import authed_device, hash_token, new_token

router = APIRouter()

MESSAGE_MIN_ANDROID = "1.0.10"   # ab hier mit Video + „zeigen ab"


def enqueue_message(device_id: str, title: str, text: str, button: str, not_before: int, keep_days: int = 3,
                    extra: dict | None = None) -> str:
    """Nachricht der Eltern ans Kind in die Warteschlange (signiert). Zugestellt ab not_before, gültig keep_days danach."""
    from .signing import sign_command
    now = int(time.time())
    start = max(now, int(not_before or 0))
    msg_id = _new_id()
    signed = sign_command(device_id, "message", expires_in=(start - now) + keep_days * 86400,
                          payload={"id": msg_id, "title": title[:120], "text": text[:2000], "button": button[:40],
                                   **{k: str(v) for k, v in (extra or {}).items()}})
    with db.tx() as c:
        c.execute("""INSERT INTO commands(id, device_id, type, payload_json, issued_at, expires_at, created_at, not_before)
                     VALUES (?,?,?,?,?,?,?,?)""",
                  (signed["commandId"], device_id, 10, json.dumps(signed), now, start + keep_days * 86400, now, start))
    return signed["commandId"]


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
    keys = row.keys()
    platform = (req.platform or (row["platform"] if "platform" in keys else "") or "windows").lower()
    name = req.deviceName or row["device_name"]
    # Name aus dem Admin hat Vorrang vor dem Gerätenamen („Handy Danyil" statt „SM-A156B").
    if platform in ("android", "ios") and row["device_name"]:
        name = row["device_name"]
    with db.tx() as c:
        c.execute(
            "INSERT INTO devices(id, name, token_hash, created_at, platform, device_info) VALUES (?,?,?,?,?,?)",
            (device_id, name, hash_token(token), now, platform, json.dumps(req.deviceInfo or {})),
        )
        c.execute("UPDATE pairing_codes SET used_device_id = ? WHERE code = ?",
                  (device_id, req.pairingCode.strip()))
    detail = f"{name} ({platform})"
    template = row["template_device_id"] if "template_device_id" in keys else None
    if template:
        copied = configbuilder.copy_settings(template, device_id)
        if copied:
            detail += f" – Einstellungen übernommen von {copied}"
    db.audit("device", "register", device_id, detail)
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
    now = int(time.time())
    with db.tx() as c:
        c.execute(
            """UPDATE devices SET last_seen=?, app_version=?, reported_config_version=?,
                   locked=?, lock_reason=?, trusted_time_age=?, pin_failures=?,
                   usage_today=?, budget_minutes=?, agent_age=?, recent_audit=?,
                   usage_segments=?, usage_day=?, screen_active=?, penalty_seconds=?, penalty_count=?,
                   lock_visible=?
               WHERE id=?""",
            (now, hb.appVersion, hb.configVersion, 1 if hb.currentlyLocked else 0,
             hb.lockReason, hb.trustedTimeAgeSeconds, hb.pinFailuresToday,
             hb.usageMinutesToday, hb.dailyBudgetMinutes, hb.agentAliveAgeSeconds, hb.recentAudit,
             json.dumps(hb.usageSegments[:100]), hb.usageDay, 1 if hb.screenActive else 0,
             hb.penaltySecondsRemaining, hb.penaltyCount,
             -1 if hb.lockVisible is None else (1 if hb.lockVisible else 0), device_id),
        )
        if hb.platform:
            info = dict(hb.deviceInfo or {})
            if "installedApps" not in info:
                # Liste der installierten Apps schickt das Handy nur ab und zu mit – letzte Liste behalten.
                prev_row = c.execute("SELECT device_info FROM devices WHERE id=?", (device_id,)).fetchone()
                try:
                    prev = json.loads(prev_row["device_info"] or "{}") if prev_row else {}
                except ValueError:
                    prev = {}
                if isinstance(prev, dict) and prev.get("installedApps"):
                    info["installedApps"] = prev["installedApps"]
            c.execute(
                """UPDATE devices SET platform=?, device_owner=?, lock_task=?, device_info=?, relaxed_until=?
                   WHERE id=?""",
                (hb.platform, None if hb.deviceOwner is None else int(hb.deviceOwner),
                 None if hb.lockTaskActive is None else int(hb.lockTaskActive),
                 json.dumps(info), hb.relaxedUntilUnix, device_id),
            )
        if not hb.platform:
            # Windows: Minuten in Ausnahme-Programmen (zählen nicht) – getrennt, nie in usage_daily.minutes
            # (sonst flösse die Zeit ins gemeinsame Tageskonto).
            c.execute("UPDATE devices SET exempt_minutes=?, exempt_detail=? WHERE id=?",
                      (max(0, int(hb.exemptMinutesToday or 0)), json.dumps(_clean_exempt(hb.exemptPrograms)), device_id))
        alerts.on_heartbeat_seen(c, device_id, now)
        new_lines = _remember_gap_alerts(c, device_id, hb.recentAudit)
        alerts.on_new_lines(c, device_id, new_lines, now)
        _note_lock_hidden(c, device_id, hb, now)
        # Tages-Historie: höchsten gemeldeten Wert je Tag behalten.
        day = hb.usageDay or time.strftime("%Y-%m-%d")
        # Abschnitte „von wann bis wann" je Tag mitschreiben (für die Zeitleiste im Admin, auch rückwirkend).
        segs = json.dumps(hb.usageSegments[:100]) if hb.usageSegments else None
        c.execute(
            """INSERT INTO usage_daily(device_id, day, minutes, segments_json, budget) VALUES (?,?,?,?,?)
               ON CONFLICT(device_id, day) DO UPDATE SET minutes=MAX(minutes, excluded.minutes),
                   segments_json=COALESCE(excluded.segments_json, segments_json),
                   budget=excluded.budget""",
            (device_id, day, hb.usageMinutesToday, segs, hb.dailyBudgetMinutes),
        )
        if not hb.platform and hb.exemptMinutesToday:
            c.execute("UPDATE usage_daily SET exempt_minutes=MAX(COALESCE(exempt_minutes, 0), ?) WHERE device_id=? AND day=?",
                      (max(0, int(hb.exemptMinutesToday)), device_id, day))
    return {"ok": True}


def _clean_exempt(items: list) -> list[dict]:
    """[{name, minutes}] aus dem Heartbeat – gekürzt und geprüft (kommt vom Gerät)."""
    out = []
    for it in (items or [])[:10]:
        if not isinstance(it, dict):
            continue
        try:
            minutes = max(0, min(24 * 60, int(it.get("minutes") or 0)))
        except (TypeError, ValueError):
            continue
        out.append({"name": str(it.get("name") or "Programm")[:60], "minutes": minutes})
    return out


def _remember_gap_alerts(c, device_id: str, recent_audit: str) -> list[str]:
    """⚠-Lücken („FrohLock stand still, obwohl der Laptop wach war") dauerhaft merken –
    das Geräte-Protokoll zeigt nur die letzten Zeilen, die Warnung darf nicht wegrollen."""
    lines = [ln.strip() for ln in (recent_audit or "").splitlines()
             if ("\tLUECKE\t" in ln or "\tOVERLAY\t" in ln or "\tUMGEHUNG\t" in ln) and "⚠" in ln]
    if not lines:
        return []
    row = c.execute("SELECT gap_alerts FROM devices WHERE id=?", (device_id,)).fetchone()
    try:
        known = json.loads(row["gap_alerts"]) if row and row["gap_alerts"] else []
    except ValueError:
        known = []
    fresh = [ln for ln in lines if ln not in known]
    if fresh:
        c.execute("UPDATE devices SET gap_alerts=? WHERE id=?", (json.dumps((known + fresh)[-30:]), device_id))
    return fresh


LOCK_HIDDEN_AFTER = 120      # s: so lange darf der Sperrbildschirm fehlen, bevor es eine Auffälligkeit wird
LOCK_HIDDEN_REPEAT = 1800    # s: danach höchstens alle 30 Min eine neue Zeile


def _note_lock_hidden(c, device_id: str, hb: HeartbeatIn, now: int) -> None:
    """Gesperrt + Bildschirm an + Sperrbildschirm nicht sichtbar = das Kind nutzt das Handy trotz Sperre
    (erlaubte App wie Telegram offen, oder das Overlay fehlt). Nach 2 Min als Auffälligkeit merken –
    das Eltern-Web darf nicht nur „gesperrt" sagen, während das Handy in Benutzung ist (Lehre 07.11.2026)."""
    hidden = (hb.platform == "android" and hb.currentlyLocked and hb.screenActive and hb.lockVisible is False)
    row = c.execute("SELECT lock_hidden_since, lock_hidden_alert_at FROM devices WHERE id=?", (device_id,)).fetchone()
    if row is None:
        return
    since = row["lock_hidden_since"] or 0
    if not hidden:
        if since:
            c.execute("UPDATE devices SET lock_hidden_since=0 WHERE id=?", (device_id,))
        return
    if not since:
        c.execute("UPDATE devices SET lock_hidden_since=? WHERE id=?", (now, device_id))
        return
    if now - since < LOCK_HIDDEN_AFTER or now - (row["lock_hidden_alert_at"] or 0) < LOCK_HIDDEN_REPEAT:
        return
    apps = (hb.deviceInfo or {}).get("allowedApps") or []
    what = (f"erlaubte App offen: {', '.join(str(a) for a in apps)} – zählt nicht aufs Limit" if apps
            else "keine App ist erlaubt – der Sperrbildschirm fehlt")
    ts = time.strftime("%Y-%m-%dT%H:%M:%S", time.gmtime(now)) + ".000000Z"
    line = f"{ts}\tUMGEHUNG\t⚠ Gesperrt, aber das Handy ist seit {(now - since) // 60} Min in Benutzung ({what})"
    c.execute("UPDATE devices SET lock_hidden_alert_at=? WHERE id=?", (now, device_id))
    _remember_gap_alerts(c, device_id, line)


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
    # Abgelaufene Befehle nicht endlos neu ausliefern (das Gerät verwirft sie ohnehin).
    rows = db.query(
        """SELECT id, type, payload_json FROM commands WHERE device_id=? AND acked=0
               AND (expires_at IS NULL OR expires_at=0 OR expires_at>?)
               AND (not_before IS NULL OR not_before<=?) ORDER BY created_at ASC""",
        (device_id, now, now),
    )
    # Auszeit-Befehle erst ab v0.20.0 ausliefern – ältere Clients kennen sie nicht und würden sie
    # quittieren, ohne zu sperren. Bis zum Update bleiben sie in der Warteschlange.
    dev_row = db.query_one("SELECT app_version, platform FROM devices WHERE id=?", (device_id,))
    platform = ((dev_row["platform"] if dev_row else "") or "windows").lower()
    # Android kennt die Auszeit von Anfang an; Windows erst ab 0.20.0.
    knows_timeout = platform in ("android", "ios") or \
        not _version_older((dev_row["app_version"] if dev_row else "") or "0", "0.20.0")
    out = []
    for r in rows:
        payload = json.loads(r["payload_json"])
        if r["type"] in (7, 8) and not knows_timeout:
            continue
        # Nachricht ans Kind: nur Android ab 1.0.9 kann sie zeigen – bis dahin bleibt sie in der Warteschlange.
        if r["type"] == 10 and not (platform == "ios" or (platform == "android" and
                                    not _version_older((dev_row["app_version"] if dev_row else "") or "0", MESSAGE_MIN_ANDROID))):
            continue
        out.append(payload["envelope"])

    # Gemeinsames Tageskonto: was die anderen Geräte des Kindes heute schon verbraucht haben (signiert,
    # bei jedem Abruf frisch, nicht gespeichert, braucht kein Ack). Nur an Geräte, die den Befehl kennen.
    try:
        min_ver = {"android": shared.MIN_ANDROID, "ios": shared.MIN_IOS}.get(platform, shared.MIN_WINDOWS)
        if dev_row and not _version_older(dev_row["app_version"] or "0", min_ver):
            o = shared.others_today(device_id)
            if o:
                from .signing import sign_command
                day, minutes, detail = o
                out.append(sign_command(device_id, "sharedUsage", expires_in=600,
                                        payload={"day": day, "minutes": str(minutes), "detail": detail[:200]})["envelope"])
    except Exception:
        pass

    # Selbst-nachlieferndes Update: solange das Gerät eine ÄLTERE Version meldet, bei JEDEM
    # Poll erneut ein Update-Kommando anbieten (robust gegen abgebrochene Downloads bei wackligem Netz).
    # Je Plattform eigenes Ziel – ein Android-Gerät (1.x) darf nie das Windows-Update (0.x) bekommen.
    # iOS installiert nie still (Apple) – die App zeigt selbst „Neue Version installieren“.
    try:
        if platform == "ios":
            target = ""
        elif platform == "android":
            t = android.update_target()
            target = t[0] if t else ""
        else:
            target = settings.update_version if (settings.update_url and settings.update_sha256) else ""
        if target and dev_row and _version_older(dev_row["app_version"] or "0", target):
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
def update_manifest(channel: str = "stable", platform: str = "windows"):
    if platform == "ios":
        from . import ios
        if not ios.version():
            raise HTTPException(status_code=404, detail="Kein iOS-Build bereitgestellt")
        return {"version": ios.version(), "url": ios.public("/ios")}
    if platform == "android":
        t = android.update_target()
        if not t:
            raise HTTPException(status_code=404, detail="Kein Android-Update bereitgestellt")
        from .signing import sign_bytes
        ver, url, sha = t
        return {"version": ver, "url": url, "sha256": sha,
                "signatureBase64": sign_bytes(android.manifest_message(ver, sha))}
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
