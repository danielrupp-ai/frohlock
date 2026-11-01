"""Öffentliche Seiten: Download-Landingpage + Auslieferung der Setup-.exe."""
from __future__ import annotations

import os
from pathlib import Path

from fastapi import APIRouter, Request
from fastapi.responses import FileResponse, HTMLResponse
from fastapi.templating import Jinja2Templates

from . import android
from .config import settings

router = APIRouter()
templates = Jinja2Templates(directory=str(Path(__file__).parent / "templates"))

INSTALLER_NAME = "FrohLockSetup.exe"


def _installer_path() -> str:
    return os.path.join(settings.download_dir, INSTALLER_NAME)


def _version() -> str:
    try:
        with open(os.path.join(settings.download_dir, "version.txt")) as fh:
            return fh.read().strip()
    except Exception:
        return ""


def _size_mb() -> int:
    try:
        return round(os.path.getsize(_installer_path()) / (1024 * 1024))
    except Exception:
        return 0


@router.get("/", response_class=HTMLResponse)
def landing(request: Request):
    return templates.TemplateResponse("landing.html", {
        "request": request,
        "version": _version(),
        "size_mb": _size_mb(),
        "available": os.path.exists(_installer_path()),
        "android_available": os.path.exists(android.apk_path()),
        "android_version": android.apk_version(),
    })


@router.get("/download")
def download():
    path = _installer_path()
    if not os.path.exists(path):
        return HTMLResponse("<h1>Download derzeit nicht verfügbar</h1>", status_code=404)
    return FileResponse(path, media_type="application/octet-stream", filename=INSTALLER_NAME)


@router.api_route("/apk", methods=["GET", "HEAD"])
@router.api_route("/download/android", methods=["GET", "HEAD"])
def download_android():
    """APK für Android – lädt der Einrichtungsassistent nach dem QR-Code selbst herunter."""
    path = android.apk_path()
    if not os.path.exists(path):
        return HTMLResponse("<h1>Android-App derzeit nicht verfügbar</h1>", status_code=404)
    return FileResponse(path, media_type="application/vnd.android.package-archive", filename=android.APK_NAME)


@router.get("/i/{code}", response_class=HTMLResponse)
def install_page(request: Request, code: str):
    """Installations-Seite für den Weg OHNE Zurücksetzen: am Kinder-Handy im Browser öffnen,
    App laden, dann „Jetzt koppeln“ – der Code wird an die App übergeben."""
    import time
    from . import db
    clean = "".join(ch for ch in code if ch.isdigit())[:6]
    row = db.query_one("SELECT device_name, expires_at, used_device_id FROM pairing_codes WHERE code=?", (clean,))
    state = "ok"
    if not row:
        state = "unknown"
    elif row["used_device_id"]:
        state = "used"
    elif row["expires_at"] < int(time.time()):
        state = "expired"
    return templates.TemplateResponse("install.html", {
        "request": request, "code": clean, "state": state,
        "name": row["device_name"] if row else "",
        "available": os.path.exists(android.apk_path()), "version": android.apk_version(),
        "pair_link": f"frohlock://koppeln?code={clean}",
    })
