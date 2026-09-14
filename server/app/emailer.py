"""E-Mail-Versand für den PIN-Reset. Ohne SMTP-Konfiguration wird der Link nur geloggt
(damit der Flow auch in Dev/ohne Mailserver testbar bleibt)."""
from __future__ import annotations

import logging
import smtplib
from email.message import EmailMessage

from .config import settings

log = logging.getLogger("frohlock.email")


def send(to_addr: str, subject: str, body: str) -> bool:
    if not settings.smtp_host:
        log.warning("SMTP nicht konfiguriert – E-Mail an %s NICHT gesendet.\nBetreff: %s\n%s",
                    to_addr, subject, body)
        return False
    try:
        msg = EmailMessage()
        msg["From"] = settings.smtp_from
        msg["To"] = to_addr
        msg["Subject"] = subject
        msg.set_content(body)

        with smtplib.SMTP(settings.smtp_host, settings.smtp_port, timeout=20) as s:
            if settings.smtp_starttls:
                s.starttls()
            if settings.smtp_user:
                s.login(settings.smtp_user, settings.smtp_password)
            s.send_message(msg)
        return True
    except Exception as exc:  # noqa: BLE001
        log.error("E-Mail-Versand an %s fehlgeschlagen: %s", to_addr, exc)
        return False


def send_reset_link(to_addr: str, reset_url: str, device_name: str) -> bool:
    subject = "FrohLock – PIN zurücksetzen"
    body = (
        f"Hallo,\n\n"
        f"für das Gerät \"{device_name}\" wurde ein PIN-Reset angefordert.\n"
        f"Über diesen Link kannst du einen neuen PIN vergeben (30 Minuten gültig):\n\n"
        f"{reset_url}\n\n"
        f"Wenn du das nicht warst, kannst du diese E-Mail ignorieren – es ändert sich nichts.\n\n"
        f"Viele Grüße\nFrohLock"
    )
    return send(to_addr, subject, body)
