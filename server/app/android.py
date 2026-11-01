"""Android: APK-Bereitstellung, Update-Ziel und Einrichtungs-QR-Code (Device Owner)."""
from __future__ import annotations

import hashlib
import json
import os

from .config import settings

APK_NAME = "FrohLock.apk"
PACKAGE = "de.froehlichdienste.frohlock"
ADMIN_COMPONENT = f"{PACKAGE}/{PACKAGE}.android.AdminReceiver"

_sha_cache: dict[str, tuple[float, int, str]] = {}


def apk_path() -> str:
    return os.path.join(settings.download_dir, APK_NAME)


def _read(name: str) -> str:
    try:
        with open(os.path.join(settings.download_dir, name)) as fh:
            return fh.read().strip()
    except OSError:
        return ""


def apk_sha256() -> str:
    """SHA-256 des bereitgestellten APK (gecacht nach Änderungszeit + Größe)."""
    if settings.android_update_sha256:
        return settings.android_update_sha256.lower()
    path = apk_path()
    try:
        st = os.stat(path)
    except OSError:
        return ""
    cached = _sha_cache.get(path)
    if cached and cached[0] == st.st_mtime and cached[1] == st.st_size:
        return cached[2]
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    digest = h.hexdigest()
    _sha_cache[path] = (st.st_mtime, st.st_size, digest)
    return digest


def apk_version() -> str:
    return settings.android_update_version or _read("android-version.txt")


def cert_checksum() -> str:
    return settings.android_cert_checksum or _read("android-cert.txt")


def download_url() -> str:
    return settings.public_base_url.rstrip("/") + "/download/android"


def update_target() -> tuple[str, str, str] | None:
    """(Version, URL, SHA-256) des aktuellen Android-Updates oder None."""
    ver, sha = apk_version(), apk_sha256()
    if not ver or not sha:
        return None
    return ver, download_url(), sha


def manifest_message(version: str, sha256: str) -> bytes:
    """Was signiert wird – mit Plattform-Präfix, damit ein Windows-Manifest nie als Android-Manifest gilt."""
    return f"android|{version}|{sha256}".encode("utf-8")


def provisioning_payload(code: str, device_name: str, wifi_ssid: str = "", wifi_password: str = "",
                         wifi_security: str = "WPA") -> dict:
    """Inhalt des QR-Codes, den der Android-Einrichtungsassistent liest (6× auf den Willkommensbildschirm tippen)."""
    payload: dict = {
        "android.app.extra.PROVISIONING_DEVICE_ADMIN_COMPONENT_NAME": ADMIN_COMPONENT,
        "android.app.extra.PROVISIONING_DEVICE_ADMIN_PACKAGE_DOWNLOAD_LOCATION": download_url(),
        "android.app.extra.PROVISIONING_DEVICE_ADMIN_SIGNATURE_CHECKSUM": cert_checksum(),
        "android.app.extra.PROVISIONING_LEAVE_ALL_SYSTEM_APPS_ENABLED": True,
        "android.app.extra.PROVISIONING_LOCALE": "de_DE",
        "android.app.extra.PROVISIONING_TIME_ZONE": "Europe/Berlin",
        "android.app.extra.PROVISIONING_ADMIN_EXTRAS_BUNDLE": {
            "pairingCode": code,
            "serverUrl": settings.public_base_url.rstrip("/"),
            "deviceName": device_name,
        },
    }
    if wifi_ssid.strip():
        payload["android.app.extra.PROVISIONING_WIFI_SSID"] = wifi_ssid.strip()
        if wifi_password:
            payload["android.app.extra.PROVISIONING_WIFI_PASSWORD"] = wifi_password
            payload["android.app.extra.PROVISIONING_WIFI_SECURITY_TYPE"] = wifi_security or "WPA"
        else:
            payload["android.app.extra.PROVISIONING_WIFI_SECURITY_TYPE"] = "NONE"
    return payload


def qr_svg(payload: dict) -> str:
    import segno
    data = json.dumps(payload, ensure_ascii=False, separators=(",", ":"))
    qr = segno.make(data, error="m")
    return qr.svg_inline(scale=5, dark="#0b1f3a", light="#ffffff", border=3)


def device_info(raw) -> dict:
    try:
        return json.loads(raw) if raw else {}
    except ValueError:
        return {}
