"""Admin-Oberfläche der Brücke (Session-geschützt)."""
from __future__ import annotations

import json
import secrets
import time
from pathlib import Path

from fastapi import APIRouter, Form, Request
from fastapi.responses import HTMLResponse, JSONResponse, RedirectResponse
from fastapi.templating import Jinja2Templates

from . import android, configbuilder, db, shared
from .config import settings
from .security import pin_hash, verify_password
from .signing import CMD, sign_command

router = APIRouter()
templates = Jinja2Templates(directory=str(Path(__file__).parent / "templates"))

DAYS = [(1, "Mo"), (2, "Di"), (3, "Mi"), (4, "Do"), (5, "Fr"), (6, "Sa"), (0, "So")]


def _is_admin(request: Request) -> bool:
    return bool(request.session.get("admin_user"))


def _hhmm(minute: int) -> str:
    return f"{minute // 60:02d}:{minute % 60:02d}"


# Gleiche Farben wie die Zeitleiste auf dem Kinder-Sperrbildschirm (UsageTimelineView.cs).
_BLOCK_COLORS = ["#FFB74D", "#4DD0E1", "#F06292", "#9CCC65", "#BA68C8", "#FFD54F"]
_WEEKDAYS = ["Montag", "Dienstag", "Mittwoch", "Donnerstag", "Freitag", "Samstag", "Sonntag"]


def _fmt_minutes(m: int) -> str:
    """120 -> "2 Std", 75 -> "1 Std 15 Min", 40 -> "40 Min" (wie beim Kind)."""
    if m < 60:
        return f"{m} Min"
    h, r = divmod(m, 60)
    return f"{h} Std" if r == 0 else f"{h} Std {r} Min"


def _berlin_now():
    import datetime
    try:
        from zoneinfo import ZoneInfo
        return datetime.datetime.now(ZoneInfo("Europe/Berlin"))
    except Exception:
        return datetime.datetime.now()


def _berlin_fmt(ts: int) -> str:
    import datetime
    try:
        from zoneinfo import ZoneInfo
        return datetime.datetime.fromtimestamp(ts, ZoneInfo("Europe/Berlin")).strftime("%d.%m. %H:%M")
    except Exception:
        return time.strftime("%d.%m. %H:%M", time.localtime(ts))


def _lock_state(dev, now: int) -> dict:
    """Ehrlicher Status fürs Eltern-Web. „gesperrt" heißt nur: FrohLock sperrt. Ist das Kind gerade in einer
    erlaubten App (Telegram/Signal) oder fehlt der Sperrbildschirm, ist das Handy trotzdem in Benutzung –
    das steht dann dabei (07.11.2026: Renat nutzte Telegram, das Web zeigte nur „gesperrt")."""
    keys = dev.keys()
    # iPhones melden sich nur alle ~15 Min (Apple weckt FrohLock nicht öfter) – nicht vorschnell „offline“.
    limit = 2400 if ("platform" in keys and (dev["platform"] or "") == "ios") else 300
    if not dev["last_seen"] or now - dev["last_seen"] > limit:
        return {"kind": "off", "label": "offline", "hint": ""}
    if not dev["locked"]:
        return {"kind": "open", "label": "frei", "hint": ""}
    is_android = "platform" in keys and (dev["platform"] or "") == "android"
    lock_visible = dev["lock_visible"] if "lock_visible" in keys and dev["lock_visible"] is not None else -1
    screen_on = ("screen_active" not in keys) or dev["screen_active"] == 1
    if is_android and screen_on and lock_visible == 0:
        info = android.device_info(dev["device_info"] if "device_info" in keys else None)
        apps = [str(a) for a in (info.get("allowedApps") or [])]
        if apps:
            hint = ("Das Handy ist gerade in Benutzung: " + ", ".join(apps) +
                    (" ist" if len(apps) == 1 else " sind") + " unter „Immer erlaubt“ freigegeben – diese Zeit zählt nicht aufs Limit. "
                    "Vollsperre: unten bei „Immer erlaubt“ alle Häkchen entfernen.")
        else:
            hint = ("Das Handy ist gerade in Benutzung, obwohl keine App erlaubt ist – der Sperrbildschirm fehlt. "
                    "Siehe „Auffälligkeiten“ und „Am Handy fehlt noch“.")
        return {"kind": "inuse", "label": "gesperrt – aber in Benutzung", "hint": hint}
    return {"kind": "locked", "label": "gesperrt", "hint": ""}


def _gap_alerts(dev) -> list[dict]:
    """Gemerkte ⚠-Lücken (neueste zuerst) mit Berliner Uhrzeit, für die Geräteseite."""
    import datetime
    raw = dev["gap_alerts"] if "gap_alerts" in dev.keys() and dev["gap_alerts"] else "[]"
    try:
        lines = json.loads(raw)
    except ValueError:
        return []
    try:
        from zoneinfo import ZoneInfo
        tz = ZoneInfo("Europe/Berlin")
    except Exception:
        tz = None
    out = []
    for ln in reversed(lines):
        ts, _, rest = ln.partition("\t")
        _, _, msg = rest.partition("\t")
        when = ts
        try:
            d = datetime.datetime.fromisoformat(ts[:19]).replace(tzinfo=datetime.timezone.utc)
            when = (d.astimezone(tz) if tz else d).strftime("%d.%m. %H:%M")
        except ValueError:
            pass
        out.append({"when": when, "msg": msg.replace("⚠ ", ""), "counted": "nachgezählt" in msg})
    return out[:15]


def _effective_budget(draft: dict, weekday_py: int) -> int:
    """Tageslimit wie im Client: Wochentag-Wert (0=So..6=Sa) > 0 überschreibt den Standard."""
    wb = ((draft.get("dailyBudgetByWeekday") or []) + [0] * 7)[:7]
    v = wb[(weekday_py + 1) % 7]
    return v if v > 0 else int(draft.get("dailyBudgetMinutes") or 0)


TIMEOUT_MINUTES = 15
TIMEOUT_TYPE = 7  # CMD["timeout"]


def _penalty_status(dev) -> dict:
    """Auszeit-Stand eines Geräts: läuft gerade (vom Gerät gemeldet) + noch nicht zugestellte Auszeiten."""
    now = int(time.time())
    pending = db.query_one(
        """SELECT COUNT(*) AS n FROM commands WHERE device_id=? AND type=? AND acked=0
               AND (expires_at IS NULL OR expires_at=0 OR expires_at>?)""",
        (dev["id"], TIMEOUT_TYPE, now))["n"]
    keys = dev.keys()
    secs = int(dev["penalty_seconds"] or 0) if "penalty_seconds" in keys else 0
    count = int(dev["penalty_count"] or 0) if "penalty_count" in keys else 0
    return {"seconds": secs, "count": count, "pending": pending,
            "countdown": f"{secs // 60}:{secs % 60:02d}",
            "active": secs > 0 or pending > 0}


def _timeline(segments: list, minutes: int, budget: int, now_minute: int | None) -> dict:
    """Baut die Zeitleiste „von wann bis wann" – dieselbe Logik wie auf dem Sperrbildschirm."""
    segs = []
    for seg in segments or []:
        try:
            start, end = int(seg.get("startMinute", 0)), int(seg.get("endMinute", 0))
        except (TypeError, ValueError):
            continue
        segs.append({"start": seg.get("start", _hhmm(start)), "end": seg.get("end", _hhmm(end)),
                     "start_min": start, "end_min": max(end, start + 1),
                     "minutes": max(1, int(seg.get("minutes", 0) or 0))})
    segs.sort(key=lambda x: x["start_min"])

    ref = now_minute if now_minute is not None else (segs[-1]["end_min"] if segs else 12 * 60)
    first = min([x["start_min"] for x in segs] + [ref])
    last = max([x["end_min"] for x in segs] + [ref])
    axis_start = min(first, 8 * 60) // 60 * 60
    axis_end = min(1440, (max(last, 20 * 60) + 59) // 60 * 60)
    span = max(60, axis_end - axis_start)

    def pct(m):
        return round((m - axis_start) * 100 / span, 2)

    for i, x in enumerate(segs):
        x["color"] = _BLOCK_COLORS[i % len(_BLOCK_COLORS)]
        x["left"] = pct(x["start_min"])
        x["width"] = max(0.6, pct(x["end_min"]) - pct(x["start_min"]))
        x["label"] = _fmt_minutes(x["minutes"])

    step = 120 if span > 10 * 60 else 60
    ticks = [{"left": pct(m), "label": f"{m // 60} Uhr"} for m in range(axis_start, axis_end + 1, step)]

    sum_segs = sum(x["minutes"] for x in segs)
    earlier = minutes - sum_segs
    parts = ([earlier] if earlier >= 2 else []) + [x["minutes"] for x in segs]
    if 2 <= len(parts) <= 8:
        calc = "Zusammengerechnet: " + " + ".join(_fmt_minutes(p) for p in parts) + " = " + _fmt_minutes(minutes)
    elif not segs and minutes == 0:
        calc = "An diesem Tag wurde keine Bildschirmzeit gezählt."
    else:
        calc = ""
    return {
        "segments": segs, "ticks": ticks, "total": _fmt_minutes(minutes), "minutes": minutes,
        "budget": budget, "budget_label": _fmt_minutes(budget) if budget > 0 else "",
        "over": budget > 0 and minutes >= budget,
        "earlier": _fmt_minutes(earlier) if earlier >= 2 else "",
        "no_times": minutes > 0 and not segs,
        "calc": calc,
        "now_left": pct(now_minute) if now_minute is not None and axis_start <= now_minute <= axis_end else None,
    }


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
        return RedirectResponse("/admin", status_code=303)
    return templates.TemplateResponse("login.html", {"request": request, "error": "Falsche Zugangsdaten"})


@router.get("/logout")
def logout(request: Request):
    request.session.clear()
    return RedirectResponse("/login", status_code=303)


# ---------- Dashboard ----------
@router.get("/admin", response_class=HTMLResponse)
def dashboard(request: Request):
    if not _is_admin(request):
        return RedirectResponse("/login", status_code=303)
    devices = db.query("SELECT * FROM devices ORDER BY created_at DESC")
    penalties = {d["id"]: _penalty_status(d) for d in devices}
    lock_states = {d["id"]: _lock_state(d, int(time.time())) for d in devices}
    codes = db.query("SELECT * FROM pairing_codes WHERE used_device_id IS NULL AND expires_at > ? ORDER BY created_at DESC",
                     (int(time.time()),))
    now = int(time.time())
    nb = _berlin_now()
    shared_by_dev = {}
    for d in devices:
        if d["child"]:
            shared_by_dev[d["id"]] = shared.summary(d["child"], nb.strftime("%Y-%m-%d"), nb.weekday())
    return templates.TemplateResponse("dashboard.html", {
        "request": request, "devices": devices, "codes": codes, "now": now, "shared_by_dev": shared_by_dev,
        "penalties": penalties, "timeout_minutes": TIMEOUT_MINUTES, "lock_states": lock_states,
        "flash": request.query_params.get("ok", ""),
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
    return RedirectResponse("/admin", status_code=303)


# ---------- Gerät-Detail ----------
@router.get("/admin/devices/{device_id}", response_class=HTMLResponse)
def device_detail(request: Request, device_id: str):
    if not _is_admin(request):
        return RedirectResponse("/login", status_code=303)
    dev = db.query_one("SELECT * FROM devices WHERE id=?", (device_id,))
    if not dev:
        return RedirectResponse("/admin", status_code=303)
    stored = configbuilder.get_stored(device_id)
    version, draft = (stored[0], stored[1]) if stored else (0, {})
    windows = draft.get("windows", [])
    for w in windows:
        w["startHHMM"] = _hhmm(w.get("startMinute", 0))
        w["endHHMM"] = _hhmm(w.get("endMinute", 0))
        w["dayLabels"] = ", ".join(lbl for d, lbl in DAYS if d in w.get("days", [])) or "täglich"
    audit_rows = db.query("SELECT * FROM audit WHERE device_id=? ORDER BY ts DESC LIMIT 30", (device_id,))

    # Nutzung: letzte 14 Tage (0 auffüllen, wo nichts gemeldet), Tag per ?day= wählbar.
    import datetime
    rows = db.query("SELECT * FROM usage_daily WHERE device_id=?", (device_id,))
    by_day = {r["day"]: r for r in rows}
    now_berlin = _berlin_now()
    today = now_berlin.date()
    today_key = today.strftime("%Y-%m-%d")
    sel_key = request.query_params.get("day") or today_key
    usage_week = []
    for i in range(13, -1, -1):
        d = today - datetime.timedelta(days=i)
        key = d.strftime("%Y-%m-%d")
        r = by_day.get(key)
        usage_week.append({"day": f"{_WEEKDAYS[d.weekday()][:2]} {d:%d.%m}", "key": key,
                           "minutes": r["minutes"] if r else 0,
                           "budget": (r["budget"] if r and r["budget"] else _effective_budget(draft, d.weekday())),
                           "selected": key == sel_key})
    max_min = max([u["minutes"] for u in usage_week] + [u["budget"] for u in usage_week] + [1])
    budget = draft.get("dailyBudgetMinutes", 0)

    # Zeitleiste des gewählten Tages – gleiche Darstellung wie beim Kind auf dem Sperrbildschirm.
    try:
        sel_date = datetime.date.fromisoformat(sel_key)
    except ValueError:
        sel_date, sel_key = today, today_key
    sel_row = by_day.get(sel_key)
    sel_segments = []
    if sel_row is not None and sel_row["segments_json"]:
        try:
            sel_segments = json.loads(sel_row["segments_json"])
        except ValueError:
            sel_segments = []
    sel_budget = (sel_row["budget"] if sel_row is not None and sel_row["budget"]
                  else _effective_budget(draft, sel_date.weekday()))
    is_today = sel_key == today_key
    timeline = _timeline(sel_segments, sel_row["minutes"] if sel_row is not None else 0, sel_budget,
                         now_berlin.hour * 60 + now_berlin.minute if is_today else None)
    timeline["title"] = ("Heute" if is_today else
                         ("Gestern" if sel_date == today - datetime.timedelta(days=1) else _WEEKDAYS[sel_date.weekday()])) \
        + f", {sel_date:%d.%m.%Y}"
    timeline["is_today"] = is_today

    # Wochentag-Budgets auf 7 Werte normalisieren (0=So..6=Sa) und als Mo..So-Liste anzeigen.
    wb = (draft.get("dailyBudgetByWeekday") or [])
    wb = (wb + [0] * 7)[:7]
    weekday_budgets = [{"idx": d, "label": lbl, "value": wb[d]} for d, lbl in DAYS]

    gap_alerts = _gap_alerts(dev)
    keys = dev.keys()
    is_android = ("platform" in keys and (dev["platform"] or "") == "android")
    is_ios = ("platform" in keys and (dev["platform"] or "") == "ios")
    ios_info = {}
    if is_ios:
        try:
            ios_info = json.loads(dev["device_info"] or "{}") or {}
        except Exception:
            ios_info = {}
        from . import ios as ios_mod
        ios_info["latest"] = ios_mod.version()
    others = [d for d in db.query("SELECT id, name, platform FROM devices WHERE id<>? ORDER BY name", (device_id,))]

    allowed = draft.get("alwaysAllowedApps", configbuilder.DEFAULT_ALLOWED_APPS)
    known_pkgs = {p for _, pkgs in configbuilder.KNOWN_MESSENGERS for p in pkgs}
    allowed_now = [n for n, pkgs in configbuilder.KNOWN_MESSENGERS if any(p in allowed for p in pkgs)] + \
        [p for p in allowed if p not in known_pkgs]
    child = dev["child"] or ""
    child_summary = shared.summary(child, today_key, today.weekday()) if child else None
    if child_summary:
        # Limit-Felder zeigen das gemeinsame Limit des Kindes.
        cwb = ((child_summary["by_weekday"] or []) + [0] * 7)[:7]
        weekday_budgets = [{"idx": d, "label": lbl, "value": cwb[d]} for d, lbl in DAYS]
    pending_msgs = []
    for r in db.query("SELECT payload_json, not_before, acked, expires_at FROM commands WHERE device_id=? AND type=10 "
                      "ORDER BY created_at DESC LIMIT 5", (device_id,)):
        try:
            env = json.loads(r["payload_json"])["envelope"]
            import base64
            body = json.loads(base64.b64decode(env["payloadBase64"]))
            p = body.get("payload", {})
        except Exception:
            p = {}
        nb = r["not_before"] or 0
        pending_msgs.append({"title": p.get("title", ""), "acked": bool(r["acked"]),
                             "from": _berlin_fmt(nb) if nb else "sofort",
                             "expired": (r["expires_at"] or 0) < int(time.time())})
    return templates.TemplateResponse("device.html", {
        "messages": pending_msgs,
        "child": child, "child_summary": child_summary, "children": shared.all_children(),
        "is_android": is_android, "is_ios": is_ios, "ios_info": ios_info,
        "lock_state": _lock_state(dev, int(time.time())),
        "allowed_now": allowed_now,
        "messengers": [{"name": n, "key": pkgs[0], "on": any(p in allowed for p in pkgs)} for n, pkgs in configbuilder.KNOWN_MESSENGERS],
        "allowed_extra": ", ".join(p for p in allowed if p not in known_pkgs),
        "android_info": android.device_info(dev["device_info"] if "device_info" in keys else None),
        "android_update": android.apk_version(),
        "other_devices": others,
        "request": request, "dev": dev, "version": version, "draft": draft,
        "windows": windows, "days": DAYS, "audit": audit_rows, "now": int(time.time()),
        "has_pin": bool(draft.get("pinHash")),
        "usage_week": usage_week, "usage_max": max_min, "budget": budget,
        "weekday_budgets": weekday_budgets,
        "timeline": timeline,
        "gap_alerts": gap_alerts,
        "penalty": _penalty_status(dev), "timeout_minutes": TIMEOUT_MINUTES,
        "flash": request.query_params.get("ok", ""),
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
async def set_settings(request: Request, device_id: str):
    if not _is_admin(request):
        return RedirectResponse("/login", status_code=303)
    form = await request.form()

    def _int(name, default=0):
        try:
            return max(0, int(form.get(name, default) or default))
        except (TypeError, ValueError):
            return default

    # Wochentag-Budgets (0=So..6=Sa); 0 = Standard verwenden.
    weekday = [_int(f"wb_{i}", 0) for i in range(7)]
    if not any(weekday):
        weekday = []  # nichts gesetzt -> Feld leeren

    draft = _load_draft(device_id)
    draft.update({
        "unlockGraceMinutes": _int("unlock_grace", 60),
        "maxTrustedTimeStalenessMinutes": _int("max_stale", 720),
        "dailyBudgetMinutes": _int("daily_budget", 0),
        "dailyBudgetByWeekday": weekday,
    })
    configbuilder.save_draft(device_id, draft)
    child = shared.child_of(device_id)
    if child:
        # Gemeinsames Tageskonto: Limit gilt für ALLE Geräte des Kindes.
        shared.set_child_budget(child, _int("daily_budget", 0), weekday)
        shared.bump_child(child)
    db.audit(request.session["admin_user"], "settings", device_id, f"Konto {child}" if child else "")
    return RedirectResponse(f"/admin/devices/{device_id}", status_code=303)


@router.post("/admin/devices/{device_id}/child")
def set_child(request: Request, device_id: str, child: str = Form("")):
    """Gerät einem Kind zuordnen – alle Geräte eines Kindes teilen sich ein Tageslimit."""
    if not _is_admin(request):
        return RedirectResponse("/login", status_code=303)
    name = shared.assign(device_id, child)
    db.audit(request.session["admin_user"], "child", device_id, name or "(keinem Kind)")
    return RedirectResponse(f"/admin/devices/{device_id}?ok={'konto' if name else 'kontoweg'}#konto", status_code=303)


@router.post("/admin/devices/{device_id}/message")
def send_message(request: Request, device_id: str, title: str = Form(""), text: str = Form(...),
                 button: str = Form("OK 💛"), show_from: str = Form("")):
    """Nachricht ans Kind (Vollbild am Handy, bis es „Danke" tippt), optional erst ab einem Zeitpunkt."""
    if not _is_admin(request):
        return RedirectResponse("/login", status_code=303)
    import datetime
    not_before = 0
    if show_from:
        try:
            dt = datetime.datetime.fromisoformat(show_from)
            from zoneinfo import ZoneInfo
            not_before = int(dt.replace(tzinfo=ZoneInfo("Europe/Berlin")).timestamp())
        except Exception:
            not_before = 0
    from .api import enqueue_message
    cid = enqueue_message(device_id, title.strip(), text.strip(), button.strip() or "OK 💛", not_before)
    db.audit(request.session["admin_user"], "message", device_id, f"{title.strip()[:60]} ab {show_from or 'sofort'} ({cid[:8]})")
    return RedirectResponse(f"/admin/devices/{device_id}?ok=nachricht#nachricht", status_code=303)


@router.post("/admin/devices/{device_id}/command")
def enqueue_command(request: Request, device_id: str,
                    kind: str = Form(...), minutes: int = Form(60), back: str = Form("")):
    if not _is_admin(request):
        return RedirectResponse("/login", status_code=303)
    kind_map = {"unlock": "unlock", "lock": "lock", "update": "update",
                "uninstall": "uninstall", "ping": "ping", "resetpin": "resetPin",
                "updateconfig": "updateConfig", "timeout": "timeout", "cleartimeout": "clearTimeout"}
    ctype = kind_map.get(kind)
    flash = ""
    if ctype:
        if ctype == "timeout":
            minutes = TIMEOUT_MINUTES
        # Auszeit muss auch zugestellt werden, wenn der Laptop erst Tage später wieder an ist.
        expires_in = 7 * 86400 if ctype in ("timeout", "clearTimeout") else 3600
        signed = sign_command(device_id, ctype,
                              unlock_minutes=minutes if ctype in ("unlock", "timeout") else None,
                              expires_in=expires_in)
        now = int(time.time())
        with db.tx() as c:
            if ctype == "clearTimeout":
                # Noch nicht zugestellte Auszeiten gleich mit zurücknehmen.
                c.execute("UPDATE commands SET acked=1 WHERE device_id=? AND type=? AND acked=0",
                          (device_id, TIMEOUT_TYPE))
            c.execute(
                """INSERT INTO commands(id, device_id, type, payload_json, issued_at, expires_at, created_at)
                   VALUES (?,?,?,?,?,?,?)""",
                (signed["commandId"], device_id, CMD[ctype], json.dumps(signed), now, now + expires_in, now),
            )
        db.audit(request.session["admin_user"], f"cmd:{ctype}", device_id, f"{minutes} min" if ctype == "timeout" else "")
        flash = {"timeout": "auszeit", "clearTimeout": "aufgehoben"}.get(ctype, "")
    target = "/admin" if back == "dashboard" else f"/admin/devices/{device_id}"
    return RedirectResponse(target + (f"?ok={flash}" if flash else ""), status_code=303)


@router.post("/admin/devices/{device_id}/delete")
def delete_device(request: Request, device_id: str):
    if not _is_admin(request):
        return RedirectResponse("/login", status_code=303)
    with db.tx() as c:
        c.execute("DELETE FROM commands WHERE device_id=?", (device_id,))
        c.execute("DELETE FROM device_config WHERE device_id=?", (device_id,))
        c.execute("DELETE FROM devices WHERE id=?", (device_id,))
    db.audit(request.session["admin_user"], "device-delete", device_id, "")
    return RedirectResponse("/admin", status_code=303)


# ---------- Android: Einrichtung per QR-Code ----------

def _android_ready() -> dict:
    """Was fehlt noch, damit der QR-Code funktioniert? (APK auf dem Server + Zertifikats-Prüfsumme)."""
    import os
    return {"apk": os.path.exists(android.apk_path()), "cert": bool(android.cert_checksum()),
            "version": android.apk_version()}


@router.get("/admin/android/new", response_class=HTMLResponse)
def android_new_form(request: Request):
    if not _is_admin(request):
        return RedirectResponse("/login", status_code=303)
    devices = db.query("SELECT id, name, platform FROM devices ORDER BY name")
    return templates.TemplateResponse("android_new.html", {
        "request": request, "devices": devices, "ready": _android_ready(),
    })


def _qr_page(request: Request, code_row, wifi_ssid: str = "", wifi_password: str = "", simple: bool = False):
    ready = _android_ready()
    payload = android.provisioning_payload(code_row["code"], code_row["device_name"], wifi_ssid, wifi_password, simple=simple)
    template_name = None
    keys = code_row.keys()
    if "template_device_id" in keys and code_row["template_device_id"]:
        t = db.query_one("SELECT name FROM devices WHERE id=?", (code_row["template_device_id"],))
        template_name = t["name"] if t else None
    link = android.install_link(code_row["code"])
    return templates.TemplateResponse("android_qr.html", {
        "request": request, "code": code_row, "ready": ready,
        "qr": android.qr_svg(payload) if ready["apk"] and ready["cert"] else None,
        "qr_dense": android.qr_version(payload) > 16,
        "install_link": link, "install_qr": android.qr_svg(link) if ready["apk"] else None,
        "with_wifi": bool(wifi_ssid.strip()), "template_name": template_name,
        "admin_component": android.ADMIN_COMPONENT, "now": int(time.time()), "simple": simple,
    })


@router.post("/admin/android/new", response_class=HTMLResponse)
def android_new(request: Request, device_name: str = Form("Handy"), template_device_id: str = Form(""),
                wifi_ssid: str = Form(""), wifi_password: str = Form("")):
    if not _is_admin(request):
        return RedirectResponse("/login", status_code=303)
    code = f"{secrets.randbelow(1000000):06d}"
    now = int(time.time())
    template = template_device_id.strip() or None
    if template and not db.query_one("SELECT id FROM devices WHERE id=?", (template,)):
        template = None
    with db.tx() as c:
        c.execute("""INSERT INTO pairing_codes(code, device_name, created_at, expires_at, platform, template_device_id)
                     VALUES (?,?,?,?,?,?)""",
                  (code, device_name.strip() or "Handy", now, now + 24 * 3600, "android", template))
    db.audit(request.session["admin_user"], "pairing-create", None, f"{code} / {device_name} (Android-QR)")
    row = db.query_one("SELECT * FROM pairing_codes WHERE code=?", (code,))
    # WLAN-Passwort steht NUR in dieser einen Antwort (im QR-Code), es wird nirgends gespeichert.
    return _qr_page(request, row, wifi_ssid, wifi_password)


@router.get("/admin/android/qr/{code}", response_class=HTMLResponse)
def android_qr(request: Request, code: str):
    if not _is_admin(request):
        return RedirectResponse("/login", status_code=303)
    row = db.query_one("SELECT * FROM pairing_codes WHERE code=?", (code,))
    if not row:
        return RedirectResponse("/admin", status_code=303)
    return _qr_page(request, row, simple=request.query_params.get("einfach") == "1")


@router.get("/admin/android/qr/{code}/status")
def android_qr_status(request: Request, code: str):
    if not _is_admin(request):
        return JSONResponse({"error": "login"}, status_code=401)
    row = db.query_one("SELECT used_device_id, expires_at FROM pairing_codes WHERE code=?", (code,))
    if not row:
        return JSONResponse({"used": False, "expired": True})
    return JSONResponse({"used": bool(row["used_device_id"]), "deviceId": row["used_device_id"],
                         "expired": row["expires_at"] < int(time.time())})


@router.post("/admin/devices/{device_id}/copy-from")
def copy_from(request: Request, device_id: str, source_id: str = Form(...)):
    """Zeitplan, Limits und PIN eines anderen Geräts übernehmen (z. B. Handy = Laptop)."""
    if not _is_admin(request):
        return RedirectResponse("/login", status_code=303)
    name = configbuilder.copy_settings(source_id, device_id)
    db.audit(request.session["admin_user"], "settings-copy", device_id, f"von {name}" if name else "Quelle ohne Einstellungen")
    return RedirectResponse(f"/admin/devices/{device_id}?ok={'kopiert' if name else 'nichtskopiert'}", status_code=303)


@router.post("/admin/devices/{device_id}/allowed-apps")
async def set_allowed_apps(request: Request, device_id: str):
    """Android: welche Apps auch während der Sperre offen bleiben (z. B. Telegram, Signal)."""
    if not _is_admin(request):
        return RedirectResponse("/login", status_code=303)
    form = await request.form()
    apps: list[str] = []
    for name, pkgs in configbuilder.KNOWN_MESSENGERS:
        if form.get(f"m_{pkgs[0]}"):
            apps += pkgs
    import re
    for p in re.split(r"[\s,;]+", str(form.get("extra", "") or "")):
        if re.fullmatch(r"[A-Za-z][\w]*(\.[A-Za-z_][\w]*)+", p) and p not in apps:
            apps.append(p)
    draft = _load_draft(device_id)
    draft["alwaysAllowedApps"] = apps
    v = configbuilder.save_draft(device_id, draft)
    db.audit(request.session["admin_user"], "allowed-apps", device_id, ", ".join(apps) + f" v{v}")
    return RedirectResponse(f"/admin/devices/{device_id}?ok=apps#apps", status_code=303)


# ---------------- iPhone ----------------

@router.get("/admin/ios/new", response_class=HTMLResponse)
def ios_new_form(request: Request):
    if not _is_admin(request):
        return RedirectResponse("/login", status_code=303)
    from . import ios
    devices = db.query("SELECT id, name, platform FROM devices ORDER BY name")
    return templates.TemplateResponse("ios_admin.html", {"request": request, "code": None, "devices": devices,
                                                         "waiting": ios.waiting()})


@router.post("/admin/ios/new")
def ios_new(request: Request, device_name: str = Form("iPhone"), template_device_id: str = Form("")):
    if not _is_admin(request):
        return RedirectResponse("/login", status_code=303)
    code = f"{secrets.randbelow(1000000):06d}"
    now = int(time.time())
    template = template_device_id.strip() or None
    if template and not db.query_one("SELECT id FROM devices WHERE id=?", (template,)):
        template = None
    with db.tx() as c:
        c.execute("""INSERT INTO pairing_codes(code, device_name, created_at, expires_at, platform, template_device_id)
                     VALUES (?,?,?,?,?,?)""",
                  (code, device_name.strip() or "iPhone", now, now + 7 * 24 * 3600, "ios", template))
    db.audit(request.session["admin_user"], "pairing-create", None, f"{code} / {device_name} (iPhone)")
    return RedirectResponse(f"/admin/ios/{code}", status_code=303)


@router.get("/admin/ios/{code}", response_class=HTMLResponse)
def ios_code_page(request: Request, code: str):
    if not _is_admin(request):
        return RedirectResponse("/login", status_code=303)
    from . import ios
    row = db.query_one("SELECT * FROM pairing_codes WHERE code=?", (code,))
    if not row:
        return RedirectResponse("/admin", status_code=303)
    link = ios.page_link(code)
    return templates.TemplateResponse("ios_admin.html", {
        "request": request, "code": row, "link": link, "qr": android.qr_svg(link),
        "expires": _berlin_fmt(row["expires_at"]), "used": bool(row["used_device_id"]),
        "enrollments": ios.enrollments(code),
    })
