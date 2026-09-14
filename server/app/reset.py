"""Öffentlicher PIN-Reset per E-Mail-Link (für Eltern)."""
from __future__ import annotations

import secrets
import time
from pathlib import Path

from fastapi import APIRouter, Form, Request
from fastapi.responses import HTMLResponse
from fastapi.templating import Jinja2Templates

from . import configbuilder, db, emailer
from .config import settings
from .security import pin_hash

router = APIRouter()
templates = Jinja2Templates(directory=str(Path(__file__).parent / "templates"))

TOKEN_TTL = 30 * 60  # 30 Minuten


@router.get("/forgot", response_class=HTMLResponse)
def forgot_form(request: Request):
    return templates.TemplateResponse("forgot.html", {"request": request, "sent": False})


@router.post("/forgot", response_class=HTMLResponse)
def forgot_submit(request: Request, email: str = Form(...)):
    email = email.strip().lower()
    now = int(time.time())
    # Alle Geräte mit dieser Reset-E-Mail (case-insensitive).
    devices = db.query("SELECT id, name FROM devices WHERE lower(reset_email) = ?", (email,))
    for d in devices:
        token = secrets.token_urlsafe(32)
        with db.tx() as c:
            c.execute("INSERT INTO reset_tokens(token, device_id, created_at, expires_at) VALUES (?,?,?,?)",
                      (token, d["id"], now, now + TOKEN_TTL))
        url = f"{settings.public_base_url.rstrip('/')}/reset/{token}"
        emailer.send_reset_link(email, url, d["name"])
        db.audit("eltern", "pin-reset-request", d["id"], email)

    # Immer neutrale Antwort (keine Enumeration, ob die E-Mail existiert).
    return templates.TemplateResponse("forgot.html", {"request": request, "sent": True})


def _valid_token(token: str):
    now = int(time.time())
    row = db.query_one("SELECT * FROM reset_tokens WHERE token = ?", (token,))
    if not row or row["used"] or row["expires_at"] < now:
        return None
    return row


@router.get("/reset/{token}", response_class=HTMLResponse)
def reset_form(request: Request, token: str):
    row = _valid_token(token)
    if not row:
        return templates.TemplateResponse("reset.html",
                                          {"request": request, "invalid": True, "token": token, "done": False})
    dev = db.query_one("SELECT name FROM devices WHERE id=?", (row["device_id"],))
    return templates.TemplateResponse("reset.html", {
        "request": request, "invalid": False, "done": False, "token": token,
        "device_name": dev["name"] if dev else "",
    })


@router.post("/reset/{token}", response_class=HTMLResponse)
def reset_submit(request: Request, token: str, pin: str = Form(...)):
    row = _valid_token(token)
    if not row:
        return templates.TemplateResponse("reset.html",
                                          {"request": request, "invalid": True, "token": token, "done": False})
    if len(pin.strip()) < 4:
        dev = db.query_one("SELECT name FROM devices WHERE id=?", (row["device_id"],))
        return templates.TemplateResponse("reset.html", {
            "request": request, "invalid": False, "done": False, "token": token,
            "device_name": dev["name"] if dev else "", "error": "Der PIN muss mindestens 4 Zeichen haben.",
        })

    device_id = row["device_id"]
    h, s, it = pin_hash(pin.strip())
    stored = configbuilder.get_stored(device_id)
    draft = stored[1] if stored else {}
    draft.update({"pinHash": h, "pinSalt": s, "pinIterations": it})
    version = configbuilder.save_draft(device_id, draft)

    with db.tx() as c:
        c.execute("UPDATE reset_tokens SET used=1 WHERE token=?", (token,))
    db.audit("eltern", "pin-reset-done", device_id, f"v{version}")

    return templates.TemplateResponse("reset.html", {"request": request, "invalid": False, "done": True, "token": token})
