"""RSA-Signatur der an Geräte ausgelieferten Nutzlasten.

Wichtig: Der Client verifiziert die Signatur über GENAU die Bytes, die wir in
`payloadBase64` legen (kein Re-Serialisieren). Deshalb ist die JSON-Form frei –
sie muss nur vom Client (System.Text.Json, camelCase, Enums als Integer erlaubt)
deserialisierbar sein. PSS-Saltlänge 32 passt zu .NET RSASignaturePadding.Pss.
"""
from __future__ import annotations

import base64
import json
import time
from functools import lru_cache

from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.asymmetric import padding
from cryptography.hazmat.primitives.serialization import load_pem_private_key

from .config import settings

ALGORITHM = "RSASSA-PSS-SHA256"


@lru_cache(maxsize=1)
def _private_key():
    with open(settings.signing_key_path, "rb") as fh:
        return load_pem_private_key(fh.read(), password=None)


def sign_bytes(data: bytes) -> str:
    sig = _private_key().sign(
        data,
        padding.PSS(mgf=padding.MGF1(hashes.SHA256()), salt_length=32),
        hashes.SHA256(),
    )
    return base64.b64encode(sig).decode("ascii")


def _canonical(obj: dict) -> bytes:
    return json.dumps(obj, separators=(",", ":"), ensure_ascii=False).encode("utf-8")


def make_envelope(payload: dict) -> dict:
    raw = _canonical(payload)
    return {
        "payloadBase64": base64.b64encode(raw).decode("ascii"),
        "signatureBase64": sign_bytes(raw),
        "algorithm": ALGORITHM,
    }


# ---- Enum-Werte (müssen zu FrohLock.Core.Models passen) ----
CMD = {
    "unlock": 0, "lock": 1, "updateConfig": 2, "resetPin": 3,
    "uninstall": 4, "update": 5, "ping": 6,
}


def sign_command(device_id: str, cmd_type: str, *, unlock_minutes: int | None = None,
                 payload: dict | None = None, expires_in: int = 3600) -> dict:
    now = int(time.time())
    body = {
        "commandId": _new_id(),
        "deviceId": device_id,
        "type": CMD[cmd_type],
        "issuedAtUnix": now,
        "expiresAtUnix": now + expires_in if expires_in else 0,
    }
    if unlock_minutes is not None:
        body["unlockMinutes"] = unlock_minutes
    if payload:
        body["payload"] = payload
    return {"commandId": body["commandId"], "envelope": make_envelope(body)}


def sign_update_manifest(version: str, sha256: str) -> str:
    return sign_bytes(f"{version}|{sha256}".encode("utf-8"))


def _new_id() -> str:
    import uuid
    return uuid.uuid4().hex
