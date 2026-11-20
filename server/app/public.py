"""Öffentliche Seiten: Download-Landingpage + Auslieferung der Setup-.exe."""
from __future__ import annotations

import os
from pathlib import Path

from fastapi import APIRouter, Request
from fastapi.responses import FileResponse, HTMLResponse
from fastapi.templating import Jinja2Templates

from . import android, ios
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


@router.api_route("/media/{name}", methods=["GET", "HEAD"])
def media(name: str):
    """Videos zu Eltern-Nachrichten (zufälliger Dateiname, nur .mp4 aus download/media)."""
    import re
    if not re.fullmatch(r"[A-Za-z0-9_-]{16,80}\.mp4", name):
        return HTMLResponse("Nicht gefunden", status_code=404)
    path = os.path.join(settings.download_dir, "media", name)
    if not os.path.exists(path):
        return HTMLResponse("Nicht gefunden", status_code=404)
    return FileResponse(path, media_type="video/mp4")


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


# ---------------- iPhone (ohne App Store) ----------------

def _ios_code_state(code: str):
    import time
    from . import db
    row = db.query_one("SELECT device_name, expires_at, used_device_id FROM pairing_codes WHERE code=?", (code,))
    if not row:
        return None, "unknown"
    if row["used_device_id"]:
        return row, "used"
    if row["expires_at"] < int(time.time()):
        return row, "expired"
    return row, "ok"


@router.get("/ios", response_class=HTMLResponse)
def ios_page_generic(request: Request):
    """Für Updates: App neu installieren (gleiche Seite ohne Anmeldung/Koppeln)."""
    return _ios_page(request, "")


@router.get("/ios/manifest.plist")
def ios_manifest():
    from fastapi.responses import Response
    if not ios.version():
        return HTMLResponse("Nicht verfügbar", status_code=404)
    return Response(ios.manifest_plist(), media_type="application/xml", headers={"Cache-Control": "no-store"})


@router.api_route("/ios/FrohLock.ipa", methods=["GET", "HEAD"])
def ios_ipa():
    if not os.path.exists(ios.ipa_path()):
        return HTMLResponse("Nicht verfügbar", status_code=404)
    return FileResponse(ios.ipa_path(), media_type="application/octet-stream", filename="FrohLock.ipa",
                        headers={"Cache-Control": "no-store"})


@router.get("/ios/icon.png")
def ios_icon():
    path = os.path.join(ios.base_dir(), "icon.png")
    if not os.path.exists(path):
        return HTMLResponse("Nicht gefunden", status_code=404)
    return FileResponse(path, media_type="image/png")


@router.get("/ios/{code}", response_class=HTMLResponse)
def ios_page(request: Request, code: str):
    return _ios_page(request, "".join(ch for ch in code if ch.isdigit())[:6])


def _ios_page(request: Request, code: str):
    row, state = _ios_code_state(code) if code else (None, "update")
    udid = (request.query_params.get("u") or request.cookies.get("fl_udid") or "").strip().upper()
    if udid and not ios.UDID_RE.match(udid):
        udid = ""
    resp = templates.TemplateResponse("ios_install.html", {
        "request": request, "code": code, "state": state, "name": row["device_name"] if row else "",
        "udid": udid, "built": ios.is_built_for(udid), "version": ios.version(),
        "install_url": ios.install_url(), "pair_link": f"frohlock://koppeln?code={code}",
        "profile_url": f"/ios/{code}/anmelden.mobileconfig" if code else "",
    })
    if request.query_params.get("u") and udid:
        resp.set_cookie("fl_udid", udid, max_age=180 * 86400, secure=True, httponly=True, samesite="lax")
    return resp


@router.get("/ios/{code}/anmelden.mobileconfig")
def ios_enroll_profile(code: str):
    from fastapi.responses import Response
    code = "".join(ch for ch in code if ch.isdigit())[:6]
    row, state = _ios_code_state(code)
    if state != "ok":
        return HTMLResponse("Code ungültig oder abgelaufen", status_code=404)
    return Response(ios.enroll_profile(code), media_type="application/x-apple-aspen-config",
                    headers={"Content-Disposition": 'attachment; filename="FrohLock-anmelden.mobileconfig"'})


@router.post("/ios/{code}/udid")
async def ios_enroll_receive(request: Request, code: str):
    """iOS schickt hierher die Geräte-Daten und öffnet danach die Weiterleitung in Safari."""
    from fastapi.responses import RedirectResponse
    code = "".join(ch for ch in code if ch.isdigit())[:6]
    body = await request.body()
    _, state = _ios_code_state(code)
    udid = ios.remember(code, ios.parse_device_attributes(body[:200_000])) if state == "ok" else None
    target = f"/ios/{code}" + (f"?u={udid}" if udid else "?fehler=1")
    return RedirectResponse(ios.public(target), status_code=301)
