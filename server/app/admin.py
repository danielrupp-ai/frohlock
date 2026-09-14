"""Admin-Oberfläche der Brücke (Session-geschützt)."""
from __future__ import annotations

import json
import secrets
import time
from pathlib import Path

from fastapi import APIRouter, Form, Request
from fastapi.responses import HTMLResponse, RedirectResponse
from fastapi.templating import Jinja2Templates

from . import configbuilder, db
from .config import settings
from .security import pin_hash, verify_password
from .signing import sign_command

router = APIRouter()
templates = Jinja2Templates(directory=str(Path(__file__).parent / "templates"))

DAYS = [(1, "Mo"), (2, "Di"), (3, "Mi"), (4, "Do"), (5, "Fr"), (6, "Sa"), (0, "So")]


def _is_admin(request: Request) -> bool:
    return bool(request.session.get("admin_user"))


def _hhmm(minute: int) -> str:
    return f"{minute // 60:02d}:{minute % 60:02d}"


def _to_min(hhmm: str) -> int:
    h, m = hhmm.split(":")
    return int(h) * 60 + int(m)


# ---------- Login ----------
@router.get("/login", response_class=HTMLResponse)
def login_form(request: Request):
    return templates.TemplateResponse("login.html", {"request": request, "error": None})


@router.post("/login")
def login(request: Request, username: str = Form(...), password: str = Form(...)):
    pw_hash = request.app.state.admin_password_hash
    if username == settings.admin_user and verify_password(password, pw_hash):
        request.session["admin_user"] = username
        return RedirectResponse("/", status_code=303)
    return templates.TemplateResponse("login.html", {"request": request, "error": "Falsche Zugangsdaten"})


@router.get("/logout")
def logout(request: Request):
    request.session.clear()
    return RedirectResponse("/login", status_code=303)


# ---------- Dashboard ----------
@router.get("/", response_class=HTMLResponse)
def dashboard(request: Request):
    if not _is_admin(request):
        return RedirectResponse("/login", status_code=303)
    devices = db.query("SELECT * FROM devices ORDER BY created_at DESC")
    codes = db.query("SELECT * FROM pairing_codes WHERE used_device_id IS NULL AND expires_at > ? ORDER BY created_at DESC",
                     (int(time.time()),))
    now = int(time.time())
    return templates.TemplateResponse("dashboard.html", {
        "request": request, "devices": devices, "codes": codes, "now": now,
    })


@router.post("/admin/pairing")
def create_pairing(request: Request, device_name: str = Form("Gerät")):
    if not _is_admin(request):
        return RedirectResponse("/login", status_code=303)
    code = f"{secrets.randbelow(1000000):06d}"
    now = int(time.time())
    with db.tx() as c:
        c.execute("INSERT INTO pairing_codes(code, device_name, created_at, expires_at) VALUES (?,?,?,?)",
                  (code, device_name, now, now + 24 * 3600))
    db.audit(request.session["admin_user"], "pairing-create", None, f"{code} / {device_name}")
    return RedirectResponse("/", status_code=303)


# ---------- Gerät-Detail ----------
@router.get("/admin/devices/{device_id}", response_class=HTMLResponse)
def device_detail(request: Request, device_id: str):
    if not _is_admin(request):
        return RedirectResponse("/login", status_code=303)
    dev = db.query_one("SELECT * FROM devices WHERE id=?", (device_id,))
    if not dev:
        return RedirectResponse("/", status_code=303)
    stored = configbuilder.get_stored(device_id)
    version, draft = (stored[0], stored[1]) if stored else (0, {})
    windows = draft.get("windows", [])
    for w in windows:
        w["startHHMM"] = _hhmm(w.get("startMinute", 0))
        w["endHHMM"] = _hhmm(w.get("endMinute", 0))
        w["dayLabels"] = ", ".join(lbl for d, lbl in DAYS if d in w.get("days", [])) or "täglich"
    audit_rows = db.query("SELECT * FROM audit WHERE device_id=? ORDER BY ts DESC LIMIT 30", (device_id,))

    # Nutzung: letzte 7 Tage (0 auffüllen, wo nichts gemeldet).
    import datetime
    rows = db.query("SELECT day, minutes FROM usage_daily WHERE device_id=?", (device_id,))
    by_day = {r["day"]: r["minutes"] for r in rows}
    today = datetime.date.today()
    usage_week = []
    for i in range(6, -1, -1):
        d = today - datetime.timedelta(days=i)
        key = d.strftime("%Y-%m-%d")
        usage_week.append({"day": d.strftime("%a %d.%m."), "minutes": by_day.get(key, 0)})
    max_min = max([u["minutes"] for u in usage_week] + [1])
    budget = draft.get("dailyBudgetMinutes", 0)

    return templates.TemplateResponse("device.html", {
        "request": request, "dev": dev, "version": version, "draft": draft,
        "windows": windows, "days": DAYS, "audit": audit_rows, "now": int(time.time()),
        "has_pin": bool(draft.get("pinHash")),
        "usage_week": usage_week, "usage_max": max_min, "budget": budget,
        "usage_today": dev["usage_today"] if "usage_today" in dev.keys() else 0,
    })


def _load_draft(device_id: str) -> dict:
    stored = configbuilder.get_stored(device_id)
    return stored[1] if stored else {
        "windows": [], "pinHash": "", "pinSalt": "", "pinIterations": 210000,
        "unlockGraceMinutes": 60, "dailyBudgetMinutes": 0, "maxTrustedTimeStalenessMinutes": 720,
    }


@router.post("/admin/devices/{device_id}/window/add")
async def add_window(request: Request, device_id: str):
    if not _is_admin(request):
        return RedirectResponse("/login", status_code=303)
    form = await request.form()
    label = str(form.get("label", "") or "")
    start = str(form.get("start", "21:00"))
    end = str(form.get("end", "07:00"))
    days = [d for d, _ in DAYS if form.get(f"day_{d}")]

    draft = _load_draft(device_id)
    draft.setdefault("windows", []).append({
        "id": secrets.token_hex(8), "label": label or None, "days": days,
        "startMinute": _to_min(start), "endMinute": _to_min(end), "enabled": True,
    })
    v = configbuilder.save_draft(device_id, draft)
    db.audit(request.session["admin_user"], "window-add", device_id, f"{start}-{end} v{v}")
    return RedirectResponse(f"/admin/devices/{device_id}", status_code=303)


@router.post("/admin/devices/{device_id}/window/{win_id}/delete")
def del_window(request: Request, device_id: str, win_id: str):
    if not _is_admin(request):
        return RedirectResponse("/login", status_code=303)
    draft = _load_draft(device_id)
    draft["windows"] = [w for w in draft.get("windows", []) if w.get("id") != win_id]
    configbuilder.save_draft(device_id, draft)
    db.audit(request.session["admin_user"], "window-del", device_id, win_id)
    return RedirectResponse(f"/admin/devices/{device_id}", status_code=303)


@router.post("/admin/devices/{device_id}/pin")
def set_pin(request: Request, device_id: str, pin: str = Form(...)):
    if not _is_admin(request):
        return RedirectResponse("/login", status_code=303)
    h, s, it = pin_hash(pin)
    draft = _load_draft(device_id)
    draft.update({"pinHash": h, "pinSalt": s, "pinIterations": it})
    v = configbuilder.save_draft(device_id, draft)
    db.audit(request.session["admin_user"], "pin-set", device_id, f"v{v}")
    return RedirectResponse(f"/admin/devices/{device_id}", status_code=303)


@router.post("/admin/devices/{device_id}/settings")
def set_settings(request: Request, device_id: str,
                 unlock_grace: int = Form(60), max_stale: int = Form(720), daily_budget: int = Form(0)):
    if not _is_admin(request):
        return RedirectResponse("/login", status_code=303)
    draft = _load_draft(device_id)
    draft.update({
        "unlockGraceMinutes": unlock_grace,
        "maxTrustedTimeStalenessMinutes": max_stale,
        "dailyBudgetMinutes": daily_budget,
    })
    configbuilder.save_draft(device_id, draft)
    db.audit(request.session["admin_user"], "settings", device_id, "")
    return RedirectResponse(f"/admin/devices/{device_id}", status_code=303)


@router.post("/admin/devices/{device_id}/command")
def enqueue_command(request: Request, device_id: str,
                    kind: str = Form(...), minutes: int = Form(60)):
    if not _is_admin(request):
        return RedirectResponse("/login", status_code=303)
    kind_map = {"unlock": "unlock", "lock": "lock", "update": "update",
                "uninstall": "uninstall", "ping": "ping", "resetpin": "resetPin",
                "updateconfig": "updateConfig"}
    ctype = kind_map.get(kind)
    if ctype:
        signed = sign_command(device_id, ctype,
                              unlock_minutes=minutes if ctype == "unlock" else None)
        now = int(time.time())
        with db.tx() as c:
            c.execute(
                """INSERT INTO commands(id, device_id, type, payload_json, issued_at, expires_at, created_at)
                   VALUES (?,?,?,?,?,?,?)""",
                (signed["commandId"], device_id, 0, json.dumps(signed), now, now + 3600, now),
            )
        db.audit(request.session["admin_user"], f"cmd:{ctype}", device_id, "")
    return RedirectResponse(f"/admin/devices/{device_id}", status_code=303)


@router.post("/admin/devices/{device_id}/delete")
def delete_device(request: Request, device_id: str):
    if not _is_admin(request):
        return RedirectResponse("/login", status_code=303)
    with db.tx() as c:
        c.execute("DELETE FROM commands WHERE device_id=?", (device_id,))
        c.execute("DELETE FROM device_config WHERE device_id=?", (device_id,))
        c.execute("DELETE FROM devices WHERE id=?", (device_id,))
    db.audit(request.session["admin_user"], "device-delete", device_id, "")
    return RedirectResponse("/", status_code=303)
