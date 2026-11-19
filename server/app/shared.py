"""Gemeinsames Tageskonto je Kind (19.11.2026, Daniel: „2 Stunden am Tag, egal ob PC oder Handy").

Mehrere Geräte, die demselben Kind gehören (Spalte devices.child), teilen sich EIN Tageslimit:
- Das Limit steht in der Tabelle `children` und überschreibt beim Bau der Config die Gerätewerte.
- Jedes Gerät zählt weiter nur seine eigene Zeit und meldet sie im Heartbeat (usage_daily).
- Der Server schickt jedem Gerät signiert, wie viel die ANDEREN Geräte heute schon verbraucht haben
  (Fernbefehl „sharedUsage"). Das Gerät sperrt bei eigene + andere ≥ Limit.

Gleichzeitig genutzte Geräte zählen doppelt (1 Min Handy + 1 Min Laptop = 2 Min) – gewollt, das Konto
misst Gerätezeit, nicht Wanduhrzeit.
"""
from __future__ import annotations

import json
import time

from . import db

# Ab diesen Versionen kennen die Geräte den Befehl „sharedUsage" (ältere würden ihn protokollieren und ignorieren).
MIN_WINDOWS = "0.29.0"
MIN_ANDROID = "1.0.8"


def norm_name(name: str | None) -> str:
    return " ".join((name or "").split())[:40]


def child_of(device_id: str) -> str:
    row = db.query_one("SELECT child FROM devices WHERE id=?", (device_id,))
    return norm_name(row["child"]) if row and row["child"] else ""


def get_child(name: str) -> dict | None:
    name = norm_name(name)
    if not name:
        return None
    row = db.query_one("SELECT * FROM children WHERE name=? COLLATE NOCASE", (name,))
    if not row:
        return None
    try:
        wb = json.loads(row["budget_by_weekday"]) if row["budget_by_weekday"] else []
    except ValueError:
        wb = []
    return {"name": row["name"], "dailyBudgetMinutes": int(row["daily_budget"] or 0),
            "dailyBudgetByWeekday": wb if isinstance(wb, list) and len(wb) == 7 else []}


def siblings(device_id: str) -> list:
    """Andere Geräte desselben Kindes (ohne das Gerät selbst)."""
    child = child_of(device_id)
    if not child:
        return []
    return db.query("SELECT * FROM devices WHERE child=? COLLATE NOCASE AND id<>? ORDER BY name", (child, device_id))


def members(child: str) -> list:
    child = norm_name(child)
    if not child:
        return []
    return db.query("SELECT * FROM devices WHERE child=? COLLATE NOCASE ORDER BY name", (child,))


def all_children() -> list[str]:
    rows = db.query("SELECT DISTINCT child FROM devices WHERE child IS NOT NULL AND child<>'' ORDER BY child")
    return [r["child"] for r in rows]


def set_child_budget(name: str, daily: int, by_weekday: list[int]) -> None:
    name = norm_name(name)
    with db.tx() as c:
        c.execute(
            """INSERT INTO children(name, daily_budget, budget_by_weekday, updated_at) VALUES (?,?,?,?)
               ON CONFLICT(name) DO UPDATE SET daily_budget=excluded.daily_budget,
                   budget_by_weekday=excluded.budget_by_weekday, updated_at=excluded.updated_at""",
            (name, max(0, int(daily)), json.dumps(by_weekday or []), int(time.time())),
        )


def assign(device_id: str, child: str) -> str:
    """Gerät einem Kind zuordnen (leer = keinem). Gibt den gespeicherten Namen zurück.
    Gibt es das Kind noch nicht, übernimmt es das Limit dieses Geräts – nichts ändert sich überraschend."""
    from . import configbuilder
    child = norm_name(child)
    old = child_of(device_id)
    # Schreibweise eines vorhandenen Kindes übernehmen („renat" -> „Renat").
    if child:
        for existing in all_children():
            if existing.lower() == child.lower():
                child = existing
                break
        if get_child(child) is None:
            stored = configbuilder.get_stored(device_id)
            draft = stored[1] if stored else {}
            set_child_budget(child, int(draft.get("dailyBudgetMinutes") or 0), draft.get("dailyBudgetByWeekday") or [])
    with db.tx() as c:
        c.execute("UPDATE devices SET child=? WHERE id=?", (child or None, device_id))
    # Geräte neu versorgen: das Limit kommt jetzt vom Kind (bzw. wieder vom Gerät).
    bump = {device_id} | {r["id"] for r in members(child)} | {r["id"] for r in members(old)}
    for did in bump:
        configbuilder.bump_version(did)
    return child


def bump_child(name: str) -> None:
    from . import configbuilder
    for r in members(name):
        configbuilder.bump_version(r["id"])


def effective_budget(child: dict, weekday_py: int) -> int:
    """weekday_py: Python-Wochentag (0=Mo). Liste ist 0=So..6=Sa wie im Client."""
    wb = child.get("dailyBudgetByWeekday") or []
    if len(wb) == 7:
        v = int(wb[(weekday_py + 1) % 7] or 0)
        if v > 0:
            return v
    return int(child.get("dailyBudgetMinutes") or 0)


def usage_by_device(child: str, day: str) -> list[dict]:
    """Minuten je Gerät des Kindes an einem Tag (aus den Heartbeats)."""
    out = []
    for r in members(child):
        u = db.query_one("SELECT minutes FROM usage_daily WHERE device_id=? AND day=?", (r["id"], day))
        out.append({"id": r["id"], "name": r["name"], "platform": (r["platform"] or "windows"),
                    "minutes": int(u["minutes"]) if u else 0})
    return out


def others_today(device_id: str) -> tuple[str, int, str] | None:
    """(Tag, Minuten der anderen Geräte, Klartext) für das Gerät – None, wenn es kein gemeinsames Konto hat.
    Der Tag ist der, den das Gerät selbst zuletzt gemeldet hat (lokale Zeit des Kindes)."""
    child = child_of(device_id)
    if not child:
        return None
    dev = db.query_one("SELECT usage_day FROM devices WHERE id=?", (device_id,))
    day = (dev["usage_day"] if dev else "") or ""
    if not day:
        return None
    others = [u for u in usage_by_device(child, day) if u["id"] != device_id]
    if not others:
        return None
    total = sum(u["minutes"] for u in others)
    detail = ", ".join(f"{u['name']} {u['minutes']} Min" for u in others)
    return day, total, detail


def summary(child: str, day: str, weekday_py: int) -> dict | None:
    """Für das Eltern-Web: „Renat heute 75 von 120 Min (Laptop 45, Handy 30)"."""
    c = get_child(child)
    if not c:
        return None
    per = usage_by_device(c["name"], day)
    used = sum(u["minutes"] for u in per)
    budget = effective_budget(c, weekday_py)
    return {"name": c["name"], "used": used, "budget": budget, "devices": per,
            "left": max(0, budget - used) if budget > 0 else None,
            "over": budget > 0 and used >= budget,
            "daily": c["dailyBudgetMinutes"], "by_weekday": c["dailyBudgetByWeekday"]}
