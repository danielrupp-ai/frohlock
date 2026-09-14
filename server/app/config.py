"""Server-Konfiguration aus Umgebungsvariablen (mit sicheren Dev-Defaults)."""
from __future__ import annotations

import os
import secrets
from pathlib import Path

BASE_DIR = Path(__file__).resolve().parent.parent          # server/
REPO_DIR = BASE_DIR.parent                                  # frohlock/


class Settings:
    def __init__(self) -> None:
        self.db_path: str = os.getenv("FROHLOCK_DB", str(BASE_DIR / "app.db"))

        # Verzeichnis, aus dem die Setup-.exe öffentlich ausgeliefert wird.
        self.download_dir: str = os.getenv("FROHLOCK_DOWNLOAD_DIR", "/data/download")

        # Privater Signaturschlüssel – NUR auf dem Server. Dev-Default = tools/dev-keys.
        self.signing_key_path: str = os.getenv(
            "FROHLOCK_SIGNING_KEY", str(REPO_DIR / "tools" / "dev-keys" / "signing_private.pem")
        )

        # Basis-URL, die den Geräten in die Config geschrieben wird.
        self.public_base_url: str = os.getenv("FROHLOCK_PUBLIC_URL", "http://localhost:8080")

        # TLS-SPKI-Pins (Base64, kommagetrennt), die den Geräten mitgegeben werden. Leer = kein Pinning (Dev).
        pins = os.getenv("FROHLOCK_TLS_PINS", "").strip()
        self.tls_pins: list[str] = [p.strip() for p in pins.split(",") if p.strip()]

        # Admin-Zugang. Passwort-Hash wird beim ersten Start aus FROHLOCK_ADMIN_PASSWORD erzeugt.
        self.admin_user: str = os.getenv("FROHLOCK_ADMIN_USER", "admin")
        self.admin_password: str = os.getenv("FROHLOCK_ADMIN_PASSWORD", "frohlock-admin")

        # Session-Signaturschlüssel für das Admin-Cookie.
        self.session_secret: str = os.getenv("FROHLOCK_SESSION_SECRET", secrets.token_hex(32))

        # E-Mail-Versand (PIN-Reset). Ohne Host wird der Link nur geloggt.
        self.smtp_host: str = os.getenv("FROHLOCK_SMTP_HOST", "")
        self.smtp_port: int = int(os.getenv("FROHLOCK_SMTP_PORT", "587"))
        self.smtp_user: str = os.getenv("FROHLOCK_SMTP_USER", "")
        self.smtp_password: str = os.getenv("FROHLOCK_SMTP_PASSWORD", "")
        self.smtp_from: str = os.getenv("FROHLOCK_SMTP_FROM", "FrohLock <noreply@froehlichdienste.de>")
        self.smtp_starttls: bool = os.getenv("FROHLOCK_SMTP_STARTTLS", "1") not in ("0", "false", "")

        # Aktuelles Client-Update.
        self.update_version: str = os.getenv("FROHLOCK_UPDATE_VERSION", "")
        self.update_url: str = os.getenv("FROHLOCK_UPDATE_URL", "")
        self.update_sha256: str = os.getenv("FROHLOCK_UPDATE_SHA256", "")


settings = Settings()
