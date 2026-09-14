"""Token- und Admin-Authentifizierung."""
from __future__ import annotations

import hashlib
import hmac
import secrets

from fastapi import HTTPException, Request

from . import db
from .config import settings

_PBKDF2_ITER = 210_000


def hash_token(token: str) -> str:
    return hashlib.sha256(token.encode("utf-8")).hexdigest()


def new_token() -> str:
    return secrets.token_urlsafe(32)


def hash_password(password: str, salt: str | None = None) -> str:
    salt = salt or secrets.token_hex(16)
    dk = hashlib.pbkdf2_hmac("sha256", password.encode(), bytes.fromhex(salt), _PBKDF2_ITER)
    return f"{salt}${dk.hex()}"


def verify_password(password: str, stored: str) -> bool:
    try:
        salt, _ = stored.split("$", 1)
        return hmac.compare_digest(hash_password(password, salt), stored)
    except Exception:
        return False


def pin_hash(pin: str, iterations: int = _PBKDF2_ITER) -> tuple[str, str, int]:
    """Erzeugt PIN-Hash im GLEICHEN Format wie FrohLock.Core.Crypto.PinHasher
    (PBKDF2-HMAC-SHA256, 16-Byte-Salt, 32-Byte-Hash, beides Base64)."""
    import base64
    import os
    salt = os.urandom(16)
    dk = hashlib.pbkdf2_hmac("sha256", pin.encode("utf-8"), salt, iterations, dklen=32)
    return base64.b64encode(dk).decode(), base64.b64encode(salt).decode(), iterations


def authed_device(request: Request) -> str:
    """Prüft Bearer-Token gegen die Geräte-Tabelle und gibt die device_id zurück."""
    auth = request.headers.get("authorization", "")
    if not auth.lower().startswith("bearer "):
        raise HTTPException(status_code=401, detail="Kein Token")
    token = auth[7:].strip()
    row = db.query_one("SELECT id FROM devices WHERE token_hash = ?", (hash_token(token),))
    if not row:
        raise HTTPException(status_code=401, detail="Ungültiges Token")
    return row["id"]


def require_admin(request: Request) -> str:
    user = request.session.get("admin_user") if hasattr(request, "session") else None
    if not user:
        raise HTTPException(status_code=302, headers={"Location": "/login"})
    return user
