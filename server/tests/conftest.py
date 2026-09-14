"""Stellt sicher, dass ein Dev-Signaturschlüssel existiert (privater Schlüssel ist NICHT im Repo)."""
import os
from pathlib import Path

from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric import rsa

KEYS_DIR = Path(__file__).resolve().parent.parent.parent / "tools" / "dev-keys"
PRIV = KEYS_DIR / "signing_private.pem"
PUB = KEYS_DIR / "signing_public.pem"


def _ensure_keys() -> None:
    KEYS_DIR.mkdir(parents=True, exist_ok=True)
    if PRIV.exists() and PUB.exists():
        return
    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    PRIV.write_bytes(key.private_bytes(
        serialization.Encoding.PEM,
        serialization.PrivateFormat.PKCS8,
        serialization.NoEncryption(),
    ))
    PUB.write_bytes(key.public_key().public_bytes(
        serialization.Encoding.PEM,
        serialization.PublicFormat.SubjectPublicKeyInfo,
    ))


_ensure_keys()
