"""Warn-E-Mails an die Eltern (seit 17.11.2026, Anlass: Renat löschte FrohLock am 16.11. unbemerkt).

1. Sofort: Das Handy meldet einen Löschversuch (Seite „Geräteadministrator“ geöffnet, Abschalten angefragt
   oder Administrator abgeschaltet). Höchstens eine E-Mail je Gerät alle 10 Minuten.
2. Wächter (alle 5 Min): Ein Android-Gerät meldet sich nicht mehr. War der Schutz zuletzt aus oder gab es
   einen Löschversuch → nach 10 Min „wahrscheinlich gelöscht“, sonst nach 3 Std „meldet sich nicht“.
   Einmal je Funkstille; meldet es sich wieder, kommt eine Entwarnung.

Empfänger: FROHLOCK_ALERT_EMAIL (kommagetrennt) + die beim Gerät hinterlegte Reset-E-Mail.
"""
from __future__ import annotations

import logging
import threading
import time

from . import android, db, emailer
from .config import settings

log = logging.getLogger("frohlock.alerts")

TAMPER_MARKERS = ("LÖSCHVERSUCH", "Geräteadministrator wurde abgeschaltet", "Geräteadministrator abschalten")
TAMPER_REPEAT = 600              # s
SILENT_AFTER_TAMPER = 600        # s
SILENT_AFTER = 3 * 3600          # s
ACTIVE_WITHIN = 14 * 86400       # s: ältere Geräte (längst ersetzt) nicht mehr überwachen
WATCH_EVERY = 300                # s


def recipients(dev) -> list[str]:
    out: list[str] = []
    raw = (settings.alert_email or "") + "," + ((dev["reset_email"] if "reset_email" in dev.keys() else "") or "")
    for a in raw.split(","):
        a = a.strip()
        if a and "@" in a and a.lower() not in [x.lower() for x in out]:
            out.append(a)
    return out


def _send_async(dev, subject: str, body: str) -> None:
    to = recipients(dev)
    if not to:
        log.warning("Keine Empfänger für Warnung %s: %s", dev["id"], subject)
        return

    def run():
        for addr in to:
            emailer.send(addr, subject, body)

    threading.Thread(target=run, daemon=True).start()


def _berlin(ts: int) -> str:
    try:
        from zoneinfo import ZoneInfo
        import datetime
        return datetime.datetime.fromtimestamp(ts, ZoneInfo("Europe/Berlin")).strftime("%d.%m. %H:%M")
    except Exception:  # noqa: BLE001
        return time.strftime("%d.%m. %H:%M", time.gmtime(ts))


def _link(device_id: str) -> str:
    return f"{settings.public_base_url.rstrip('/')}/admin/devices/{device_id}"


def _msg(line: str) -> str:
    return line.split("\t", 2)[-1].replace("⚠ ", "").strip()


def is_tamper(line: str) -> bool:
    return "\tUMGEHUNG\t" in line and any(m in line for m in TAMPER_MARKERS)


def _line_ts(line: str) -> int | None:
    import calendar
    try:
        return calendar.timegm(time.strptime(line.split("\t", 1)[0][:19], "%Y-%m-%dT%H:%M:%S"))
    except ValueError:
        return None


def on_new_lines(c, device_id: str, new_lines: list[str], now: int) -> None:
    """Aus dem Heartbeat (innerhalb der Transaktion): neue ⚠-Zeilen prüfen, bei Löschversuch E-Mail.
    Nur frische Zeilen (≤ 30 Min alt) – das Geräte-Protokoll schickt immer die letzten 40 Zeilen mit."""
    tamper = [ln for ln in new_lines if is_tamper(ln) and now - (_line_ts(ln) or 0) <= 1800]
    if not tamper:
        return
    dev = c.execute("SELECT * FROM devices WHERE id=?", (device_id,)).fetchone()
    if dev is None or now - (dev["tamper_alert_at"] or 0) < TAMPER_REPEAT:
        return
    c.execute("UPDATE devices SET tamper_alert_at=? WHERE id=?", (now, device_id))
    name = dev["name"] or "Gerät"
    body = (f"Hallo,\n\nauf „{name}“ wurde gerade versucht, FrohLock abzuschalten oder zu löschen:\n\n"
            + "\n".join(f"• {_msg(ln)}" for ln in tamper)
            + "\n\nFrohLock hat das Handy gesperrt. Es bleibt gesperrt, bis ihr die Eltern-PIN eingebt "
              "oder im Admin auf „Entsperren“ tippt.\n\n"
            f"Gerät im Admin: {_link(device_id)}\n\nViele Grüße\nFrohLock")
    _send_async(dev, f"⚠ FrohLock: Löschversuch auf „{name}“", body)


def on_heartbeat_seen(c, device_id: str, now: int) -> None:
    """Gerät meldet sich wieder, nachdem wir „meldet sich nicht“ geschickt hatten → Entwarnung."""
    dev = c.execute("SELECT * FROM devices WHERE id=?", (device_id,)).fetchone()
    if dev is None or not (dev["silence_alert_at"] or 0):
        return
    c.execute("UPDATE devices SET silence_alert_at=0 WHERE id=?", (device_id,))
    name = dev["name"] or "Gerät"
    _send_async(dev, f"✓ FrohLock: „{name}“ meldet sich wieder",
                f"Hallo,\n\n„{name}“ meldet sich seit {_berlin(now)} Uhr wieder – FrohLock läuft.\n\n"
                f"Gerät im Admin: {_link(device_id)}\n\nViele Grüße\nFrohLock")


def _tamper_before_silence(dev) -> bool:
    info = android.device_info(dev["device_info"])
    if info.get("deviceAdmin") is False and not dev["device_owner"]:
        return True
    tail = (dev["recent_audit"] or "").splitlines()[-6:]
    return any(is_tamper(ln) for ln in tail)


def check_silence(now: int | None = None) -> int:
    """Ein Durchlauf des Wächters. Gibt die Zahl der verschickten Warnungen zurück."""
    now = now or int(time.time())
    sent = 0
    rows = db.query("SELECT * FROM devices WHERE platform='android' AND last_seen IS NOT NULL AND last_seen > ?",
                    (now - ACTIVE_WITHIN,))
    for dev in rows:
        if dev["silence_alert_at"] and dev["silence_alert_at"] >= dev["last_seen"]:
            continue  # für diese Funkstille schon gewarnt
        silent = now - dev["last_seen"]
        tampered = _tamper_before_silence(dev)
        if silent < (SILENT_AFTER_TAMPER if tampered else SILENT_AFTER):
            continue
        name = dev["name"] or "Gerät"
        since = _berlin(dev["last_seen"])
        if tampered:
            subject = f"⚠ FrohLock wurde auf „{name}“ wahrscheinlich gelöscht"
            text = (f"„{name}“ meldet sich seit {since} Uhr nicht mehr, und davor war der Löschschutz abgeschaltet "
                    f"bzw. es gab einen Löschversuch. FrohLock wurde sehr wahrscheinlich gelöscht.\n\n"
                    f"So richtet ihr es wieder ein: Admin → „Android-Gerät einrichten“ → den angezeigten Link am Handy öffnen.")
            line_msg = f"⚠ Meldet sich seit {since} nicht mehr – FrohLock wurde wahrscheinlich gelöscht (E-Mail an die Eltern)"
        else:
            subject = f"⚠ FrohLock: „{name}“ meldet sich nicht mehr"
            text = (f"„{name}“ meldet sich seit {since} Uhr nicht mehr. Mögliche Gründe: Handy aus, kein Internet "
                    f"– oder FrohLock wurde gelöscht. Bitte kurz am Handy nachsehen.")
            line_msg = f"⚠ Meldet sich seit {since} nicht mehr (aus, kein Internet oder gelöscht) – E-Mail an die Eltern"
        with db.tx() as c:
            c.execute("UPDATE devices SET silence_alert_at=? WHERE id=?", (now, dev["id"]))
            ts = time.strftime("%Y-%m-%dT%H:%M:%S", time.gmtime(now)) + ".000000Z"
            from .api import _remember_gap_alerts
            _remember_gap_alerts(c, dev["id"], f"{ts}\tUMGEHUNG\t{line_msg}")
        _send_async(dev, subject, f"Hallo,\n\n{text}\n\nGerät im Admin: {_link(dev['id'])}\n\nViele Grüße\nFrohLock")
        db.audit("system", "alert-silent", dev["id"], subject)
        sent += 1
    return sent


def start_watchdog() -> None:
    if not settings.watchdog_enabled:
        return

    def loop():
        time.sleep(30)
        while True:
            try:
                check_silence()
            except Exception as exc:  # noqa: BLE001
                log.error("Wächter-Fehler: %s", exc)
            time.sleep(WATCH_EVERY)

    threading.Thread(target=loop, name="frohlock-watchdog", daemon=True).start()
