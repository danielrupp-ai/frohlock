"""Android: APK-Bereitstellung, Update-Ziel und Einrichtungs-QR-Code (Device Owner)."""
from __future__ import annotations

import hashlib
import json
import os

from .config import settings

APK_NAME = "FrohLock.apk"
PACKAGE = "de.froehlichdienste.frohlock"
ADMIN_COMPONENT = f"{PACKAGE}/{PACKAGE}.android.AdminReceiver"
# Kurzform für den QR-Code (Android ergänzt das Paket vor dem Punkt) – jedes Zeichen macht den Code dichter.
ADMIN_COMPONENT_SHORT = f"{PACKAGE}/.android.AdminReceiver"

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


def short_download_url() -> str:
    """Kurzer APK-Link für den QR-Code (gleiche Datei wie /download/android)."""
    return settings.public_base_url.rstrip("/") + "/apk"


def install_link(code: str) -> str:
    """Link für den Weg ohne Zurücksetzen: Seite mit Download + „Jetzt koppeln“."""
    return settings.public_base_url.rstrip("/") + f"/i/{code}"


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
    """Inhalt des QR-Codes, den der Android-Einrichtungsassistent liest (6× auf den Willkommensbildschirm tippen).

    So KURZ wie möglich: Der QR-Leser des Einrichtungsassistenten ist einfach – ein dichter Code
    (früher ~715 Zeichen, 105×105 Punkte) ließ sich vom Bildschirm nicht scannen. Gerätename und
    Server kommen ohnehin vom Server bzw. sind in der App vorbelegt; nur der Kopplungscode zählt.
    """
    extras: dict = {"pairingCode": code}
    base = settings.public_base_url.rstrip("/")
    if base != "https://frohlock.froehlichdienste.de":
        extras["serverUrl"] = base
    payload: dict = {
        "android.app.extra.PROVISIONING_DEVICE_ADMIN_COMPONENT_NAME": ADMIN_COMPONENT_SHORT,
        "android.app.extra.PROVISIONING_DEVICE_ADMIN_PACKAGE_DOWNLOAD_LOCATION": short_download_url(),
        "android.app.extra.PROVISIONING_DEVICE_ADMIN_SIGNATURE_CHECKSUM": cert_checksum(),
        # Ohne das schaltet Android beim „ganzes Gerät verwalten" viele vorinstallierte Apps ab (Kamera, Galerie …).
        "android.app.extra.PROVISIONING_LEAVE_ALL_SYSTEM_APPS_ENABLED": True,
        "android.app.extra.PROVISIONING_ADMIN_EXTRAS_BUNDLE": extras,
    }
    if wifi_ssid.strip():
        payload["android.app.extra.PROVISIONING_WIFI_SSID"] = wifi_ssid.strip()
        if wifi_password:
            payload["android.app.extra.PROVISIONING_WIFI_PASSWORD"] = wifi_password
            payload["android.app.extra.PROVISIONING_WIFI_SECURITY_TYPE"] = wifi_security or "WPA"
        else:
            payload["android.app.extra.PROVISIONING_WIFI_SECURITY_TYPE"] = "NONE"
    return payload


def qr_svg(payload) -> str:
    """Schwarz auf Weiß, niedrige Fehlerkorrektur (Bildschirm-Scan braucht keine) = möglichst grobe Punkte.
    Größe steuert das CSS (viewBox), damit der Code auf jedem Bildschirm groß erscheint."""
    import segno
    data = payload if isinstance(payload, str) else json.dumps(payload, ensure_ascii=False, separators=(",", ":"))
    qr = segno.make(data, error="l", boost_error=False)
    return qr.svg_inline(scale=10, dark="#000000", light="#ffffff", border=4, omitsize=True)


def qr_version(payload: dict) -> int:
    import segno
    return segno.make(json.dumps(payload, ensure_ascii=False, separators=(",", ":")), error="l", boost_error=False).version


def device_info(raw) -> dict:
    try:
        return json.loads(raw) if raw else {}
    except ValueError:
        return {}
